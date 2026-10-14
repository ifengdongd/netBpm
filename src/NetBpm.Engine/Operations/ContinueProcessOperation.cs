using NetBpm.Bpmn.Model;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 推进执行：对流程节点执行其行为，对顺序流则跳转到目标节点。
/// 对应 Activiti 的 <c>ContinueProcessOperation</c>。
/// <para>
/// 子集裁剪：无子流程、多实例、异步作业、边界事件（见 §1.3 的第 1 / 2 / 5 / 7 条）。
/// 因此 Activiti 的 <c>executeSynchronous</c> 之外的分支全部不存在。
/// </para>
/// </summary>
public sealed class ContinueProcessOperation : IOperation
{
    private readonly IExecutionEntity _execution;
    private readonly IEngineServices _services;

    public ContinueProcessOperation(IExecutionEntity execution, IEngineServices services)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        _execution = execution;
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "ContinueProcessOperation";

    /// <inheritdoc />
    public async ValueTask RunAsync(CancellationToken ct = default)
    {
        var currentFlowElement = _execution.CurrentFlowElement
            ?? throw new EngineException(
                "Programmatic error: no current flow element found or invalid type. Halting.");

        switch (currentFlowElement)
        {
            case IFlowNode flowNode:
                await ExecuteSynchronousAsync(flowNode, ct).ConfigureAwait(false);
                break;

            case SequenceFlow sequenceFlow:
                await ContinueThroughSequenceFlowAsync(sequenceFlow, ct).ConfigureAwait(false);
                break;

            default:
                throw new EngineException(
                    "Programmatic error: no current flow element found or invalid type: " +
                    $"'{currentFlowElement.GetType().Name}'. Halting.");
        }
    }

    /// <summary>对应 Activiti 的 <c>executeSynchronous</c>。</summary>
    private async ValueTask ExecuteSynchronousAsync(IFlowNode flowNode, CancellationToken ct)
    {
        await _services.HistoryManager.RecordActivityStartAsync(_execution, ct).ConfigureAwait(false);

        // 执行监听器（activiti:executionListener，event="start"）：行为执行前逐个通知
        await ListenerInvoker
            .NotifyExecutionListenersAsync(_services, _execution, (FlowElement)flowNode, ListenerInvoker.StartEvent, ct)
            .ConfigureAwait(false);

        await EngineEventDispatcher
            .DispatchAsync(_services, _execution, EngineEvents.ActivityStarted, flowNode.Id, ct)
            .ConfigureAwait(false);

        // 流程级执行监听器（全局观测）：元素级之后通知（对齐 Activiti 设计器「流程上配监听器」用法）
        await ListenerInvoker.NotifyProcessListenersAsync(_services, _execution, ListenerInvoker.StartEvent, ct).ConfigureAwait(false);
        var behavior = _services.ActivityBehaviorFactory.Create(flowNode);
        await behavior.ExecuteAsync(_execution, ct).ConfigureAwait(false);
    }

    /// <summary>对应 Activiti 的 <c>continueThroughSequenceFlow</c>。</summary>
    private async ValueTask ContinueThroughSequenceFlowAsync(SequenceFlow sequenceFlow, CancellationToken ct)
    {
        var targetFlowElement = sequenceFlow.TargetFlowNode as IFlowElement
            ?? sequenceFlow.ParentContainer?.GetFlowElement(sequenceFlow.TargetRef ?? string.Empty)
            ?? throw new EngineException(
                $"顺序流 '{sequenceFlow.Id}' 的 targetRef '{sequenceFlow.TargetRef}' 无法解析到流程元素。");

        // ⚠️ 必须在改写 CurrentFlowElement **之前**记录：端口靠"当前元素是顺序流"判断语义，
        // 改成目标节点后就再也拿不到"选了哪条流"（2026-10-12 真值比对时我们整段缺失）。
        await _services.HistoryManager
            .RecordSequenceFlowTakenAsync(_execution, ct)
            .ConfigureAwait(false);

        // 执行监听器（activiti:executionListener，event="take"）：挂在顺序流上，
        // 在"穿越顺序流"处恰好触发一次（网关路径与普通路径都经这里，不会双发）
        await ListenerInvoker
            .NotifyExecutionListenersAsync(_services, _execution, sequenceFlow, ListenerInvoker.TakeEvent, ct)
            .ConfigureAwait(false);

        // 流程级执行监听器（全局观测）：take 随元素级一起，恰好一次
        await ListenerInvoker.NotifyProcessListenersAsync(_services, _execution, ListenerInvoker.TakeEvent, ct).ConfigureAwait(false);
        _execution.CurrentFlowElement = targetFlowElement;

        _services.Agenda.PlanContinueProcessOperation(_execution);
    }
}
