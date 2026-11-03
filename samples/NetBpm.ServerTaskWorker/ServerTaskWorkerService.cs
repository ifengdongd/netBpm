using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// worker 主循环：领取 → 处理 → 回执。
/// </summary>
/// <remarks>
/// <para>三条不能妥协的规则（踩错任何一条都会表现成「流程卡住」）：</para>
/// <list type="number">
/// <item>回执必须用<b>领取时</b>的 <c>workerId</c>（锁按 id 归属，换了 id 一律 409）；</item>
/// <item>业务成功但回执失败时，<b>绝不能补发 failure</b>（业务已落库，补 failure 会把它记成失败并扣重试）；</item>
/// <item>处理失败走 <c>failure</c>，不走 <c>unlock</c>（unlock 等于「我没看见过」，会把失败原因抹掉）。</item>
/// </list>
/// </remarks>
public sealed class ServerTaskWorkerService : BackgroundService
{
    private readonly NetBpmClient _client;
    private readonly ServerTaskDispatcher _dispatcher;
    private readonly WorkerOptions _options;
    private readonly WorkerRuntimeState _state;
    private readonly ILogger<ServerTaskWorkerService> _logger;

    private int _consecutiveErrors;

    /// <summary>构造。</summary>
    /// <param name="client">协议客户端。</param>
    /// <param name="dispatcher">topic 分派器。</param>
    /// <param name="options">运行参数。</param>
    /// <param name="state">运行态。</param>
    /// <param name="logger">日志。</param>
    public ServerTaskWorkerService(
        NetBpmClient client,
        ServerTaskDispatcher dispatcher,
        WorkerOptions options,
        WorkerRuntimeState state,
        ILogger<ServerTaskWorkerService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _dispatcher = dispatcher;
        _options = options;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[worker] 启动 workerId={WorkerId} topics={Topics} 引擎={Engine} 租户={Tenant}",
            _options.WorkerId,
            string.Join(",", _options.Topics),
            _options.NormalizedBaseUrl,
            _options.TenantId);

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<ExternalTask> tasks;

