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

        // 需要为所有历史 activity join 设置 endTime
        await Services.HistoryManager
            .RecordActivityEndAsync(execution.AsEntity(), null, ct)
            .ConfigureAwait(false);

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
