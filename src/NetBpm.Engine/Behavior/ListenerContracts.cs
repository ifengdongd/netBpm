using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 执行监听器契约。对应 Activiti 的 <c>ExecutionListener</c>。
/// </summary>
/// <remarks>
/// <para>
/// 挂在任意流程元素（<c>activiti:executionListener</c>，事件 <c>start</c> / <c>take</c> / <c>end</c>）
/// 与用户任务（<c>activiti:taskListener</c> 的宿主同理）。实现通过
/// <c>activiti:class</c> / <c>activiti:delegateExpression</c> / <c>activiti:expression</c>
/// 三种方式解析（与服务任务同一套 <see cref="IActivityDelegateResolver"/>）。
/// </para>
/// <para>调用前引擎会把 <see cref="IDelegateExecution.EventName"/> 置为本次事件名，调用后还原。</para>
/// </remarks>
public interface IExecutionListener
{
    /// <summary>监听器回调。抛出的异常按引擎的监听器错误策略处理（执行监听器默认使命令失败）。</summary>
    ValueTask NotifyAsync(IDelegateExecution execution, CancellationToken ct = default);
}

/// <summary>
/// 任务监听器契约。对应 Activiti 的 <c>TaskListener</c>。
/// </summary>
/// <remarks>挂在用户任务上（<c>activiti:taskListener</c>，事件 <c>create</c> / <c>assignment</c> /
/// <c>complete</c> / <c>delete</c>）。</remarks>
public interface ITaskListener
{
    /// <summary>监听器回调。<paramref name="context"/> 携带事件名、任务实体与所属执行。</summary>
    ValueTask NotifyAsync(TaskListenerContext context, CancellationToken ct = default);
}

/// <summary>任务监听器的回调上下文。</summary>
/// <param name="EventName">事件名：create / assignment / complete / delete。</param>
/// <param name="Task">任务实体（create / assignment 阶段变量还在执行上；complete 阶段随提交的变量已写入执行）。</param>
/// <param name="Execution">任务所属执行（变量作用域）。</param>
public sealed record TaskListenerContext(string EventName, TaskEntity Task, IDelegateExecution Execution);

/// <summary>
/// 流程级事件监听器契约。对应 Activiti 的 <c>ActivitiEventListener</c>。
/// </summary>
/// <remarks>
/// 由流程级 <c>activiti:eventListener</c> 声明（<see cref="Bpmn.Model.EventListener"/>），
/// 引擎在 <c>PROCESS_STARTED</c> / <c>PROCESS_COMPLETED</c> / <c>ACTIVITY_STARTED</c> /
/// <c>ACTIVITY_COMPLETED</c> / <c>TASK_CREATED</c> / <c>TASK_COMPLETED</c> 事件发生时逐个通知；
/// <c>throwOnFailure=false</c>（缺省）时监听器抛错只记录、不阻断流程。
/// </remarks>
public interface IEngineEventListener
{
    /// <summary>事件回调。<paramref name="engineEvent"/> 携带事件类型、实体 id 与所属执行。</summary>
    ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default);
}

/// <summary>流程级事件监听器的回调事件。</summary>
/// <param name="Type">事件类型（Activiti 事件枚举名，如 <c>TASK_COMPLETED</c>）。</param>
/// <param name="EntityId">实体 id（任务 id / 活动 id / 流程实例 id，视事件而定）。</param>
/// <param name="Execution">事件所属执行（变量作用域；流程级事件为流程实例根执行）。</param>
public sealed record WorkflowEngineEvent(string Type, string? EntityId, IDelegateExecution Execution);
