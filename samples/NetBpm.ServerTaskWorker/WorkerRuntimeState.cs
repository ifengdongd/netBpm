namespace NetBpm.ServerTaskWorker;

/// <summary>
/// worker 的运行态 —— 只为了让 <c>/healthz</c> 能回答「它到底在不在干活」。
/// </summary>
/// <remarks>
/// 光有 <c>status=ok</c> 是不够的：进程活着但 fetch 一直失败，也照样能回 ok。
/// 所以额外暴露「上一次成功轮询是什么时候」。
/// </remarks>
public sealed class WorkerRuntimeState
{
    private long _lastPollTicks;
    private string? _degradedReason;

    /// <summary>上次成功领取的时间（UTC）；还没成功过则为 <c>null</c>。</summary>
    public DateTimeOffset? LastPollAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastPollTicks);

            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// 降级原因：进程起来了，但没在领任务。
    /// </summary>
    /// <remarks>
    /// <c>status=ok</c> 但 <c>lastPollAt</c> 一直是 <c>null</c> 时，光看这两个字段分不清
    /// 「配置没给全所以根本没启动 worker」和「启动了但一直领不到」—— 这里把前者说出来。
    /// </remarks>
    public string? DegradedReason => Volatile.Read(ref _degradedReason);

    /// <summary>记一次成功轮询。</summary>
    /// <param name="at">轮询时刻。</param>
    public void MarkPoll(DateTimeOffset at) => Volatile.Write(ref _lastPollTicks, at.UtcTicks);

    /// <summary>标记降级（只用于启动期，运行期不改）。</summary>
    /// <param name="reason">给人看的原因。</param>
    public void MarkDegraded(string reason) => Volatile.Write(ref _degradedReason, reason);
}
