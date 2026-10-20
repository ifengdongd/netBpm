using NetBpm.Bpmn.Model;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 完成任务。对应 Activiti 的 <c>CompleteTaskCmd</c> + <c>AbstractCompleteTaskCmd</c>。
/// <para><b>流程（逐条照搬）</b>：</para>
/// <list type="number">
///   <item><description>校验任务存在 / 未挂起 / 未被委派（<c>DelegationState.Pending</c> 抛错）；</description></item>
///   <item><description>把变量写到<b>执行</b>上（<c>setExecutionVariables</c>，经作用域链落到流程实例）；</description></item>
///   <item><description>触发任务监听器（<c>complete</c>）与流程级 <c>TASK_COMPLETED</c> 事件分发；</description></item>
///   <item><description>删除任务（含身份关系）；</description></item>
///   <item><description><c>planTriggerExecutionOperation(execution)</c> —— 由议程把用户任务节点推过去。</description></item>
/// </list>
/// <para>
/// ⚠️ 未实现项：「当前登录用户补 <c>participant</c> 关系」（需要认证上下文）。
/// </para>
/// </summary>
public sealed class CompleteTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly IDictionary<string, object?>? _variables;
    private readonly int? _expectedRevision;

    public CompleteTaskCommand(
        string taskId,
        IDictionary<string, object?>? variables = null,
        int? expectedRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _variables = variables;
        _expectedRevision = expectedRevision;
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
            ?? throw new EngineObjectNotFoundException($"任务 '{_taskId}' 不存在（可能已完成或已被删除）。");

        if (_expectedRevision is { } expectedRevision && task.Revision != expectedRevision)
        {
            throw new OptimisticLockingException(
                $"任务 '{_taskId}' 已被修改（期望版本 {expectedRevision}，实际 {task.Revision}），请刷新后重试。");
        }

        if (task.SuspensionState != TaskEntity.ActiveSuspensionState)
        {
            throw new EngineConflictException("Cannot complete a suspended task");
        }

        if (task.DelegationState == DelegationState.Pending)
        {
            throw new EngineConflictException("A delegated task cannot be completed, but should be resolved instead.");
        }

        IExecutionEntity? execution = null;

        if (task.ExecutionId is not null)
        {
            execution = await services.ExecutionManager.FindByIdAsync(task.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new EngineObjectNotFoundException(
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

        // 任务监听器（activiti:taskListener，event="complete"）：变量已写入执行，监听器读得到提交值；
        // 任务此时还未删除，监听器拿得到完整任务实体。
        if (execution is not null)
        {
            var userTask = await ResolveUserTaskAsync(services, task, execution, ct).ConfigureAwait(false);

            if (userTask is not null)
            {
                await ListenerInvoker
                    .NotifyTaskListenersAsync(
                        services, task, execution, userTask.TaskListeners, ListenerInvoker.CompleteEvent, ct)
                    .ConfigureAwait(false);
            }
        }

        // 流程级 TASK_COMPLETED 事件分发（activiti:eventListener，
        // 对应 Activiti 里 WorkFlowTaskCompletedListener 这类全局监听的用法）
        if (execution is not null)
        {
            await EngineEventDispatcher
                .DispatchAsync(services, execution, EngineEvents.TaskCompleted, task.Id, ct)
                .ConfigureAwait(false);
        }

        await taskManager.DeleteTaskAsync(task, null, ct).ConfigureAwait(false);

        // 任务已结束：挂在它上面的定时边界作业一并作废（否则到点会对着空任务空转）
        if (execution is not null && services.TimerJobManager is { } timerJobs)
        {
            await timerJobs.DeleteByExecutionAsync(execution.Id, ct).ConfigureAwait(false);
        }

        // 继续流程（独立任务没有执行，不需要推进）
        if (execution is not null)
        {
            services.Agenda.PlanTriggerExecutionOperation(execution);
        }

        return Unit.Value;
    }

    /// <summary>
    /// 取任务所属的用户任务元素（拿它的任务监听器配置）。
    /// 跨命令读回的执行只有 <c>ACT_ID_</c> 字符串，须靠流程定义解析。
    /// </summary>
    private static async ValueTask<UserTask?> ResolveUserTaskAsync(
        IEngineServices services,
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct)
    {
        if (services.ProcessDefinitionResolver is not { } resolver)
        {
            return null;
        }

        var element = await resolver
            .ResolveFlowElementAsync(execution.ProcessDefinitionId, task.TaskDefinitionKey, ct)
            .ConfigureAwait(false);

        return element as UserTask;
    }
}
