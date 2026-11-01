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

    // ─────────────────────────── outbox 积压 ───────────────────────────
    // 积压是状态量，只能用 Gauge；而观测式 Gauge 的回调是同步的，不能在里面查库。
    // 取值统一走 OutboxBacklogProbe（后台采样器写入的快照），这里只做「读」。
    // Prometheus 侧名字：netbpm_outbox_pending / netbpm_outbox_dead_letter /
    // netbpm_outbox_oldest_pending_age_seconds / netbpm_outbox_backlog_stale。

    /// <summary>outbox 待发布条数（未发布且未死信）。</summary>
    public static readonly ObservableGauge<int> OutboxPending = Meter.CreateObservableGauge(
        "netbpm.outbox.pending",
        () => OutboxBacklogProbe.Pending,
        unit: "{event}",
        description: "outbox 中尚未发布、也未进死信的事件数。");

    /// <summary>outbox 死信条数（已停投，需人工介入）。</summary>
    public static readonly ObservableGauge<int> OutboxDeadLetter = Meter.CreateObservableGauge(
        "netbpm.outbox.dead_letter",
        () => OutboxBacklogProbe.DeadLetter,
        unit: "{event}",
        description: "outbox 中已判死信、停止重试的事件数。");

    /// <summary>
    /// 最老一条待发布事件的年龄（秒）；没有积压时为 0。
    /// <para>
    /// 🔑 积压要看<b>年龄</b>而不是条数：条数高可能只是瞬时洪峰，
    /// 条数低但最老的一条躺了很久，说明发布链路已经断了。
    /// </para>
    /// </summary>
    public static readonly ObservableGauge<double> OutboxOldestPendingAge = Meter.CreateObservableGauge(
        "netbpm.outbox.oldest_pending_age",
        () => OutboxBacklogProbe.OldestPendingAgeSeconds,
        unit: "s",
        description: "最老一条待发布事件的年龄（秒）；没有待发布事件时为 0。");

    /// <summary>
    /// 积压采样是否已停止（1 = 上面三个数不可信）。
    /// <para>
    /// 缓存式 Gauge 的老问题：进程没了，最后那个数字还在。把「没有积压」
    /// 和「不知道有没有」混在一条指标里，是最容易让告警失灵的一种混法。
    /// </para>
    /// </summary>
    public static readonly ObservableGauge<int> OutboxBacklogStale = Meter.CreateObservableGauge(
        "netbpm.outbox.backlog_stale",
        () => OutboxBacklogProbe.IsStale ? 1 : 0,
        unit: "{state}",
        description: "积压指标是否已停止更新（1 = 采样器未运行，积压数值不可信）。");
}
