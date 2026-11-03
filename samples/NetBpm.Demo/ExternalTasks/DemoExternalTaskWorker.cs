using NetBpm.Engine.Jobs;
using NetBpm.Runtime;

namespace NetBpm.Demo.ExternalTasks;

/// <summary>
/// 进程内的外部任务 worker：定时「领取 → 执行 handler → 回执」，把
/// <see cref="IExternalTaskHandler"/> 的注册真正跑起来。
/// <para>
/// ⚠️ 真实部署里 worker 是<b>客户系统的独立进程</b>，用 HTTP 拉任务
/// （`samples/NetBpm.ExternalWorker` 就是那个形态）；Demo 把它放进同一宿主，
/// 只为让「注册 + 调用」在一台机器上就能完整跑通。
/// </para>
/// <para>
/// 🔑 语义要点（与 HTTP worker 完全一致）：workerId 稳定、领取时拿锁、成功回执才推进流程、
/// 失败回执只扣重试次数、异常一律转成失败回执（不许把任务"吞掉"等锁过期）。
/// </para>
/// </summary>
public sealed class DemoExternalTaskWorker : BackgroundService
{
    /// <summary>锁持有者标识：稳定且唯一（换 id 等于放弃已领任务，只能等锁过期）。</summary>
    public const string WorkerId = "demo-inproc-worker";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>单条任务持锁时长：Demo 的 handler 都是纯计算，60 秒足够；真实场景按处理耗时给。</summary>
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(60);

    private const int MaxTasksPerPoll = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyList<IExternalTaskHandler> _handlers;
    private readonly string[] _topics;
    private readonly ILogger<DemoExternalTaskWorker> _logger;

    private volatile bool _paused;

    /// <summary>注册与暂停状态由 DI 提供；handler 用单例，避免每次轮询重建。</summary>
    public DemoExternalTaskWorker(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IExternalTaskHandler> handlers,
        ILogger<DemoExternalTaskWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _handlers = handlers.ToArray();
        _topics = _handlers.Select(handler => handler.Topic).Distinct(StringComparer.Ordinal).ToArray();
        _logger = logger;
    }

    /// <summary>订阅的主题（= 已注册 handler 的 topic 去重后的集合）。</summary>
    public IReadOnlyList<string> Topics => _topics;

    /// <summary>是否已暂停（用例要观察"任务登记后原地等待"的中间态时会临时暂停）。</summary>
    public bool Paused => _paused;

    /// <summary>暂停领取：已在处理的任务照常回执，只是不再领新任务。</summary>
    public void Pause() => _paused = true;

    /// <summary>恢复领取。</summary>
    public void Resume() => _paused = false;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "外部任务 worker 已启动：workerId={WorkerId}，订阅 {Topics}",
            WorkerId,
            string.Join(", ", _topics));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_paused && _topics.Length > 0)
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 轮询整体失败（库抖动等）：不退出，下一秒重试；已领任务会随锁过期被重新领取
                _logger.LogWarning(ex, "外部任务轮询失败，{Delay} 后重试", PollInterval);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>领一轮：每条任务都走「执行 handler → 成功回执 / 失败回执」。</summary>
    private async Task PollOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // IProcessEngine 是 scoped：后台线程不能用请求作用域，必须自己开一个
        var engine = scope.ServiceProvider.GetRequiredService<IProcessEngine>();

        var tasks = await engine
            .FetchExternalTasksAsync(WorkerId, _topics, MaxTasksPerPoll, LockDuration, tenantId: null, ct)
            .ConfigureAwait(false);

        foreach (var task in tasks)
        {
            await HandleAsync(engine, task, ct).ConfigureAwait(false);
        }
    }

    /// <summary>执行单条任务并回执。</summary>
    private async Task HandleAsync(IProcessEngine engine, ExternalTask task, CancellationToken ct)
    {
        var handler = _handlers.FirstOrDefault(
            candidate => string.Equals(candidate.Topic, task.Topic, StringComparison.Ordinal));

        if (handler is null)
        {
            // 领到没注册 handler 的主题：立刻失败回执，别占着锁等过期
            await FailAsync(engine, task.Id, $"没有为 topic '{task.Topic}' 注册 IExternalTaskHandler", ct)
                .ConfigureAwait(false);

            return;
        }

        try
        {
            var invocation = new ExternalTaskInvocation(
                task.Id,
                task.ProcessInstanceId,
                task.ActivityId,
                task.BusinessKey,
                new Dictionary<string, object?>(task.Payload, StringComparer.Ordinal));

            var variables = await handler.ExecuteAsync(invocation, ct).ConfigureAwait(false);

            await engine
                .CompleteExternalTaskAsync(
                    task.Id,
                    WorkerId,
                    variables is null
                        ? new Dictionary<string, object?>(StringComparer.Ordinal)
                        : new Dictionary<string, object?>(variables, StringComparer.Ordinal),
                    tenantId: null,
                    commandId: null,
                    ct)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "外部任务已回执：任务 {TaskId}（{Topic}）→ 写回 {Count} 个变量",
                task.Id,
                task.Topic,
                variables?.Count ?? 0);
        }
        catch (Exception ex)
        {
            // 业务异常 → 失败回执（扣一次重试、按退避下次再领）；流程原地不动
            await FailAsync(engine, task.Id, ex.Message, ct, ex).ConfigureAwait(false);
        }
    }

    /// <summary>失败回执；失败回执本身再失败（例如锁已被别人拿走）只记日志。</summary>
    private async Task FailAsync(
        IProcessEngine engine,
        string taskId,
        string message,
        CancellationToken ct,
        Exception? error = null)
    {
        try
        {
            await engine
                .FailExternalTaskAsync(
                    taskId,
                    WorkerId,
                    new ExternalTaskFailure(message, error?.ToString()),
                    tenantId: null,
                    commandId: null,
                    ct)
                .ConfigureAwait(false);

            _logger.LogWarning("外部任务失败回执：任务 {TaskId}，原因：{Message}", taskId, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "外部任务失败回执本身失败：任务 {TaskId}", taskId);
        }
    }
}
