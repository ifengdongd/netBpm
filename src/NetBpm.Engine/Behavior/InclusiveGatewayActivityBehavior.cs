using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 包含网关（或）行为：Fork / Join 双向。对应 Activiti 的 <c>InclusiveGatewayActivityBehavior</c>。
/// <para>
/// 🔑 <b>与并行网关的差异</b>：
/// </para>
/// <list type="number">
///   <item><description><b>Fork</b>：出口要<b>判定条件</b> —— 取全部条件为真的出流（可能多条），
///   一条都没有时走默认流；这与并行网关「不判条件、全部分支」不同；</description></item>
///   <item><description><b>Join</b>：期望值不是「入流条数」，而是「<b>仍处于活动、且沿图仍能到达本网关</b>的执行数 + 1」
///   —— 没被激活的分支不该阻塞 Join（这正是包含网关存在的意义）；</description></item>
///   <item><description>判定用 <c>&gt;=</c>：历史执行/循环回路下宁可放行也不死锁（与 Activiti 一致）。</description></item>
/// </list>
/// </summary>
public sealed class InclusiveGatewayActivityBehavior : GatewayActivityBehavior
{
    public InclusiveGatewayActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var inclusiveGateway = execution.CurrentFlowElement as InclusiveGateway
            ?? throw new EngineException(
                "Programmatic error: inclusive gateway behaviour can only be applied to an InclusiveGateway instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        // 只有一条入流 ⇒ 纯 Fork（无可合并的分支）
        if (inclusiveGateway.IncomingFlows.Count <= 1)
        {
            // 出口判定条件（可能同时走多条），见 LeaveAsync
            await LeaveAsync(execution, ct).ConfigureAwait(false);
            return;
        }

        // ── Join ──
        // ① 先把自己置为非活动：自己也算「已到达本网关」的一员
        execution.Inactivate();

        await LockFirstParentScopeAsync(execution, ct).ConfigureAwait(false);

        var processInstanceId = execution.ProcessInstanceId
            ?? throw new EngineException($"包含网关执行 '{execution.Id}' 缺少 PROC_INST_ID_。");

        var activityId = execution.CurrentActivityId
            ?? throw new EngineException($"包含网关执行 '{execution.Id}' 缺少当前活动 id。");

        // ② 已到达（非活动、停在本网关）的执行集合
        var joinedExecutions = await Services.ExecutionManager
            .FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(activityId, processInstanceId, ct)
            .ConfigureAwait(false);

        // ③ 仍在活动、且「沿图还能到达本网关」的执行 ⇒ 必须一并等待
        var process = inclusiveGateway.ParentContainer
            ?? throw new EngineException($"包含网关 '{inclusiveGateway.Id}' 未挂载到流程上。");

        var activeExecutions = await Services.ExecutionManager
            .FindActiveChildExecutionsByProcessInstanceIdAsync(processInstanceId, ct)
            .ConfigureAwait(false);

        // 自己也算一个（稍后放行时由自己继续）
        var numberOfExecutionsToJoin = 1;

        foreach (var activeExecution in activeExecutions)
        {
            if (string.Equals(activeExecution.Id, execution.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var currentElement = activeExecution.CurrentFlowElement;

            if (currentElement is IFlowNode flowNode
                && ExecutionGraphUtil.IsReachable(process, flowNode, inclusiveGateway))
            {
                numberOfExecutionsToJoin++;
            }
        }

        // ④ 只收口历史行、不发 ACTIVITY_COMPLETED（与并行网关 Join 一致，见 §15.10 R5）
        await Services.HistoryManager
            .RecordActivityEndQuietlyAsync(execution.AsEntity(), null, ct)
            .ConfigureAwait(false);

        // ⑤ 到齐 → 合并后按条件放行
        if (joinedExecutions.Count >= numberOfExecutionsToJoin)
        {
            foreach (var joinedExecution in joinedExecutions)
            {
                if (!string.Equals(joinedExecution.Id, execution.Id, StringComparison.Ordinal))
                {
                    await Services.ExecutionManager
                        .DeleteExecutionAndRelatedDataAsync(joinedExecution, null, ct)
                        .ConfigureAwait(false);
                }
            }

            await LeaveAsync(execution, ct).ConfigureAwait(false);
        }

        // 否则：静默等待其它分支到达
    }

    /// <summary>
    /// 包含网关的离开：**要判定条件**（可能同时走多条出流），
    /// 一条都选不中时由 <c>TakeOutgoingSequenceFlowsOperation</c> 走默认流或报错。
    /// </summary>
    public override ValueTask LeaveAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        Services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(execution.AsEntity(), evaluateConditions: true);
        return ValueTask.CompletedTask;
    }
}
