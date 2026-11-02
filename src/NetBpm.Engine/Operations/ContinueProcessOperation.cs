using NetBpm.Bpmn.Model;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 推进执行：对流程节点执行其行为，对顺序流则跳转到目标节点。
/// 对应 Activiti 的 <c>ContinueProcessOperation</c>。
/// <para>
/// 子集裁剪：无子流程、无边界事件（见 §1.3 的第 1 / 2 条）。
/// 支持独占异步作业：<c>asyncBefore</c> 时本操作只落作业并返回，由 JobExecutor 在后续命令里重新进入。
/// </para>
/// </summary>
public sealed class ContinueProcessOperation : IOperation
{
    private readonly IExecutionEntity _execution;
    private readonly IEngineServices _services;
    private readonly bool _skipAsyncBefore;

    /// <param name="execution">要推进的执行。</param>
    /// <param name="services">引擎服务。</param>
    /// <param name="skipAsyncBefore">
    /// 仅为 <c>true</c> 时由异步作业恢复执行：表示「这次进入活动是作业推进的」，
    /// 不能再落一条 <c>asyncBefore</c> 作业，否则自我循环。
    /// </param>
    public ContinueProcessOperation(
        IExecutionEntity execution,
        IEngineServices services,
        bool skipAsyncBefore = false)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        _execution = execution;
        _services = services;
        _skipAsyncBefore = skipAsyncBefore;
    }

    /// <inheritdoc />
    public string Name => "ContinueProcessOperation";

    /// <inheritdoc />
    public async ValueTask RunAsync(CancellationToken ct = default)
    {
        // 终止结束事件会在同一命令里删掉整棵执行树；队列里排着的后续操作必须变成空操作
        if (_execution.IsEnded)
        {
            return;
        }

        var currentFlowElement = _execution.CurrentFlowElement
            ?? throw new EngineException(
                "Programmatic error: no current flow element found or invalid type. Halting.");

        // asyncBefore：命令提交时就停在这里，活动行为（含历史开始、任务创建、服务调用）
        // 交给 JobExecutor 在独立事务里执行。作业与「执行停在活动上」在同一事务，天然一致。
        // 用 FlowNode 而不是 Activity 判定：异步断点也可以写在**网关**上（Activiti 的 async 语义），
        // 只认 Activity 会让网关上的配置静默失效。
        if (!_skipAsyncBefore
            && currentFlowElement is FlowNode { AsyncBefore: true } asyncActivity)
        {
            await AsyncJobScheduler.CreateAsync(
                _services,
                _execution,
                asyncActivity,
                AsyncJobPhase.Before,
                ct).ConfigureAwait(false);

            return;
        }

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
