using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 维度③「事件序列」的采集器（开发计划 §9.2）。
///
/// <para>
/// 🔑 <b>为什么用"装饰历史管理器"而不是给引擎加事件总线</b>：引擎已经在**正确的时机**
/// 逐点调用 <see cref="IHistoryManager"/>（启动、节点进出、任务创建/结束、变量增改、实例收口）。
/// 装饰它即可拿到同一时刻的动作序列，<b>零引擎改动</b>；而"执行到达顺序流时收口源节点"这一调用
/// 恰好对应 Activiti 的 <c>SEQUENCEFLOW_TAKEN</c>（见 <c>TakeOutgoingSequenceFlowsOperation.handleSequenceFlow</c>），
/// 于是"选了哪条流"这个维度③最有价值的信息也拿得到 —— 而它在 <c>ACT_*</c> 里是<b>没有</b>的
/// （§15.4 H4：顺序流不产生活动历史行），不会与维度②重复。
/// </para>
///
/// <para>
/// ⚠️ <b>口径校准声明</b>：事件类型名一律取 Activiti 的 <c>ActivitiEventType</c>，但两端的
/// <b>触发时机与集合尚未经真机跑批校准</b>。首次跑批后必须按差异清单逐条确认（是口径差异还是真实缺陷），
/// 校准结论应回写到本节与本文件。
/// </para>
/// </summary>
public sealed class TraceRecordingHistoryManager : IHistoryManager
{
    private readonly IHistoryManager _inner;
    private readonly List<TraceEvent> _events = [];
    private readonly Lock _syncRoot = new();

