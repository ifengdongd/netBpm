using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 排他网关（异或）行为。对应 Activiti 的 <c>ExclusiveGatewayActivityBehavior</c>。
/// <para>
/// 🔑 <b>必须照搬的 Activiti 特有语义</b>（开发计划 §5.4 之一）：取<b>第一个</b>条件为真、
/// 且不是默认流的出流 —— 这与 BPMN 规范「所有条件为真的流都要走（且建议报错）」<b>不同</b>。
/// Activiti <b>没有</b>「多个条件同时为真则报错」的检查，请勿顺手补上，否则两端行为不一致。
/// </para>
/// </summary>
public sealed class ExclusiveGatewayActivityBehavior : GatewayActivityBehavior
{
    public ExclusiveGatewayActivityBehavior(Services.IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask LeaveAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var exclusiveGateway = execution.CurrentFlowElement as ExclusiveGateway
            ?? throw new EngineException(
                "Programmatic error: exclusive gateway behaviour can only be applied to an ExclusiveGateway instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        SequenceFlow? outgoingSequenceFlow = null;
        SequenceFlow? defaultSequenceFlow = null;
        var defaultSequenceFlowId = exclusiveGateway.DefaultFlow;

        // 先把所有出流条件里的变量一次取回，避免逐条件 / 逐作用域往返（N+1）
        await PrefetchConditionVariablesAsync(exclusiveGateway, execution, ct).ConfigureAwait(false);

        // 判定要走哪条流。注意：一旦选中就不再继续判定条件（与 Activiti 的 while 循环等价）
        foreach (var sequenceFlow in exclusiveGateway.OutgoingFlows)
        {
            if (outgoingSequenceFlow is null)
            {
                var conditionEvaluatesToTrue = await ConditionUtil
                    .HasTrueConditionAsync(sequenceFlow, execution, Services.ExpressionManager, ct)
                    .ConfigureAwait(false);

                if (conditionEvaluatesToTrue
                    && (defaultSequenceFlowId is null
                        || !string.Equals(defaultSequenceFlowId, sequenceFlow.Id, StringComparison.Ordinal)))
                {
                    outgoingSequenceFlow = sequenceFlow;
                }
            }

            // 先记下来，后面可能要用（省一次循环）
            if (defaultSequenceFlowId is not null
                && string.Equals(defaultSequenceFlowId, sequenceFlow.Id, StringComparison.Ordinal))
            {
                defaultSequenceFlow = sequenceFlow;
            }
        }

        // 必须在这里记录结束，否则就已经越过了
        await Services.HistoryManager
            .RecordActivityEndAsync(execution.AsEntity(), null, ct)
            .ConfigureAwait(false);

        if (outgoingSequenceFlow is not null)
        {
            execution.CurrentFlowElement = outgoingSequenceFlow;
        }
        else if (defaultSequenceFlow is not null)
        {
            execution.CurrentFlowElement = defaultSequenceFlow;
        }
        else
        {
            throw new EngineException(
                $"No outgoing sequence flow of the exclusive gateway '{exclusiveGateway.Id}' " +
                "could be selected for continuing the process");
        }

        // GatewayActivityBehavior.leave：忽略条件地离开（当前元素已是选中的顺序流）
        await base.LeaveAsync(execution, ct).ConfigureAwait(false);
    }
}
