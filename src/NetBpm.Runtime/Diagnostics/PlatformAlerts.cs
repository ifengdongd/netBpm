using Microsoft.Extensions.Logging;

namespace NetBpm.Runtime.Diagnostics;

/// <summary>
/// 平台告警码。告警与异常的区别：<b>异常是这次调用失败了，告警是没人调用</b> ——
/// 外部任务没人领、事件投递一直失败这类「静默卡住」的情况不会体现在任何一次请求的返回值里，
/// 只能靠巡检发现，因此单独走一条通道。
/// </summary>
public static class AlertCodes
{
    /// <summary>
    /// 外部任务登记后长时间没有 worker 来领（<c>PENDING</c> 且超过阈值）。
    /// <para>典型成因：worker 全挂了 / topic 拼错 / worker 凭据没有 <c>workflow:external:task</c>。</para>
    /// </summary>
    public const string ExternalTaskNoWorker = "external_task.no_worker";

    /// <summary>
    /// 外部任务重试耗尽进了死信，流程停在原地等管理干预。
    /// </summary>
    public const string ExternalTaskDeadLetter = "external_task.dead_letter";

    /// <summary>
    /// outbox 事件投递失败并进入死信（订阅方一直不可用）。
    /// </summary>
    public const string EventDeliveryDeadLetter = "event.delivery.dead_letter";
}

/// <summary>告警级别。</summary>
public enum AlertSeverity
{
    /// <summary>知道就行，不要求马上处理。</summary>
    Info = 0,

    /// <summary>需要人来看一眼，短时间内不会造成数据错误。</summary>
    Warning = 1,

    /// <summary>业务已经在停摆（流程卡死 / 通知全丢），要立刻处理。</summary>
    Critical = 2,
}

/// <summary>
/// 一条平台告警。
/// </summary>
/// <param name="Code">告警码（见 <see cref="AlertCodes"/>），订阅告警的人按它分类路由。</param>
/// <param name="Severity">级别。</param>
/// <param name="Message">人话说明，要能直接贴到工单里。</param>
/// <param name="OccurredAt">发生时间（UTC）。</param>
/// <param name="TenantId">租户；与租户无关时为空。</param>
/// <param name="Subject">主体 id：外部任务 id / 事件 id，用来定位到具体那一条。</param>
/// <param name="Data">结构化上下文（topic、实例 id、错误信息等）。</param>
public sealed record PlatformAlert(
    string Code,
    AlertSeverity Severity,
    string Message,
    DateTime OccurredAt,
    string? TenantId = null,
    string? Subject = null,
    IReadOnlyDictionary<string, object?>? Data = null)
{
    /// <summary>构造一条告警（时间为当前 UTC）。</summary>
    public static PlatformAlert Raise(
        string code,
        AlertSeverity severity,
        string message,
        string? tenantId = null,
        string? subject = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(code, severity, message, DateTime.UtcNow, tenantId, subject, data);
}

/// <summary>
/// 告警出口。宿主实现它把告警接到自己的通道（企业微信 / 钉钉 / PagerDuty / 工单系统）；
/// 不实现也能用 —— 默认注册 <see cref="LoggingAlertSink"/>，至少不会石沉大海。
/// </summary>
public interface IAlertSink
{
    /// <summary>
    /// 发出一条告警。实现<b>不应抛异常</b>：告警通道挂了不能反过来把巡检服务带崩，
    /// 内部自行兜住（<see cref="LoggingAlertSink"/> 就是这么做的）。
    /// </summary>
    ValueTask RaiseAsync(PlatformAlert alert, CancellationToken ct = default);
}

/// <summary>
/// 默认告警出口：写日志。级别映射为 Warning / Critical → LogError，Info → LogInformation。
/// </summary>
public sealed class LoggingAlertSink : IAlertSink
{
    private readonly ILogger<LoggingAlertSink> _logger;

    /// <summary>构造。</summary>
    public LoggingAlertSink(ILogger<LoggingAlertSink> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask RaiseAsync(PlatformAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var data = alert.Data is { Count: > 0 } items
            ? string.Join(", ", items.Select(pair => $"{pair.Key}={pair.Value}"))
            : "-";

        // 模板必须是字面量（CA2254），三个级别各自写一份，不拼变量
        if (alert.Severity >= AlertSeverity.Critical)
        {
            _logger.LogError(
                "[{AlertCode}] {Message}（tenant={TenantId}, subject={Subject}, data={Data}）",
                alert.Code,
                alert.Message,
                alert.TenantId ?? "-",
                alert.Subject ?? "-",
                data);
        }
        else if (alert.Severity == AlertSeverity.Warning)
        {
            _logger.LogWarning(
                "[{AlertCode}] {Message}（tenant={TenantId}, subject={Subject}, data={Data}）",
                alert.Code,
                alert.Message,
                alert.TenantId ?? "-",
                alert.Subject ?? "-",
                data);
        }
        else
        {
            _logger.LogInformation(
                "[{AlertCode}] {Message}（tenant={TenantId}, subject={Subject}, data={Data}）",
                alert.Code,
                alert.Message,
                alert.TenantId ?? "-",
                alert.Subject ?? "-",
                data);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 内存告警出口：把告警攒在进程内，供测试断言与 <c>/diagnostics</c> 类端点回显。
/// </summary>
public sealed class InMemoryAlertSink : IAlertSink
{
    private readonly List<PlatformAlert> _alerts = [];
    private readonly int _capacity;

    /// <summary>构造。</summary>
    /// <param name="capacity">最多保留多少条（超出丢最旧的，避免巡检长期运行把内存吃满）。</param>
    public InMemoryAlertSink(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <inheritdoc />
    public ValueTask RaiseAsync(PlatformAlert alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        lock (_alerts)
        {
            if (_alerts.Count >= _capacity)
            {
                _alerts.RemoveAt(0);
            }

            _alerts.Add(alert);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>快照（按发生时间升序）。</summary>
    public IReadOnlyList<PlatformAlert> Alerts
    {
        get
        {
            lock (_alerts)
            {
                return _alerts.OrderBy(item => item.OccurredAt).ToArray();
            }
        }
    }

    /// <summary>清空（测试用）。</summary>
    public void Clear()
    {
        lock (_alerts)
        {
            _alerts.Clear();
        }
    }
}
