using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.UnitTests;

/// <summary>
/// 只记录调用顺序的历史管理器（不落库）。
/// <para>用途：把「历史写入发生在哪个时机」钉死 —— 真库集成测试验证行内容，这里验证调用时机。</para>
/// </summary>
internal sealed class RecordingHistoryManager : IHistoryManager
{
    private readonly List<string> _calls = [];
    private readonly List<HistoricTaskInstance> _tasks = [];

    /// <summary>已记录的调用，形如 <c>ActivityStart:approve</c>。</summary>
    public IReadOnlyList<string> Calls => _calls;

    public bool IsHistoryEnabled => true;

    public ValueTask RecordProcessInstanceStartAsync(
        IExecutionEntity processInstance,
        string? startActivityId,
        CancellationToken ct = default)
    {
        _calls.Add($"ProcessInstanceStart:{startActivityId}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordProcessInstanceEndAsync(
        string processInstanceId,
        string? deleteReason,
        string? currentFlowElementId,
        CancellationToken ct = default)
    {
        _calls.Add($"ProcessInstanceEnd:{currentFlowElementId}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordActivityStartAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        _calls.Add($"ActivityStart:{execution.CurrentActivityId}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordActivityEndAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        // 与 Activiti 一致：顺序流收口的是「源节点」那一行
        var activityId = execution.CurrentFlowElement is Bpmn.Model.SequenceFlow flow
            ? flow.SourceRef
            : execution.CurrentActivityId;

        _calls.Add($"ActivityEnd:{activityId}");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RecordSequenceFlowTakenAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        _calls.Add("SequenceFlowTaken");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RecordActivityEndQuietlyAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        _calls.Add($"ActivityEndQuiet:{execution.CurrentActivityId}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTaskCreatedAsync(
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct = default)
    {
        _calls.Add($"TaskCreated:{task.Id}");

        // 顺带存一份最小历史，供「历史任务查询 / 分页」的门面测试使用
        // （真库行内容由集成测试验证，这里只保证读回的形状与真库一致）
        _tasks.Add(new HistoricTaskInstance(
            task.Id,
            task.ProcessDefinitionId,
            task.TaskDefinitionKey,
            task.ProcessInstanceId,
            task.ExecutionId,
            task.Name,
            task.Assignee,
            task.Owner,
            task.Priority,
            task.FormKey,
            task.CreateTime ?? DateTime.UtcNow,
            null,
            null,
            null));

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTaskIdAsync(TaskEntity task, CancellationToken ct = default)
    {
        _calls.Add($"TaskId:{task.Id}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTaskAssignmentAsync(TaskEntity task, CancellationToken ct = default)
    {
        _calls.Add($"TaskAssignment:{task.Id}:{task.Assignee}");

        // 办理人是在任务创建之后才写进历史行的（ACT_HI_TASKINST.ASSIGNEE_ 随认领 / 改派更新）
        var index = _tasks.FindIndex(item => string.Equals(item.Id, task.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _tasks[index] = _tasks[index] with { Assignee = task.Assignee, Owner = task.Owner };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTaskFieldsChangedAsync(TaskEntity task, CancellationToken ct = default)
    {
        _calls.Add($"TaskFieldsChanged:{task.Id}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTaskEndAsync(string taskId, string? deleteReason, CancellationToken ct = default)
    {
        _calls.Add($"TaskEnd:{taskId}");

        var index = _tasks.FindIndex(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _tasks[index] = _tasks[index] with
            {
                EndTime = DateTime.UtcNow,
                DeleteReason = deleteReason,
            };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordVariableCreateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        _calls.Add($"VariableCreate:{variable.Name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordVariableUpdateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        _calls.Add($"VariableUpdate:{variable.Name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordIdentityLinkCreatedAsync(
        IdentityLinkRecord identityLink,
        CancellationToken ct = default)
    {
        _calls.Add($"IdentityLinkCreated:{identityLink.Type}:{identityLink.UserId ?? identityLink.GroupId}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<HistoricActivityInstance>> FindActivityInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricActivityInstance>>([]);

    public ValueTask<HistoricProcessInstance?> FindProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default) => ValueTask.FromResult<HistoricProcessInstance?>(null);

    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindTaskInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricTaskInstance>>(
            [.. _tasks.Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))]);

    public ValueTask<IReadOnlyList<HistoricProcessInstance>> FindProcessInstancesAsync(
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricProcessInstance>>([]);

    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindAllTaskInstancesAsync(
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricTaskInstance>>([.. _tasks]);

    public ValueTask<IReadOnlyList<HistoricVariableInstance>> FindVariableInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricVariableInstance>>([]);
}
