using NetBpm.Engine.Tasks;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Tasks;

/// <summary>
/// <c>ACT_RU_TASK</c> 一行的持久化状态（行 → 实体映射与 UPDATE 参数来源）。
/// </summary>
internal sealed record TaskState(
    string Id,
    int Revision,
    string? Name,
    string? BusinessKey,
    string? ParentTaskId,
    string? Description,
    int Priority,
    DateTime? CreateTime,
    string? Owner,
    string? Assignee,
    string? Delegation,
    string? ExecutionId,
    string? ProcessInstanceId,
    string? ProcessDefinitionId,
    string? TaskDefinitionKey,
    DateTime? DueDate,
    string? Category,
    int SuspensionState,
    string? TenantId,
    string? FormKey,
    DateTime? ClaimTime)
{
    public static TaskState From(TaskEntity task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskState(
            task.Id,
            task.Revision,
            task.Name,
            task.BusinessKey,
            task.ParentTaskId,
            task.Description,
            task.Priority,
            task.CreateTime,
            task.Owner,
            task.Assignee,
            task.DelegationStateText,
            task.ExecutionId,
            task.ProcessInstanceId,
            task.ProcessDefinitionId,
            task.TaskDefinitionKey,
            task.DueDate,
            task.Category,
            task.SuspensionState,
            task.TenantId,
            task.FormKey,
            task.ClaimTime);
    }

    public TaskEntity ToEntity() => new()
    {
        Id = Id,
        Revision = Revision,
        Name = Name,
        BusinessKey = BusinessKey,
        ParentTaskId = ParentTaskId,
        Description = Description,
        Priority = Priority,
        CreateTime = CreateTime,
        Owner = Owner,
        Assignee = Assignee,
        DelegationState = Delegation switch
        {
            "PENDING" => NetBpm.Engine.Tasks.DelegationState.Pending,
            "RESOLVED" => NetBpm.Engine.Tasks.DelegationState.Resolved,
            _ => null,
        },
        ExecutionId = ExecutionId,
        ProcessInstanceId = ProcessInstanceId,
        ProcessDefinitionId = ProcessDefinitionId,
        TaskDefinitionKey = TaskDefinitionKey,
        DueDate = DueDate,
        Category = Category,
        SuspensionState = SuspensionState,
        TenantId = TenantId,
        FormKey = FormKey,
        ClaimTime = ClaimTime,
    };
}

/// <summary><c>ACT_RU_TASK</c> 的列名与取值。</summary>
internal static class TaskColumns
{
    /// <summary>与 Activiti <c>Task.xml</c> 的 resultMap 一致的列清单。</summary>
    public const string All =
        "ID_, REV_, NAME_, BUSINESS_KEY_, PARENT_TASK_ID_, DESCRIPTION_, PRIORITY_, CREATE_TIME_, OWNER_, " +
        "ASSIGNEE_, DELEGATION_, EXECUTION_ID_, PROC_INST_ID_, PROC_DEF_ID_, TASK_DEF_KEY_, DUE_DATE_, " +
        "CATEGORY_, SUSPENSION_STATE_, TENANT_ID_, FORM_KEY_, CLAIM_TIME_, APP_VERSION_";

    public static TaskState Read(IDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TaskState(
            Id: Text(row, "ID_") ?? throw new Engine.EngineException("ACT_RU_TASK 行缺少 ID_。"),
            Revision: Number(row, "REV_") ?? 0,
            Name: Text(row, "NAME_"),
            BusinessKey: Text(row, "BUSINESS_KEY_"),
            ParentTaskId: Text(row, "PARENT_TASK_ID_"),
            Description: Text(row, "DESCRIPTION_"),
            Priority: Number(row, "PRIORITY_") ?? 0,
            CreateTime: NullableDate(row, "CREATE_TIME_"),
            Owner: Text(row, "OWNER_"),
            Assignee: Text(row, "ASSIGNEE_"),
            Delegation: Text(row, "DELEGATION_"),
            ExecutionId: Text(row, "EXECUTION_ID_"),
            ProcessInstanceId: Text(row, "PROC_INST_ID_"),
            ProcessDefinitionId: Text(row, "PROC_DEF_ID_"),
            TaskDefinitionKey: Text(row, "TASK_DEF_KEY_"),
            DueDate: NullableDate(row, "DUE_DATE_"),
            Category: Text(row, "CATEGORY_"),
            SuspensionState: Number(row, "SUSPENSION_STATE_") ?? 0,
            TenantId: Text(row, "TENANT_ID_"),
            FormKey: Text(row, "FORM_KEY_"),
            ClaimTime: NullableDate(row, "CLAIM_TIME_"));
    }
}
