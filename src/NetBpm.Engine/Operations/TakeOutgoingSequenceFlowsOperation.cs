using NetBpm.Bpmn.Model;
using NetBpm.Engine.Agenda;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 离开当前元素并沿出流前进。对应 Activiti 的 <c>TakeOutgoingSequenceFlowsOperation</c>。
/// <para>
/// 🔑 这是<b>分支产生的唯一位置</b>（开发计划 §5.5）：第一条出流沿用当前执行（reuse），
/// 其余每条流新建一个并发子执行；全部建完后才统一推进。
/// </para>
/// </summary>
public sealed class TakeOutgoingSequenceFlowsOperation : IOperation
{
    private readonly IExecutionEntity _execution;
    private readonly IEngineServices _services;
    private readonly bool _evaluateConditions;
    private readonly bool _skipAsyncAfter;

    /// <param name="execution">要离开的执行。</param>
    /// <param name="services">引擎服务。</param>
    /// <param name="evaluateConditions">是否按条件选择出流（网关 / 条件流）。</param>
    /// <param name="skipAsyncAfter">
    /// 仅为 <c>true</c> 时由 <c>asyncAfter</c> 作业恢复：表示已经过异步断点，不能再落新作业。
    /// </param>
    public TakeOutgoingSequenceFlowsOperation(
        IExecutionEntity execution,
        IEngineServices services,
        bool evaluateConditions,
        bool skipAsyncAfter = false)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(services);

