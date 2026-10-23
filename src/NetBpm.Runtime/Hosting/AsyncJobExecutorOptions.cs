using NetBpm.Engine.Jobs;

namespace NetBpm.Runtime.Hosting;

/// <summary>异步作业执行器配置（<c>NetBpm:Jobs</c>）。</summary>
public sealed class AsyncJobExecutorOptions
{
    /// <summary>是否启用后台执行器；关闭时作业只能通过运维接口手动执行。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔；本轮有作业执行时立即继续下一轮，不等待。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>每轮最多处理的作业数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>启动后延迟多久开始轮询（让宿主完成初始化 / 迁移）。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 连续空转多少轮之后开始退避（在此之前保持基准 <see cref="PollInterval"/>）。
    /// <para>默认值 0 表示「一空转就按 2 倍递增」，能最快地把空闲库的轮询压力降下来。</para>
    /// </summary>
    public int IdleRoundsBeforeBackoff { get; set; }

    /// <summary>
    /// 退避上限。空闲库最多退避到这个间隔；一有作业执行就立刻回落到 <see cref="PollInterval"/>。
    /// <para>默认 30s：兼顾「空闲时几乎不打库」与「新作业最坏 30s 内被拾取」。</para>
    /// </summary>
    public TimeSpan MaxPollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 一次拾取的租约时长；执行超过该时长仍未完成的作业会被判为「宿主可能已崩溃」并可被接管。
    /// <para>默认 5min：远大于任何单条作业的合理执行时长，避免误判。</para>
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 延迟作业队列配置（<c>NetBpm:Jobs:Queue</c>）。
    /// </summary>
    public JobQueueOptions Queue { get; set; } = new();
}

/// <summary>
/// 延迟作业队列配置：决定「到期作业提示」用哪种实现。
/// </summary>
/// <remarks>
/// <para>
/// 队列<b>只传作业 id 与到期时间</b>，不传业务负载；作业数据与租约始终以数据库为准。
/// 因此无论选哪种实现，<b>正确性完全一致</b>，差别只在延迟与吞吐量：
/// </para>
/// <list type="bullet">
///   <item><description><see cref="JobQueueProvider.Memory"/>（默认）：零外部依赖，
///   进程内唤醒延迟毫秒级；进程重启后未到期提示丢失，由执行器的兜底扫库补上。</description></item>
///   <item><description><see cref="JobQueueProvider.Redis"/>：多宿主共享队列，
///   作业提示落在 Redis 里，进程重启不丢；以有序集合做延迟段、列表做就绪段，
///   并用「可见性超时 + 回收」兜住宿主崩溃。</description></item>
/// </list>
/// <para>
/// <b>配置示例</b>（appsettings.json）：
/// <code>
/// "NetBpm": {
///   "Jobs": {
///     "Queue": {
///       "Provider": "Redis",
///       "Redis": {
///         "Host": "127.0.0.1",
///         "Port": 6379,
///         "Auth": "******",
///         "KeyPrefix": "netbpm:"
///       }
///     }
///   }
/// }
/// </code>
/// </para>
/// </remarks>
public sealed class JobQueueOptions
{
    /// <summary>
    /// 队列实现种类，对应配置 <c>NetBpm:Jobs:Queue:Provider</c>。
    /// 取值 <c>Memory</c>（默认）或 <c>Redis</c>（不区分大小写）。
    /// </summary>
    public JobQueueProvider Provider { get; set; } = JobQueueProvider.Memory;
}


/// <summary>outbox 发布器配置（<c>NetBpm:Outbox</c>）。</summary>
public sealed class OutboxPublisherOptions
{
    /// <summary>是否启用后台发布器。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>每轮最多发布的事件数。</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>启动后延迟多久开始发布。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 发布租约时长：抢到一条事件后在这段时间内独占，其它宿主不会重复投递。
    /// <para>
    /// 必须<b>明显大于</b>单条 handler 的最长耗时；进程崩溃时租约到期，事件自动回到待发布队列
    /// （仍是至少一次语义，订阅方按事件 id 幂等）。
    /// </para>
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