            try
            {
                tasks = await _client.FetchAsync(stoppingToken).ConfigureAwait(false);

                _consecutiveErrors = 0;
                _state.MarkPoll(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await BackoffAsync(exception, stoppingToken).ConfigureAwait(false);
                continue;
            }

            foreach (var task in tasks)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // 已领到但决定不处理：立刻还回去，别让锁白占到过期。
                    await TryUnlockAsync(task, CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                await HandleAsync(task, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("[worker] 已停止：{WorkerId}", _options.WorkerId);
    }

    /// <summary>引擎不可达 / 凭据错 / 限流：指数退避，别把日志打满，也别退出进程。</summary>
    private async Task BackoffAsync(Exception exception, CancellationToken ct)
    {
        _consecutiveErrors++;

        var seconds = Math.Min(1 << Math.Min(_consecutiveErrors, 10), _options.ErrorBackoffMaxSeconds);

        _logger.LogError(
            "[worker] 领取失败（连续 {Count} 次）：{Message}；{Seconds}s 后重试",
            _consecutiveErrors,
            exception.Message,
            seconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机中，交给外层 while 判断
        }
    }

    private async Task HandleAsync(ExternalTask task, CancellationToken stoppingToken)
    {
        var startedAt = Stopwatch.GetTimestamp();

        IReadOnlyDictionary<string, object?> variables;

        try
        {
            // 超时按「可重试失败」处理：可能是下游慢，不是数据错。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(_options.TaskTimeoutMs);

            variables = await _dispatcher
                .DispatchAsync(task.Topic, task, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await ReportFailureAsync(
                    task,
                    new BusinessRuleException($"业务处理超时（{_options.TaskTimeoutMs}ms）", retryable: true),
                    startedAt,
                    CancellationToken.None)
                .ConfigureAwait(false);

            return;
        }
        catch (OperationCanceledException)
        {
            // 停机中：不回执，也不 unlock（可能已经改了一半数据），让锁自然过期后重领。
            return;
        }
        catch (Exception exception)
        {
            await ReportFailureAsync(task, exception, startedAt, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            // 回执一旦发出就必须送完：这里不跟随停机令牌，否则「业务已落库、回执被取消」= 白做一遍。
            await _client.CompleteAsync(task.Id, variables, CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "[worker] 完成 taskId={TaskId} topic={Topic} instance={InstanceId} 耗时={Elapsed}ms 变量={Variables}",
                task.Id,
                task.Topic,
                task.ProcessInstanceId,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                JsonSerializer.Serialize(variables, WorkerJson.Options));
        }
        catch (NetBpmApiException exception) when (exception.IsLockConflict)
        {
            // 锁过期被别的 worker 领走了：对方也会做一遍，业务幂等兜底，这里不需要补救。
            _logger.LogWarning(
                "[worker] 回执时锁已易主，放弃 taskId={TaskId} topic={Topic}：{Message}",
                task.Id,
                task.Topic,
                exception.Message);
        }
        catch (Exception exception)
        {
            // 业务已成功、回执没送到 —— 绝不能补发 failure（见类注释第 2 条）。
            _logger.LogError(
                exception,
                "[worker] 回执失败 taskId={TaskId} topic={Topic}：{Message}；锁到期后会被重新领取，依赖业务幂等去重",
                task.Id,
                task.Topic,
                exception.Message);
        }
    }

    private async Task ReportFailureAsync(
        ExternalTask task,
        Exception exception,
        long startedAt,
        CancellationToken ct)
    {
        var rule = exception as BusinessRuleException;
        var retryable = rule?.Retryable ?? true;

        // 不可恢复：直接 0 次重试进死信，把「再给几次机会」的额度留给偶发故障。
        int? retries = retryable ? null : 0;

        var errorMessage = $"{task.Topic} 处理失败：{exception.Message}";
        var errorDetails = BuildErrorDetails(exception);

        try
        {
            await _client
                .FailAsync(task.Id, Truncate(errorMessage, 900), errorDetails, retries, ct)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "[worker] 回执失败 taskId={TaskId} topic={Topic} instance={InstanceId} retryable={Retryable} "
                + "剩余重试={Retries} 耗时={Elapsed}ms 原因={Reason}",
                task.Id,
                task.Topic,
                task.ProcessInstanceId,
                retryable,
                retries?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ?? $"{task.Retries - 1}（自动扣减）",
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                exception.Message);
        }
        catch (NetBpmApiException apiException) when (apiException.IsLockConflict)
        {
            _logger.LogWarning(
                "[worker] 回执失败时锁已易主，放弃 taskId={TaskId}：{Message}",
                task.Id,
                apiException.Message);
        }
        catch (Exception failException)
        {
            // 连失败回执都送不到：锁到期后引擎会重投并扣重试，这里只能留痕。
            _logger.LogError(
                failException,
                "[worker] 失败回执未能送达 taskId={TaskId}：{Message}",
                task.Id,
                failException.Message);
        }
    }

    private async Task TryUnlockAsync(ExternalTask task, CancellationToken ct)
    {
        try
        {
            await _client.UnlockAsync(task.Id, ct).ConfigureAwait(false);

            _logger.LogInformation("[worker] 停机归还任务 taskId={TaskId}", task.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "[worker] 归还任务失败 taskId={TaskId}，锁到期后自动回可领取。",
                task.Id);
        }
    }

    /// <summary>
    /// 组装 <c>errorDetails</c>：业务详情 + 异常名 + 栈顶几行。
    /// </summary>
    /// <remarks>
    /// 栈只取前 8 行：死信页面是给人看的，整段栈既存不下也没人读。
    /// </remarks>
    private static string BuildErrorDetails(Exception exception)
    {
        var node = new JsonObject
        {
            ["name"] = exception.GetType().Name,
            ["stack"] = string.Join(
                '\n',
                (exception.StackTrace ?? string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Take(8)),
        };

        if (exception is BusinessRuleException { Details: not null } rule)
        {
            node["details"] = JsonSerializer.SerializeToNode(rule.Details, WorkerJson.Options);
        }

        return node.ToJsonString();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
