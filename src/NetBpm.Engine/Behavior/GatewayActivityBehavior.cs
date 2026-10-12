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
