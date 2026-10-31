using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Diagnostics;

namespace NetBpm.Runtime.Hosting;

/// <summary>
/// 外部任务巡检选项（<c>NetBpm:ExternalTasks:Watchdog</c>）。
/// </summary>
public sealed class ExternalTaskWatchdogOptions
{
    /// <summary>是否启用巡检（默认开）。关掉则完全不发告警。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动后先缓一缓再开始扫，避免宿主刚起来时把存量任务全报一遍（默认 30 秒）。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>扫描间隔（默认 60 秒）。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 「登记后多久还没人领」算异常（默认 5 分钟）。
    /// <para>别调太小：worker 通常是批量轮询，刚登记的头几秒没人领完全正常。</para>
    /// </summary>
    public TimeSpan NoWorkerThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 同一条任务同一类告警的冷却时间（默认 15 分钟）。
    /// <para>没有它，一条卡死的任务会在每个扫描周期告警一次，把告警通道彻底淹没。</para>
    /// </summary>
    public TimeSpan AlertCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>死信也告警（默认开）：死信意味着流程停在原地，必须有人来决定重投还是放弃。</summary>
    public bool AlertOnDeadLetter { get; set; } = true;

    /// <summary>每轮最多扫多少条（默认 500）。</summary>
    public int MaxResults { get; set; } = 500;
}

/// <summary>
/// 外部任务巡检：找出<b>没人来领</b>与<b>进了死信</b>的外部任务并发告警。
/// <para>
/// 为什么必须单独有它：外部任务的失败不会体现在任何一次请求的返回码上。worker 全挂了，
/// fetch 接口只会「正常返回空列表」；任务进了死信，也没有任何调用会报错 ——
/// 流程就那么静静地卡在 <c>serviceTask</c> 上，直到有人去看数据库才发现。
/// 这个服务就是把「去看数据库」这件事自动化。
/// </para>
/// <para>
/// 🔑 <b>只告警，不动数据</b>：它不重投、不推进、不改状态。处置是人的决定
/// （重投端点 <c>POST /external-tasks/{id}/retry</c> 就是给这时用的），
/// 自动处置会把「worker 有 bug 反复失败」掩盖成「看起来在重试」。
/// </para>
/// <para>
/// 告警按 <c>告警码 + 任务 id</c> 冷却去重：一条卡死的任务每轮都报会把告警通道淹掉，
/// 反而让人看不到真正的新问题。
/// </para>
/// </summary>
public sealed class ExternalTaskWatchdogService : BackgroundService
{
    private readonly IExternalTaskBroker _broker;
    private readonly IEnumerable<IAlertSink> _sinks;
    private readonly ExternalTaskWatchdogOptions _options;
    private readonly ILogger<ExternalTaskWatchdogService> _logger;

    /// <summary>已告警条目 → 上次告警时间（UTC），用于冷却去重。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastAlerted = new(StringComparer.Ordinal);

    public ExternalTaskWatchdogService(
        IExternalTaskBroker broker,
        IEnumerable<IAlertSink> sinks,
        IOptions<ExternalTaskWatchdogOptions> options,
        ILogger<ExternalTaskWatchdogService> logger)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _broker = broker;
        _sinks = sinks;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("外部任务巡检已禁用（NetBpm:ExternalTasks:Watchdog:Enabled=false）。");
            return;
        }

        if (!_sinks.Any())
        {
            _logger.LogWarning("外部任务巡检没有注册任何 IAlertSink：发现异常也无处可报。");
            return;
        }

        try
        {
            await Task.Delay(_options.StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "外部任务巡检扫描失败，将在下一轮重试。");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>扫一轮：先查「没人领」，再查「死信」。</summary>
    private async Task ScanOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        var limit = Math.Clamp(_options.MaxResults, 1, 1000);

        var pending = await _broker.FindAsync(
            new ExternalTaskQuery(Status: ExternalTaskStatus.Pending, MaxResults: limit),
            ct).ConfigureAwait(false);

        foreach (var task in pending.Where(task => IsStarving(task, nowUtc)))
        {
            await RaiseAsync(
                AlertCodes.ExternalTaskNoWorker,
                AlertSeverity.Critical,
                task,
                nowUtc,
                $"外部任务 '{task.Id}'（topic={task.Topic}）自 {task.CreatedAt:O} 登记后"
                    + $" {(nowUtc - task.CreatedAt).TotalMinutes:F0} 分钟无人领取，流程停在节点 '{task.ActivityId}'。",
                ct).ConfigureAwait(false);
        }

        if (!_options.AlertOnDeadLetter)
        {
            return;
        }

        var dead = await _broker.FindAsync(
            new ExternalTaskQuery(Status: ExternalTaskStatus.DeadLetter, MaxResults: limit),
            ct).ConfigureAwait(false);

        foreach (var task in dead)
        {
            await RaiseAsync(
                AlertCodes.ExternalTaskDeadLetter,
                AlertSeverity.Critical,
                task,
                nowUtc,
                $"外部任务 '{task.Id}'（topic={task.Topic}）重试已耗尽进入死信，"
                    + $"流程 '{task.ProcessInstanceId}' 停在节点 '{task.ActivityId}'，需人工重投或放弃。",
                ct).ConfigureAwait(false);
        }

        PruneCooldown(nowUtc);
    }

    /// <summary>
    /// 是否「饿死」：登记后一直是待领取、从来没人持有过锁、也没失败过（失败过的走死信告警），
    /// 且已经超过阈值时间。
    /// </summary>
    private bool IsStarving(ExternalTask task, DateTime nowUtc) =>
        string.Equals(task.Status, ExternalTaskStatus.Pending, StringComparison.Ordinal)
        && task.WorkerId is null
        && task.ErrorMessage is null
        && task.NextDueAt is null
        && nowUtc - task.CreatedAt >= _options.NoWorkerThreshold;

    private async Task RaiseAsync(
        string code,
        AlertSeverity severity,
        ExternalTask task,
        DateTime nowUtc,
        string message,
        CancellationToken ct)
    {
        var key = $"{code}|{task.Id}";

        if (_lastAlerted.TryGetValue(key, out var last) && nowUtc - last < _options.AlertCooldown)
        {
            return;
        }

        _lastAlerted[key] = nowUtc;

        EngineDiagnostics.ExternalTaskAlerts.Add(
            1,
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("tenant", task.TenantId ?? "default"));

        var alert = PlatformAlert.Raise(
            code,
            severity,
            message,
            task.TenantId,
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["topic"] = task.Topic,
                ["activityId"] = task.ActivityId,
                ["processInstanceId"] = task.ProcessInstanceId,
                ["processDefinitionKey"] = task.ProcessDefinitionKey,
                ["waitingMinutes"] = Math.Round((nowUtc - task.CreatedAt).TotalMinutes, 1),
                ["errorMessage"] = task.ErrorMessage,
            });

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.RaiseAsync(alert, ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // 告警通道本身挂了不能把巡检带崩：剩下的出口继续发，本轮就算了
                _logger.LogError(exception, "告警出口 {SinkType} 处理告警 {AlertCode} 时失败。", sink.GetType().Name, code);
            }
        }
    }

    /// <summary>清掉已过冷却期的条目，避免字典随任务量无限增长。</summary>
    private void PruneCooldown(DateTime nowUtc)
    {
        foreach (var pair in _lastAlerted)
        {
            if (nowUtc - pair.Value >= _options.AlertCooldown)
            {
                _lastAlerted.TryRemove(pair.Key, out _);
            }
        }
    }
}
