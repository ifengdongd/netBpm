namespace NetBpm.Engine.History;

/// <summary>
/// 历史变量。对应 Activiti 的 <c>HistoricVariableInstance</c> / <c>ACT_HI_VARINST</c> 一行。
/// <para>
/// 🔑 历史变量行<b>只增不删</b>：运行态变量删除后，历史仍保留最后一次的值（见 <c>RemoveVariableAsync</c>）。
/// 因此它回答的是「这个流程实例上曾经有过哪些变量、最后是什么值」，而不是「当前值」。
/// </para>
/// </summary>
/// <param name="Id"><c>ID_</c>（与 <c>ACT_RU_VARIABLE.ID_</c> 相同）。</param>
/// <param name="ProcessInstanceId"><c>PROC_INST_ID_</c>。</param>
/// <param name="ExecutionId"><c>EXECUTION_ID_</c>。</param>
/// <param name="TaskId"><c>TASK_ID_</c>（任务局部变量才有值）。</param>
/// <param name="Name"><c>NAME_</c>。</param>
/// <param name="Value">按 <c>VAR_TYPE_</c> + 值列解码后的 CLR 值。</param>
/// <param name="TypeName"><c>VAR_TYPE_</c>（<c>string</c> / <c>long</c> / <c>boolean</c> …）。</param>
/// <param name="CreateTime"><c>CREATE_TIME_</c>。</param>
/// <param name="LastUpdatedTime"><c>LAST_UPDATED_TIME_</c>。</param>
public sealed record HistoricVariableInstance(
    string Id,
    string? ProcessInstanceId,
    string? ExecutionId,
    string? TaskId,
    string Name,
    object? Value,
    string? TypeName,
    DateTime? CreateTime,
    DateTime? LastUpdatedTime);
