namespace NetBpm.Engine.Jobs;

/// <summary>
/// 延迟作业队列中的一个条目：<b>只带作业 id 与到期时间，不带任何业务负载</b>。
/// </summary>
/// <param name="JobId">作业 id（<c>ACT_RU_JOB.ID_</c>）。</param>
/// <param name="DueAtUtc">期望被拾取的时间（UTC）。</param>
/// <remarks>
/// 队列只是「什么时候该去看一眼某个作业」的提示通道，作业数据与租约<b>始终在数据库里</b>。
/// 这样设计带来三个直接好处：
/// <list type="number">
///   <item><description>队列重复投递不会造成重复执行 —— 命令里的数据库租约会挡住；</description></item>
///   <item><description>队列提前投递不会造成提前执行 —— 命令会校验 <c>DUEDATE_</c>；</description></item>
///   <item><description>宿主崩溃后未完成的作业靠<b>数据库租约过期</b>回收，不依赖队列的可见性语义。</description></item>
/// </list>
/// 因此换队列实现（内存 ↔ Redis）是纯粹的延迟 / 吞吐优化，不改变正确性。
/// </remarks>
public sealed record JobQueueItem(string JobId, DateTime DueAtUtc);

/// <summary>作业队列实现的种类，由配置 <c>NetBpm:Jobs:Queue:Provider</c> 决定。</summary>
public enum JobQueueProvider
{
    /// <summary>进程内优先队列（默认）。零外部依赖；进程重启后未到期提示丢失，由兜底扫库补上。</summary>
    Memory = 0,

    /// <summary>Redis 延迟队列（有序集合 + 就绪列表 + 可见性超时回收），可多宿主共享、重启不丢。</summary>
    Redis = 1,
}

/// <summary>
/// 延迟作业队列端口：把「到期作业从哪来」与「作业怎么执行」解耦。
/// </summary>
/// <remarks>
/// <para>
/// 引擎侧只在落库异步作业时 <see cref="EnqueueAsync"/> 一个提示；
/// 后台执行器 <c>AsyncJobExecutorService</c> 用 <see cref="TakeDueAsync"/> 取到候选作业 id，
/// 再回到数据库抢占租约并执行。队列丢提示 = 退化成定期扫库，<b>不会丢作业</b>。
/// </para>
/// </remarks>
public interface IJobQueue : IAsyncDisposable
{
    /// <summary>实现名称（<c>memory</c> / <c>redis</c>），仅用于诊断与启动日志。</summary>
    string Name { get; }

    /// <summary>
    /// 投递一个到期提示。
    /// </summary>
    /// <remarks>
    /// 允许重复投递：同一个 jobId 被投递多次只会多一次无效的数据库查询。
    /// 实现<b>不得</b>因为条目已存在而抛异常。
    /// </remarks>
    ValueTask EnqueueAsync(JobQueueItem item, CancellationToken ct = default);

    /// <summary>
    /// 取出最多 <paramref name="maxCount"/> 条已到期的条目。
    /// </summary>
    /// <param name="maxCount">本轮最多取几条。</param>
    /// <param name="waitTimeout">
    /// 无到期条目时最多等待多久；到期后返回空集合，让调用方回扫数据库兜底。
    /// </param>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyList<JobQueueItem>> TakeDueAsync(
        int maxCount,
        TimeSpan waitTimeout,
        CancellationToken ct = default);

    /// <summary>作业执行成功：从在途集合移除，避免可见性超时后被重复拾取。</summary>
    ValueTask CompleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>作业执行失败 / 主动放弃：提前把条目放回就绪队列（不等可见性超时）。</summary>
    ValueTask ReleaseAsync(string jobId, CancellationToken ct = default);
}
