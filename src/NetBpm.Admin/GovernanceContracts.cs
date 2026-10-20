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
