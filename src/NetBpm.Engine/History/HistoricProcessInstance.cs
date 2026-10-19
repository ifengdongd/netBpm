namespace NetBpm.Engine.History;

/// <summary>
/// 历史流程实例。对应 Activiti 的 <c>HistoricProcessInstance</c> / <c>ACT_HI_PROCINST</c> 一行。
/// <para>
/// 它在实例结束后仍然存在（运行时的 <c>ACT_RU_EXECUTION</c> 行已被删除），
/// 因此「已归档实例」的查询必须走它 —— 这是 P9「查历史」端点的数据源。
/// </para>
/// </summary>
/// <param name="Id"><c>ID_</c>（= 流程实例 id）。</param>
/// <param name="ProcessInstanceId"><c>PROC_INST_ID_</c>。</param>
/// <param name="BusinessKey"><c>BUSINESS_KEY_</c>。</param>
/// <param name="ProcessDefinitionId"><c>PROC_DEF_ID_</c>。</param>
/// <param name="StartTime"><c>START_TIME_</c>。</param>
/// <param name="EndTime"><c>END_TIME_</c>；为空表示实例仍在运行。</param>
/// <param name="DurationInMillis"><c>DURATION_</c>。</param>
/// <param name="StartUserId"><c>START_USER_ID_</c>。</param>
/// <param name="StartActivityId"><c>START_ACT_ID_</c>（开始事件 id）。</param>
/// <param name="EndActivityId"><c>END_ACT_ID_</c>（结束时所在节点 id）。</param>
/// <param name="DeleteReason"><c>DELETE_REASON_</c>；正常结束为 <see langword="null"/>。</param>
/// <param name="Name"><c>NAME_</c>。</param>
/// <param name="TenantId"><c>TENANT_ID_</c>；为空表示无租户（B2.3）。</param>
public sealed record HistoricProcessInstance(
    string Id,
    string ProcessInstanceId,
    string? BusinessKey,
    string ProcessDefinitionId,
    DateTime? StartTime,
    DateTime? EndTime,
    long? DurationInMillis,
    string? StartUserId,
    string? StartActivityId,
    string? EndActivityId,
    string? DeleteReason,
    string? Name,
    string? TenantId = null)
{
    /// <summary>是否仍在运行（对应 Activiti <c>HistoricProcessInstance.getEndTime() == null</c>）。</summary>
    public bool IsRunning => EndTime is null;
}
