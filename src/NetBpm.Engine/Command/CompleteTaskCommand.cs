using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 完成任务。对应 Activiti 的 <c>CompleteTaskCmd</c> + <c>AbstractCompleteTaskCmd</c>。
/// <para><b>流程（逐条照搬）</b>：</para>
/// <list type="number">
///   <item><description>校验任务存在 / 未挂起 / 未被委派（<c>DelegationState.Pending</c> 抛错）；</description></item>
///   <item><description>把变量写到<b>执行</b>上（<c>setExecutionVariables</c>，经作用域链落到流程实例）；</description></item>
///   <item><description>删除任务（含身份关系）；</description></item>
///   <item><description><c>planTriggerExecutionOperation(execution)</c> —— 由议程把用户任务节点推过去。</description></item>
/// </list>
/// <para>
/// ⚠️ 未实现项：任务完成监听器（<c>complete</c> 事件）、<c>TASK_COMPLETED</c> 事件分发、
/// 「当前登录用户补 <c>participant</c> 关系」（需要认证上下文）。前两项由部署期校验拦截
/// （模型里出现监听器即拒绝部署），因此不会静默丢语义。
/// </para>
/// </summary>
public sealed class CompleteTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly IDictionary<string, object?>? _variables;

    public CompleteTaskCommand(string taskId, IDictionary<string, object?>? variables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _variables = variables;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var taskManager = services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "P7",
                "宿主必须注册任务管理器实现");

        var task = await taskManager.FindTaskByIdAsync(_taskId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"任务 '{_taskId}' 不存在（可能已完成或已被删除）。");

        if (task.SuspensionState != TaskEntity.ActiveSuspensionState)
        {
            throw new EngineException("Cannot complete a suspended task");
        }

        if (task.DelegationState == DelegationState.Pending)
        {
            throw new EngineException("A delegated task cannot be completed, but should be resolved instead.");
        }

        IExecutionEntity? execution = null;

        if (task.ExecutionId is not null)
        {
            execution = await services.ExecutionManager.FindByIdAsync(task.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new EngineException(
                    $"任务 '{task.Id}' 关联的执行 '{task.ExecutionId}' 不存在，无法继续流程。");
        }

        if (_variables is { Count: > 0 })
        {
            if (execution is null)
            {
                throw new EngineException(
                    $"任务 '{task.Id}' 不是流程任务（无执行），无法写入流程变量。");
            }

            await execution.SetVariablesAsync(_variables, ct).ConfigureAwait(false);
        }

        await taskManager.DeleteTaskAsync(task, null, ct).ConfigureAwait(false);

        // 继续流程（独立任务没有执行，不需要推进）
        if (execution is not null)
        {
            services.Agenda.PlanTriggerExecutionOperation(execution);
        }

        return Unit.Value;
    }
}
