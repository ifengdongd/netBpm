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