    public TraceRecordingHistoryManager(IHistoryManager inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>已记录的事件序列（按发生顺序）。</summary>
    public IReadOnlyList<TraceEvent> Events
    {
        get
        {
            lock (_syncRoot)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>
    /// 清空已记录的事件。
    /// <para>⚠️ 必须在每个语料开跑前调用：采集器是引擎级的长生命周期对象，
    /// 不清空会让上一个语料的事件串进下一个（表现为"事件数翻倍"）。</para>
    /// </summary>
    public void Reset()
    {
        lock (_syncRoot)
        {
            _events.Clear();
        }
    }

    /// <inheritdoc />
    public bool IsHistoryEnabled => _inner.IsHistoryEnabled;

    public async ValueTask RecordProcessInstanceStartAsync(
        IExecutionEntity processInstance,
        string? startActivityId,
        CancellationToken ct = default)
    {
        // ⚠️ 不填 activityId：Activiti 的 PROCESS_STARTED 是 ActivitiProcessStartedEvent，
        // 并不携带 activityId（真值比对 2026-10-12 确认）。两端口径必须一致。
        Add(new TraceEvent("PROCESS_STARTED"));

        await _inner.RecordProcessInstanceStartAsync(processInstance, startActivityId, ct).ConfigureAwait(false);
    }

    public async ValueTask RecordProcessInstanceEndAsync(
        string processInstanceId,
        string? deleteReason,
        string? currentFlowElementId,
        CancellationToken ct = default)
    {
        // 不填 activityId：Activiti 的 PROCESS_COMPLETED 不带 activityId
        Add(new TraceEvent("PROCESS_COMPLETED"));

        await _inner.RecordProcessInstanceEndAsync(processInstanceId, deleteReason, currentFlowElementId, ct)
            .ConfigureAwait(false);
    }

    public async ValueTask RecordActivityStartAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        Add(new TraceEvent("ACTIVITY_STARTED", execution.CurrentActivityId));

        await _inner.RecordActivityStartAsync(execution, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordActivityEndAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default)
    {
        // 🔑 这是"执行离开活动"的语义（唯一调用点：TakeOutgoingSequenceFlowsOperation
        // 的节点路径 + 排他网关行为内的 leave）—— 对应 Activiti 的 handleActivityEnd：
        // recordActivityEnd 与 ACTIVITY_COMPLETED 事件在同一处先后发生。
        // 根执行没有当前活动（它不参与推进），不产生事件。
        if (!string.IsNullOrEmpty(execution.CurrentActivityId))
        {
            Add(new TraceEvent("ACTIVITY_COMPLETED", execution.CurrentActivityId));
        }

        await _inner.RecordActivityEndAsync(execution, deleteReason, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask RecordActivityEndQuietlyAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default) =>
        _inner.RecordActivityEndQuietlyAsync(execution, deleteReason, ct);

    public async ValueTask RecordTaskCreatedAsync(
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct = default)
    {
        // 不填 activityId：Activiti 的 TASK_CREATED 只带实体（taskDefinitionKey）
        Add(new TraceEvent("TASK_CREATED", TaskDefinitionKey: task.TaskDefinitionKey));

        await _inner.RecordTaskCreatedAsync(task, execution, ct).ConfigureAwait(false);
    }

    /// <summary>回填 <c>ACT_HI_ACTINST.TASK_ID_</c> 是历史行维护动作，不是事件。</summary>
    public ValueTask RecordTaskIdAsync(TaskEntity task, CancellationToken ct = default) =>
        _inner.RecordTaskIdAsync(task, ct);

    public async ValueTask RecordTaskAssignmentAsync(TaskEntity task, CancellationToken ct = default)
    {
        // 不填 activityId：Activiti 的 TASK_ASSIGNED 同样只带实体
        Add(new TraceEvent("TASK_ASSIGNED", TaskDefinitionKey: task.TaskDefinitionKey));

        await _inner.RecordTaskAssignmentAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 历史任务行的字段同步是"维护动作"而非独立事件：它被认领/指派/更新等多条路径调用，
    /// 若映射成事件会制造大量假差异（Activiti 只在其中一部分路径分发 <c>TASK_ASSIGNED</c>）。
    /// </summary>
    public ValueTask RecordTaskFieldsChangedAsync(TaskEntity task, CancellationToken ct = default) =>
        _inner.RecordTaskFieldsChangedAsync(task, ct);

    public async ValueTask RecordTaskEndAsync(string taskId, string? deleteReason, CancellationToken ct = default)
    {
        // 端口只给到 taskId（拿不到 TASK_DEF_KEY_）⇒ 该字段留空；顺序信息仍然有效
        Add(new TraceEvent("TASK_COMPLETED"));

        await _inner.RecordTaskEndAsync(taskId, deleteReason, ct).ConfigureAwait(false);
    }

    public async ValueTask RecordVariableCreateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        Add(new TraceEvent("VARIABLE_CREATED", VariableName: variable.Name));

        await _inner.RecordVariableCreateAsync(variable, ct).ConfigureAwait(false);
    }

    public async ValueTask RecordVariableUpdateAsync(VariableInstance variable, CancellationToken ct = default)
    {
        Add(new TraceEvent("VARIABLE_UPDATED", VariableName: variable.Name));

        await _inner.RecordVariableUpdateAsync(variable, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RecordSequenceFlowTakenAsync(IExecutionEntity execution, CancellationToken ct = default)
    {
        // 走到这里说明执行正沿顺序流跳转（CurrentFlowElement 就是那条流）
        if (execution.CurrentFlowElement is Bpmn.Model.SequenceFlow flow)
        {
            Add(new TraceEvent("SEQUENCEFLOW_TAKEN", FlowId: flow.Id));
        }

        await _inner.RecordSequenceFlowTakenAsync(execution, ct).ConfigureAwait(false);
    }

    /// <summary>身份关系在 Activiti 侧是通用 <c>ENTITY_CREATED</c>，没有专属事件类型；维度②已覆盖，故不入序列。</summary>
    public ValueTask RecordIdentityLinkCreatedAsync(IdentityLinkRecord identityLink, CancellationToken ct = default) =>
        _inner.RecordIdentityLinkCreatedAsync(identityLink, ct);

    public ValueTask<IReadOnlyList<HistoricActivityInstance>> FindActivityInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) => _inner.FindActivityInstancesAsync(processInstanceId, ct);

    public ValueTask<HistoricProcessInstance?> FindProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default) => _inner.FindProcessInstanceAsync(processInstanceId, ct);

    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindTaskInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) => _inner.FindTaskInstancesAsync(processInstanceId, ct);

    public ValueTask<IReadOnlyList<HistoricProcessInstance>> FindProcessInstancesAsync(
        CancellationToken ct = default) => _inner.FindProcessInstancesAsync(ct);

    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindAllTaskInstancesAsync(
        CancellationToken ct = default) => _inner.FindAllTaskInstancesAsync(ct);

    public ValueTask<IReadOnlyList<HistoricVariableInstance>> FindVariableInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) => _inner.FindVariableInstancesAsync(processInstanceId, ct);

    private void Add(TraceEvent traceEvent)
    {
        lock (_syncRoot)
        {
            _events.Add(traceEvent);
        }
    }
}
