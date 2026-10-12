namespace NetBpm.Engine.History;

/// <summary>
/// 历史任务实例。对应 Activiti 的 <c>HistoricTaskInstance</c> / <c>ACT_HI_TASKINST</c> 一行。
/// <para>用途：已办查询与流程审计（任务完成后 <c>ACT_RU_TASK</c> 行已删除，只剩它）。</para>
/// </summary>
/// <param name="Id"><c>ID_</c>（与 <c>ACT_RU_TASK.ID_</c> 相同）。</param>
/// <param name="ProcessDefinitionId"><c>PROC_DEF_ID_</c>。</param>
/// <param name="TaskDefinitionKey"><c>TASK_DEF_KEY_</c>（BPMN 中 userTask 的 id）。</param>
/// <param name="ProcessInstanceId"><c>PROC_INST_ID_</c>。</param>
/// <param name="ExecutionId"><c>EXECUTION_ID_</c>。</param>
/// <param name="Name"><c>NAME_</c>。</param>
/// <param name="Assignee"><c>ASSIGNEE_</c>。</param>
/// <param name="Owner"><c>OWNER_</c>。</param>
/// <param name="Priority"><c>PRIORITY_</c>。</param>
/// <param name="FormKey"><c>FORM_KEY_</c>。</param>
/// <param name="StartTime"><c>START_TIME_</c>。</param>
/// <param name="EndTime"><c>END_TIME_</c>；为空表示任务仍未结束。</param>
/// <param name="DurationInMillis"><c>DURATION_</c>。</param>
/// <param name="DeleteReason"><c>DELETE_REASON_</c>。</param>
public sealed record HistoricTaskInstance(
    string Id,
    string? ProcessDefinitionId,
    string? TaskDefinitionKey,
    string? ProcessInstanceId,
    string? ExecutionId,
    string? Name,
    string? Assignee,
    string? Owner,
    int? Priority,
    string? FormKey,
    DateTime? StartTime,
    DateTime? EndTime,
    long? DurationInMillis,
    string? DeleteReason)
{
    /// <summary>是否仍未结束。</summary>
    public bool IsOpen => EndTime is null;
}
