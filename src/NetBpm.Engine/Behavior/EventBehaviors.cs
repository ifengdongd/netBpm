using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 空开始事件行为。对应 Activiti 的 <c>NoneStartEventActivityBehavior</c>。
/// 语义即默认行为：直接离开（沿唯一出流前进）。
/// </summary>
public sealed class NoneStartEventActivityBehavior : FlowNodeActivityBehavior
{
    public NoneStartEventActivityBehavior(IEngineServices services)
        : base(services)
    {
    }
}

/// <summary>
/// 定时边界事件行为。对应 Activiti 的 <c>BoundaryEventActivityBehavior</c>。
/// <para>
/// 定时器触发时，<see cref="Command.TriggerTimerJobCommand"/> 已把执行挪到边界事件上；
/// 本行为只负责离开（沿边界事件的出流继续，默认行为即如此）。
/// 非中断型（<c>cancelActivity="false"</c>）部署期已拒绝，因此这里不需要分叉。
/// </para>
/// </summary>
public sealed class BoundaryEventActivityBehavior : FlowNodeActivityBehavior
{
    public BoundaryEventActivityBehavior(IEngineServices services)
        : base(services)
    {
    }
}

/// <summary>
/// 终止结束事件行为。对应 Activiti 的 <c>TerminateEndEventActivityBehavior</c>。
/// <para>
/// 语义：<b>立即终止整个流程实例</b> —— 删除执行树、取消全部待办、收口历史，
/// 并行/会签分支一并作废。本子集无子流程，因此不存在「只终止当前作用域」的分支。
/// </para>
/// <para>
/// 与正常结束的区别：<c>ACT_HI_PROCINST.DELETE_REASON_</c> 写
/// <c>terminate end event</c>，<c>END_ACT_ID_</c> 指向终止结束事件；
/// 监听器的 <c>PROCESS_COMPLETED</c> 不触发（被终止不是正常完成）。
/// </para>
/// </summary>
public sealed class TerminateEndEventActivityBehavior : FlowNodeActivityBehavior
{
    /// <summary>终止原因（写入 <c>ACT_HI_PROCINST.DELETE_REASON_</c>）。</summary>
    public const string DeleteReason = "terminate end event";

    public TerminateEndEventActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var processInstanceId = execution.ProcessInstanceId ?? execution.Id;

        await Services.HistoryManager
            .RecordActivityEndQuietlyAsync(execution.AsEntity(), DeleteReason, ct)
            .ConfigureAwait(false);

        await Services.ExecutionManager
            .DeleteProcessInstanceExecutionEntityAsync(
                processInstanceId,
                execution.CurrentFlowElement?.Id ?? execution.CurrentActivityId,
                DeleteReason,
                cancel: true,
                ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// 空结束事件行为。对应 Activiti 的 <c>NoneEndEventActivityBehavior</c>。
/// <para>
/// ⚠️ 注意 Activiti 的实现：它<b>并不直接结束执行</b>，而是规划一次
/// <c>TakeOutgoingSequenceFlowsOperation(execution, true)</c>；由于结束事件没有出流，
/// 该操作会在 <c>leaveFlowNode</c> 里走到「无出流 → <c>planEndExecutionOperation</c>」这一分支，
/// 从而结束执行。本实现逐条照搬，以确保执行树与历史记录顺序与 Activiti 一致。
/// </para>
/// </summary>
public sealed class NoneEndEventActivityBehavior : FlowNodeActivityBehavior
{
    public NoneEndEventActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        // Activiti：planTakeOutgoingSequenceFlowsOperation(execution, true)
        BpmnActivityBehavior.PerformDefaultOutgoingBehaviorAsync(execution, ct);
}
