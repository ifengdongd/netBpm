using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NetBpm.Engine.Diagnostics;

/// <summary>
/// 引擎运行时指标与追踪。
/// <para>
/// 与 <c>NetBpm.Admin</c> 的管理指标分开：这里回答的是「引擎本身跑得怎么样」——
/// 命令吞吐、耗时、失败率、乐观锁重试、定时作业失败。宿主通过
/// <c>AddMeter(EngineDiagnostics.MeterName)</c> / <c>AddSource(EngineDiagnostics.ActivitySourceName)</c>
/// 接入 OpenTelemetry 即可。
/// </para>
/// </summary>
public static class EngineDiagnostics
{
    /// <summary>Meter 名（OTel 仪表名）。</summary>
    public const string MeterName = "NetBpm.Engine";

    /// <summary>ActivitySource 名（分布式追踪）。</summary>
    public const string ActivitySourceName = "NetBpm.Engine";

    /// <summary>引擎版本标签。</summary>
    private static readonly string Version =
        typeof(EngineDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>命令执行追踪（每个最外层命令一条 span，标签 <c>netbpm.command</c>）。</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    /// <summary>引擎指标。</summary>
    public static readonly Meter Meter = new(MeterName, Version);

    /// <summary>命令执行总数（按命令名 / 结果）。</summary>
    public static readonly Counter<long> CommandsExecuted = Meter.CreateCounter<long>(
        "netbpm.command.executed",
        unit: "{command}",
        description: "引擎命令执行次数（tag: command / result）");

    /// <summary>命令耗时（毫秒）。</summary>
    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "netbpm.command.duration",
        unit: "ms",
        description: "引擎命令执行耗时（含重试）");

    /// <summary>乐观锁重试次数。</summary>
    public static readonly Counter<long> OptimisticLockRetries = Meter.CreateCounter<long>(
        "netbpm.command.retry",
        unit: "{retry}",
        description: "乐观锁冲突后的命令重试次数");

    /// <summary>定时作业失败次数（按是否进入死信）。</summary>
    public static readonly Counter<long> TimerJobFailures = Meter.CreateCounter<long>(
        "netbpm.timer_job.failure",
        unit: "{job}",
        description: "定时作业执行失败次数（tag: dead_letter）");

    /// <summary>异步作业失败次数（按是否进入死信）。</summary>
    public static readonly Counter<long> AsyncJobFailures = Meter.CreateCounter<long>(
        "netbpm.async_job.failure",
        unit: "{job}",
        description: "异步作业执行失败次数（tag: dead_letter）");

    /// <summary>outbox 发布失败次数（按是否进入死信）。</summary>
    public static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>(
        "netbpm.outbox.failure",
        unit: "{event}",
        description: "outbox 事件发布失败次数（tag: dead_letter）");

    /// <summary>外部任务告警次数（按告警码）。</summary>
    public static readonly Counter<long> ExternalTaskAlerts = Meter.CreateCounter<long>(
        "netbpm.external_task.alert",
        unit: "{alert}",
        description: "外部任务告警次数（tag: code / tenant）");

    /// <summary>事件订阅投递结果（按成功 / 失败）。</summary>
    public static readonly Counter<long> EventDeliveries = Meter.CreateCounter<long>(
        "netbpm.event.delivery",
        unit: "{delivery}",
        description: "事件订阅投递次数（tag: subscription / result）");
}
