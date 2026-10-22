using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 网关行为基类。对应 Activiti 的 <c>GatewayActivityBehavior</c>。
/// </summary>
public abstract class GatewayActivityBehavior : FlowNodeActivityBehavior
{
    protected GatewayActivityBehavior(IEngineServices services)
        : base(services)
    {
    }

    /// <summary>
    /// 网关离开时<b>不校验出流条件</b>（对应 Activiti 的
    /// <c>planTakeOutgoingSequenceFlowsOperation(execution, false)</c>）——
    /// 因为「走哪条流」已由网关行为自身决定，见 §5.4。
    /// </summary>
    public override ValueTask LeaveAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        Services.Agenda.PlanTakeOutgoingSequenceFlowsOperation(execution.AsEntity(), evaluateConditions: false);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 预取本网关<b>全部出流条件</b>里会读到的流程变量（一次往返，消除 N+1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么需要：网关条件里的变量通常落在<b>流程实例执行</b>上，而判定发生在子执行上。
    /// 若不预取，每个变量取值都要「本作用域查一次 → 未命中 → 父作用域再查一次……」，
    /// 出流越多、作用域链越深，往返次数越多。
    /// </para>
    /// <para>
    /// 这里一次性把「所有出流条件里出现的变量」查回来，之后逐个判定条件就是纯内存操作；
    /// 结果与逐条查询完全一致 —— 预取不改变作用域上溯语义，只改变往返次数。
    /// </para>
    /// <para>
    /// 无法静态提取变量名的场景（如变量值本身是动态拼出的表达式）不受影响：
    /// 求值时仍会走原有的懒加载路径。
    /// </para>
    /// </remarks>
    protected async ValueTask PrefetchConditionVariablesAsync(
        Gateway gateway,
        IDelegateExecution execution,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(execution);

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sequenceFlow in gateway.OutgoingFlows)
        {
            var conditionExpression = sequenceFlow.ConditionExpression;

            if (string.IsNullOrEmpty(conditionExpression))
            {
                continue;
            }

            // 表达式实例按原文缓存，提取结果也随之复用，这里没有重复解析成本
            foreach (var name in Services.ExpressionManager
                         .CreateExpression(conditionExpression)
                         .GetVariableNames())
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            return;
        }

        await execution.AsEntity()
            .PrefetchVariablesAsync(names, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 锁定「第一个父作用域」执行。对应 Activiti 的 <c>lockFirstParentScope</c>。
    /// <para>
    /// Activiti 通过 <c>forceUpdate()</c> 让该行在同事务内产生版本自增更新，从而取得行锁；
    /// 这里对应为一次显式的 <see cref="IExecutionManager.UpdateAsync"/>。
    /// 目的：并行的分支同时到达 Join 时，串行化「查非活动执行 → 计数」这段临界区。
    /// </para>
    /// </summary>
    protected async ValueTask LockFirstParentScopeAsync(IDelegateExecution execution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        IExecutionEntity? parentScopeExecution = null;
        var currentExecution = execution.AsEntity();

        while (parentScopeExecution is null && currentExecution.ParentId is not null)
        {
            parentScopeExecution = await Services.ExecutionManager
                .FindByIdAsync(currentExecution.ParentId, ct)
                .ConfigureAwait(false);

            if (parentScopeExecution is not null && parentScopeExecution.IsScope)
            {
                break;
            }

            if (parentScopeExecution is null)
            {
                break;
            }

            currentExecution = parentScopeExecution;
        }

        if (parentScopeExecution is null)
        {
            throw new EngineException(
                $"并行/网关执行 '{execution.Id}' 找不到可锁定的父作用域执行。");
        }

        await Services.ExecutionManager.UpdateAsync(parentScopeExecution, ct).ConfigureAwait(false);
    }
}
