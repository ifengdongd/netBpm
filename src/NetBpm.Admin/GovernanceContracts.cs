using NetBpm.Runtime.Hosting.JobQueue;

namespace NetBpm.Admin;

/// <summary>一条管理动作审计（REST 响应）。</summary>
/// <param name="Id">审计记录 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="Actor">操作人。</param>
/// <param name="AuthMode">鉴权方式：<c>jwt</c> / <c>api_key</c> / <c>none</c>。</param>
/// <param name="Action">动作标识（如 <c>task.reassign</c>）。</param>
/// <param name="TargetType">目标类型。</param>
/// <param name="TargetId">目标 id。</param>
/// <param name="ProcessInstanceId">关联流程实例 id。</param>
/// <param name="Detail">动作明细（JSON 字符串；不含变量值）。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Error">失败原因。</param>
/// <param name="CreatedAt">写入时间（UTC）。</param>
public sealed record AdminAuditItem(
    string Id,
    string? TenantId,
    string? Actor,
    string AuthMode,
    string Action,
    string TargetType,
    string TargetId,
    string? ProcessInstanceId,
    string? Detail,
    bool Success,
    string? Error,
    DateTime CreatedAt);

/// <summary>审计分页响应。</summary>
/// <param name="Total">总数。</param>
/// <param name="Items">当前页（按时间倒序）。</param>
public sealed record AdminAuditListResponse(int Total, IReadOnlyList<AdminAuditItem> Items);

/// <summary>审计保留策略清理结果。</summary>
/// <param name="Removed">删除条数。</param>
/// <param name="Before">清理上界（UTC，严格小于）。</param>
/// <param name="TenantId">租户口径（<see langword="null"/> 表示全部租户）。</param>
public sealed record AdminAuditPurgeResponse(int Removed, DateTime Before, string? TenantId);

/// <summary>按动作聚合的审计计数。</summary>
/// <param name="Action">动作标识。</param>
/// <param name="Count">条数。</param>
public sealed record AdminAuditActionCount(string Action, int Count);

/// <summary>
/// 管理指标。
/// <para>
/// <c>null</c> 表示<b>当前宿主无法统计</b>（例如内存宿主没有 <c>ACT_RU_TASK</c> 可聚合），
/// 而不是「统计结果为 0」—— 前端应显示为「不可用」。
/// </para>
/// </summary>
/// <param name="GeneratedAt">生成时间（UTC）。</param>
/// <param name="TenantId">租户口径。</param>
/// <param name="AuditWindowDays">审计统计窗口（天）。</param>
/// <param name="RunningProcessInstances">运行中流程实例数。</param>
/// <param name="ProcessDefinitions">流程定义数。</param>
/// <param name="HistoricProcessInstances">历史流程实例数。</param>
/// <param name="FinishedProcessInstances">已结束实例数。</param>
/// <param name="DeletedProcessInstances">已删除实例数。</param>
/// <param name="PendingTasks">待办总数。</param>
/// <param name="OverdueTasks">逾期待办数。</param>
/// <param name="DueTodayTasks">今天到期待办数。</param>
/// <param name="PendingTasksByProcessKey">按流程 key 合并后的待办分布。</param>
/// <param name="ArchivedInstances">已归档实例数（宿主不支持归档时为 <see langword="null"/>）。</param>
/// <param name="AuditActions">窗口内的管理动作计数。</param>
public sealed record AdminMetricsResponse(
    DateTime GeneratedAt,
    string? TenantId,
    int AuditWindowDays,
    int RunningProcessInstances,
    int ProcessDefinitions,
    int HistoricProcessInstances,
    int FinishedProcessInstances,
    int DeletedProcessInstances,
    int? PendingTasks,
    int? OverdueTasks,
    int? DueTodayTasks,
    IReadOnlyDictionary<string, int>? PendingTasksByProcessKey,
    int? ArchivedInstances,
    IReadOnlyList<AdminAuditActionCount> AuditActions);

/// <summary>
/// Redis 作业队列诊断（<c>GET /api/admin/redis</c>）。
/// <para>
/// 只暴露运维口径的信息：连上了没有、三个队列键里各有多少条目、服务端版本与内存；
/// <b>口令永远不回传</b>，连接串只以 <see cref="UsesConnectionString"/> 这个布尔标记体现。
/// </para>
/// <para>
/// 队列键全部为 0 / 不存在是<b>正常</b>状态：Redis 只存「作业到期提示」，
/// 作业执行完即删除，空键由 Redis 自动回收；流程数据始终在数据库里。
/// </para>
/// </summary>
/// <param name="Provider">实际生效的队列实现名：<c>redis</c> / <c>memory</c>。</param>
/// <param name="IsRedis">是否由 Redis 实现承载；<see langword="false"/> 时其余连接类字段多为 <see langword="null"/>。</param>
/// <param name="Target">目标地址摘要（<c>host:port</c>；连接串模式下为解析出的端点，不含口令）。</param>
/// <param name="Database">实际命中的逻辑库编号；连不上时无法确定，为 <see langword="null"/>。</param>
/// <param name="KeyPrefix">队列键前缀（如 <c>netbpm:</c>）。</param>
/// <param name="Ssl">是否启用 TLS。</param>
/// <param name="UsesConnectionString">是否由 <c>ConnectionString</c> 指定连接（此时 Host / Port / Database 配置被忽略）。</param>
/// <param name="CheckedAt">采集时间（UTC）。</param>
/// <param name="Reachable">PING 是否成功。</param>
/// <param name="LatencyMs">PING 往返时延（毫秒）。</param>
/// <param name="Error">失败原因（不含口令）；正常时为 <see langword="null"/>。</param>
/// <param name="Keys">三个队列键的条目数（键不存在即为 0）。</param>
/// <param name="Server">服务端 INFO 摘要；INFO 被拒或不支持时为 <see langword="null"/>。</param>
public sealed record AdminRedisInfoResponse(
    string Provider,
    bool IsRedis,
    string? Target,
    int? Database,
    string? KeyPrefix,
    bool? Ssl,
    bool UsesConnectionString,
    DateTime CheckedAt,
    bool Reachable,
    double? LatencyMs,
    string? Error,
    IReadOnlyList<RedisQueueKeyInfo> Keys,
    RedisServerInfo? Server);
