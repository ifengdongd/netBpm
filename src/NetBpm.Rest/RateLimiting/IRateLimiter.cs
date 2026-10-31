namespace NetBpm.Rest.RateLimiting;

/// <summary>
/// 配额计数器的<b>唯一抽象</b>：中间件只认它，换实现不用改中间件。
/// </summary>
/// <remarks>
/// <para>
/// 抽象出来的直接动因是<b>多副本</b>：进程内计数器在 N 个副本下实际配额是
/// <c>N × 配置值</c>——配置 600 次/分钟、跑了 3 个副本，调用方实际能打 1800 次，
/// 而这个差额在单副本测试里永远看不出来。
/// </para>
/// <para>
/// 接口是异步的（<see cref="AcquireAsync"/>）：Redis 实现要走网络。
/// 进程内实现同步完成，返回已完成的 <see cref="ValueTask{TResult}"/>，不额外分配。
/// </para>
/// </remarks>
public interface IRateLimiter
{
    /// <summary>
    /// 尝试占用一次配额。
    /// </summary>
    /// <param name="partitionKey">分区键（客户端 id / 用户 / IP，含种类前缀）。</param>
    /// <param name="permitLimit">该分区每分钟配额；<c>&lt;= 0</c> 表示不限流。</param>
    /// <param name="cancellationToken">取消令牌（请求中断）。</param>
    /// <returns>判定结果。</returns>
    ValueTask<RateLimitDecision> AcquireAsync(
        string partitionKey,
        int permitLimit,
        CancellationToken cancellationToken = default);
}

/// <summary>配额计数器的形态（配置键 <c>NetBpm:RateLimit:Store</c>）。</summary>
public enum RateLimitStore
{
    /// <summary>进程内计数：<b>单副本</b>下正确，多副本时总配额 = 副本数 × 配置值。</summary>
    Memory = 0,

    /// <summary>Redis 计数：跨副本共享同一个桶，配额才是「全局」的。</summary>
    Redis = 1,
}