        _execution = execution;
        _services = services;
        _evaluateConditions = evaluateConditions;
        _skipAsyncAfter = skipAsyncAfter;
    }

    /// <inheritdoc />
    public string Name => "TakeOutgoingSequenceFlowsOperation";

    /// <inheritdoc />
    public async ValueTask RunAsync(CancellationToken ct = default)
    {
        // 终止结束事件会在同一命令里删掉整棵执行树（含并行分支上的执行）
        if (_execution.IsEnded)
        {
            return;
        }

        var currentFlowElement = _execution.CurrentFlowElement
            ?? throw new EngineException($"执行 '{_execution.Id}' 没有当前流程元素，无法离开。");

        // asyncAfter：活动行为已经执行完（例如服务调用、脚本），但「离开节点」推迟到作业里。
        // 这样历史结束、节点结束监听器与后续推进都在作业事务中完成，失败可重试。
        if (!_skipAsyncAfter
            && currentFlowElement is Activity { AsyncAfter: true } asyncActivity)
        {
            await AsyncJobScheduler.CreateAsync(
                _services,
                _execution,
                asyncActivity,
                AsyncJobPhase.After,
                ct).ConfigureAwait(false);

            return;
        }

        switch (currentFlowElement)
        {
            case IFlowNode flowNode:
                await HandleActivityEndAsync(flowNode, ct).ConfigureAwait(false);
                await LeaveFlowNodeAsync(flowNode, ct).ConfigureAwait(false);
                break;

            case SequenceFlow:
                await HandleSequenceFlowAsync(ct).ConfigureAwait(false);
                break;

            default:
                throw new EngineException(
                    $"不支持的流程元素类型 '{currentFlowElement.GetType().Name}'（执行 '{_execution.Id}'）。");
        }
    }

    /// <summary>
    /// 记录节点结束。对应 Activiti 的 <c>handleActivityEnd</c>。
    /// <para>子集裁剪：无边界事件。</para>
    /// </summary>
    private async ValueTask HandleActivityEndAsync(IFlowNode flowNode, CancellationToken ct)
    {
        // 流程实例执行永远不会「离开」某个节点，它只会在清理阶段经过这里
        if (_execution.IsProcessInstanceType)
        {
            return;
        }

        await _services.HistoryManager.RecordActivityEndAsync(_execution, null, ct).ConfigureAwait(false);

        // 执行监听器（activiti:executionListener，event="end"）：离开节点前逐个通知
        await ListenerInvoker
            .NotifyExecutionListenersAsync(_services, _execution, (FlowElement)flowNode, ListenerInvoker.EndEvent, ct)
            .ConfigureAwait(false);

        // 流程级执行监听器（全局观测）：离开节点前通知
        await ListenerInvoker.NotifyProcessListenersAsync(_services, _execution, ListenerInvoker.EndEvent, ct).ConfigureAwait(false);
        await EngineEventDispatcher
            .DispatchAsync(_services, _execution, EngineEvents.ActivityCompleted, flowNode.Id, ct)
            .ConfigureAwait(false);
    }

    private async ValueTask HandleSequenceFlowAsync(CancellationToken ct)
    {
        // ⚠️ 只收口历史行（源节点那一行，见 §15.4 H4），不发 ACTIVITY_COMPLETED ——
        // 若当前元素是顺序流，说明行为已经自己发过"离开"事件（如排他网关），
        // Activiti 在 handleSequenceFlow 里同样只有 recordActivityEnd。
        // ⚠️ take 监听器不在这里发：网关路径会二次穿越同一条顺序流，
        // take 统一在 ContinueProcessOperation.continueThroughSequenceFlow 处恰好触发一次。
        var recorded = _services.HistoryManager.RecordActivityEndQuietlyAsync(_execution, null, ct);
        _services.Agenda.PlanContinueProcessOperation(_execution);
        await recorded.ConfigureAwait(false);
    }

    private async ValueTask LeaveFlowNodeAsync(IFlowNode flowNode, CancellationToken ct)
    {
        var defaultSequenceFlowId = flowNode switch
        {
            Activity activity => activity.DefaultFlow,
            Gateway gateway => gateway.DefaultFlow,
            _ => null,
        };

        // 判定哪些出流可用（Activiti: leaveFlowNode 的判定循环，逐条照搬）
        var outgoingSequenceFlows = new List<SequenceFlow>(flowNode.OutgoingFlows.Count);

        foreach (var sequenceFlow in flowNode.OutgoingFlows)
        {
            if (!_evaluateConditions)
            {
                // 并行网关出口不做条件判定（§5.4 之二 ⑤）
                outgoingSequenceFlows.Add(sequenceFlow);
                continue;
            }

            var hasTrueCondition = await ConditionUtil
                .HasTrueConditionAsync(sequenceFlow, _execution, _services.ExpressionManager, ct)
                .ConfigureAwait(false);

            var isDefaultFlow = defaultSequenceFlowId is not null
                && string.Equals(defaultSequenceFlowId, sequenceFlow.Id, StringComparison.Ordinal);

            if (hasTrueCondition && !isDefaultFlow)
            {
                outgoingSequenceFlows.Add(sequenceFlow);
            }
        }

        // 默认流：仅当需要判定条件、且没有任何出流被选中时才使用
        if (outgoingSequenceFlows.Count == 0 && _evaluateConditions && defaultSequenceFlowId is not null)
        {
            foreach (var sequenceFlow in flowNode.OutgoingFlows)
            {
                if (string.Equals(defaultSequenceFlowId, sequenceFlow.Id, StringComparison.Ordinal))
                {
                    outgoingSequenceFlows.Add(sequenceFlow);
                    break;
                }
            }
        }

        if (outgoingSequenceFlows.Count == 0)
        {
            if (flowNode.OutgoingFlows.Count == 0)
            {
                // 无出流：结束该执行分支
                _services.Agenda.PlanEndExecutionOperation(_execution);
                return;
            }

            throw new EngineException(
                $"No outgoing sequence flow of element '{flowNode.Id}' could be selected for continuing the process");
        }

        // 复用现有执行走第一条流
        _execution.CurrentFlowElement = outgoingSequenceFlows[0];
        _execution.IsActive = true;

        var outgoingExecutions = new List<IExecutionEntity>(outgoingSequenceFlows.Count) { _execution };

        // 🔑 分支的父执行必须**先解析出来**：
        // Activiti 的 `executionEntity.getParent()` 是同步懒加载（直接从库里取），
        // 本移植版是异步仓储 —— 跨命令从库里读回的执行只带 PARENT_ID_，`Parent` 对象是空的。
        // 不解析就会出现"父执行对象为 null" → 内存宿主（Parent 恒有值）与数据库宿主行为分叉。
        var parent = await ResolveParentAsync(_execution, ct).ConfigureAwait(false);

        // 其余流：新建并发子执行
        for (var i = 1; i < outgoingSequenceFlows.Count; i++)
        {
            var child = _services.ExecutionManager.CreateChildExecution(parent);
            child.CurrentFlowElement = outgoingSequenceFlows[i];

            await _services.ExecutionManager.InsertAsync(child, ct).ConfigureAwait(false);
            outgoingExecutions.Add(child);
        }

        // Leave（全部建完后才统一推进 —— 某些查询依赖这一点）
        foreach (var outgoingExecution in outgoingExecutions)
        {
            _services.Agenda.PlanContinueProcessOperation(outgoingExecution);
        }
    }

    /// <summary>
    /// 取分支所用的父执行（等价于 Activiti 的 <c>getParent()</c> 懒加载）：
    /// 依次尝试「已挂载的父执行」→「按 PARENT_ID_ 从仓储读回」→「当前执行自身」。
    /// <para>
    /// 最后一步的兜底只在<b>流程实例执行的直接子执行</b>上触发（它的 PARENT_ID_ 指向流程实例，
    /// 而流程实例执行本身就是作用域），与 Activiti 的树形一致。
    /// </para>
    /// </summary>
    private static async ValueTask<IExecutionEntity> ResolveParentAsync(
        IExecutionEntity execution,
        CancellationToken ct)
    {
        if (execution.Parent is not null)
        {
            return execution.Parent;
        }

        if (execution.ParentId is null || execution.ParentLoader is null)
        {
            return execution;
        }

        var parent = await execution.ParentLoader.FindByIdAsync(execution.ParentId, ct).ConfigureAwait(false);

        if (parent is null)
        {
            return execution;
        }

        // 回填，后续分支/变量作用域上溯直接复用（同一命令内反复取父执行是常态）
        execution.Parent = parent;

        return parent;
    }
}
