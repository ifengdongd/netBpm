namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// Redis 作业队列的运行快照（管理端「Redis 诊断」页只读展示）。
/// </summary>
/// <remarks>
/// 只是<b>观测数据</b>，不参与作业调度：快照里的条目数会随作业消费随时变化，
/// 三个键都不存在（计数为 0）是<b>正常</b>状态 —— 队列只存「到期提示」，作业执行完即删除，空键由 Redis 自动回收。
/// </remarks>
/// <param name="Reachable">PING 是否成功。</param>
/// <param name="LatencyMs">PING 往返时延（毫秒）；不可达时为 <see langword="null"/>。</param>
/// <param name="Database">实际命中的逻辑库编号（配了连接串时以连接串为准）。</param>
/// <param name="Error">失败原因（不含口令）；成功时为 <see langword="null"/>。</param>
/// <param name="Keys">三个队列键的条目数；键不存在时计数为 0。</param>
/// <param name="Server">服务端 INFO 摘要；INFO 被拒或不支持时为 <see langword="null"/>。</param>
public sealed record RedisQueueSnapshot(
    bool Reachable,
    double? LatencyMs,
    int Database,
    string? Error,
    IReadOnlyList<RedisQueueKeyInfo> Keys,
    RedisServerInfo? Server);

/// <summary>一个队列键的当前状态。</summary>
/// <param name="Role">语义角色：<c>delay</c>（延迟段）/ <c>ready</c>（就绪段）/ <c>inflight</c>（在途段）。</param>
/// <param name="Key">完整键名（含 <c>KeyPrefix</c>）。</param>
/// <param name="Type">Redis 数据类型（<c>zset</c> / <c>list</c>）。</param>
/// <param name="Length">条目数（<c>ZCARD</c> / <c>LLEN</c>）。</param>
public sealed record RedisQueueKeyInfo(string Role, string Key, string Type, long Length);

/// <summary>Redis 服务端 INFO 摘要（只取诊断需要的几项，不含任何数据内容）。</summary>
/// <param name="Version">服务端版本（如 <c>7.2.4</c>）。</param>
/// <param name="Mode">运行模式：<c>standalone</c> / <c>cluster</c> / <c>sentinel</c>。</param>
/// <param name="UptimeSeconds">服务端已运行秒数。</param>
/// <param name="ConnectedClients">当前连接数。</param>
/// <param name="UsedMemoryBytes">已用内存（字节）。</param>
/// <param name="TotalCommandsProcessed">累计处理命令数。</param>
/// <param name="KeyspaceHits">键空间命中次数。</param>
/// <param name="KeyspaceMisses">键空间未命中次数。</param>
/// <param name="Keyspace">各逻辑库的键数量（键名形如 <c>db1</c>）。</param>
public sealed record RedisServerInfo(
    string? Version,
    string? Mode,
    long? UptimeSeconds,
    int? ConnectedClients,
    long? UsedMemoryBytes,
    long? TotalCommandsProcessed,
    long? KeyspaceHits,
    long? KeyspaceMisses,
    IReadOnlyDictionary<string, long> Keyspace);
