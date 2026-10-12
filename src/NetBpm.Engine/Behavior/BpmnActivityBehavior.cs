using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 「离开节点」的默认行为集合。对应 Activiti 的 <c>BpmnActivityBehavior</c>。
/// </summary>
public sealed class BpmnActivityBehavior
{
    private readonly IEngineServices _services;

    public BpmnActivityBehavior(IEngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>
    /// 默认离开行为：校验出流条件，走那些条件为真的流。
    /// 对应 <c>performDefaultOutgoingBehavior</c>。
    /// </summary>
    public ValueTask PerformDefaultOutgoingBehaviorAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(execution.AsEntity(), evaluateConditions: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 忽略条件的离开行为。对应 <c>performIgnoreConditionsOutgoingBehavior</c>。
    /// </summary>
    public ValueTask PerformIgnoreConditionsOutgoingBehaviorAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        _services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(execution.AsEntity(), evaluateConditions: false);
        return ValueTask.CompletedTask;
    }
}
