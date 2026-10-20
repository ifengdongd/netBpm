using Microsoft.Extensions.Logging;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 并行网关（与）行为：Fork / Join。
/// 对应 Activiti 的 <c>ParallelGatewayActivityBehavior</c>。
/// <para>
/// 🔑 <b>必须照搬的 Activiti 特有语义</b>（开发计划 §5.4 之二 / 风险 R1）：
/// </para>
/// <list type="number">
///   <item><description>先 <c>Inactivate()</c> 把自己置为非活动；</description></item>
///   <item><description>按「当前活动相同且非活动」的执行集合来计数；</description></item>
///   <item><description>期望值 = <b>入流条数</b>（不是「每条入流都到过」）；</description></item>
///   <item><description>判定用<b>总数相等</b>；</description></item>
///   <item><description>出口<b>不校验条件</b>（<c>evaluateConditions: false</c>）。</description></item>
/// </list>
/// <para>
/// 类注释中 Activiti 明确说明了这是对规范 p.436 的有意偏离：
/// 「若两条 token 从同一条入流到达，我们的实现也会激活网关」。
/// </para>
/// </summary>
public sealed class ParallelGatewayActivityBehavior : GatewayActivityBehavior
{
    public ParallelGatewayActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        // ① 先把自己置为非活动
        execution.Inactivate();

        var parallelGateway = execution.CurrentFlowElement as ParallelGateway
            ?? throw new EngineException(
                "Programmatic error: parallel gateway behaviour can only be applied to a ParallelGateway instance, " +
                $"but got an instance of {execution.CurrentFlowElement}");

        await LockFirstParentScopeAsync(execution, ct).ConfigureAwait(false);

        var processInstanceId = execution.ProcessInstanceId
            ?? throw new EngineException($"并行网关执行 '{execution.Id}' 缺少 PROC_INST_ID_。");

        // ② 查「当前活动为同一个 gateway、且处于非活动状态」的执行 —— Join 的判定基础
        var joinedExecutions = await Services.ExecutionManager
            .FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(
                execution.CurrentActivityId!,
                processInstanceId,
                ct)
            .ConfigureAwait(false);

        // ③ 期望值 = 入流条数
        var numberOfExecutionsToJoin = parallelGateway.IncomingFlows.Count;

        // ⚠️ 只收口历史行、**不发** ACTIVITY_COMPLETED：Activiti 在这里的调用
        // （"Is needed to set the endTime for all historic activity joins"）不分发事件 ——
        // Join 完成的事件由最后一个令牌放行时的 TakeOutgoing 发出（§15.10 R5）
        await Services.HistoryManager
            .RecordActivityEndQuietlyAsync(execution.AsEntity(), null, ct)
            .ConfigureAwait(false);

        // ⚠️ Activiti 的判定是「总数相等」。一旦某条入流送来两个 token（环路重复经过、
        // 上游误连成多入口），计数会大于期望值且永远不会回落 —— 流程静默卡死。
        // 语义不改（对拍口径），但必须留下可诊断的痕迹，否则线上只能看到"流程不动了"。
        if (joinedExecutions.Count > numberOfExecutionsToJoin)
        {
            Services.Logger?.LogWarning(
                "并行网关 {GatewayId}（流程实例 {ProcessInstanceId}）收到 {Actual} 个 token，超过入流数 {Expected}：" +
                "Join 永远无法满足，流程会停在该网关。请检查网关是否位于环路或同一条入流被重复触发。",
                parallelGateway.Id,
                processInstanceId,
                joinedExecutions.Count,
                numberOfExecutionsToJoin);
        }

        // ④ 全部到齐 → Fork
        if (joinedExecutions.Count == numberOfExecutionsToJoin)
        {
            if (parallelGateway.IncomingFlows.Count > 1)
            {
                // 所有（此刻已是非活动的）子执行都被删除；当前执行被复用，不删
                foreach (var joinedExecution in joinedExecutions)
                {
                    if (!string.Equals(joinedExecution.Id, execution.Id, StringComparison.Ordinal))
                    {
                        await Services.ExecutionManager
                            .DeleteExecutionAndRelatedDataAsync(joinedExecution, null, ct)
                            .ConfigureAwait(false);
                    }
                }
            }

            // ⑤ 并行网关出口不做条件判定
            Services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(execution.AsEntity(), evaluateConditions: false);
        }

        // 否则：什么都不做，静默等待后续执行到达
    }
}
