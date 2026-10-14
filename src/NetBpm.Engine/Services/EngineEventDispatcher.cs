using Microsoft.Extensions.Logging;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Services;

/// <summary>
/// 引擎当前分发的流程级事件类型（对齐 Activiti 的 <c>ActivitiEventType</c> 子集）。
/// </summary>
public static class EngineEvents
{
    public const string ProcessStarted = "PROCESS_STARTED";
    public const string ProcessCompleted = "PROCESS_COMPLETED";
    public const string ActivityStarted = "ACTIVITY_STARTED";
    public const string ActivityCompleted = "ACTIVITY_COMPLETED";
    public const string TaskCreated = "TASK_CREATED";
    public const string TaskCompleted = "TASK_COMPLETED";
}

/// <summary>
/// 流程级事件分发：把引擎内部事件通知给流程定义里声明的
/// <c>activiti:eventListener</c>（<see cref="Bpmn.Model.EventListener"/>）。
/// <para>
/// 事件监听器的实现解析与服务任务同一套委托解析器；
/// <c>throwOnFailure != true</c> 时监听器抛错只记警告、不阻断流程
/// （对应 Activiti 的 <c>isFailOnException()</c> = false 的语义）。
/// </para>
/// </summary>
public static class EngineEventDispatcher
{
    /// <summary>
    /// 向 <paramref name="execution"/> 所属流程定义的事件监听器分发一个事件。
    /// 流程没有声明监听器（或没有解析器）时是零开销空操作。
    /// </summary>
    /// <param name="services">引擎服务。</param>
    /// <param name="execution">事件所属执行（变量作用域）。</param>
    /// <param name="eventType">事件类型（见 <see cref="EngineEvents"/>）。</param>
    /// <param name="entityId">实体 id：任务 id / 活动 id / 流程实例 id，视事件而定。</param>
    public static async ValueTask DispatchAsync(
        IEngineServices services,
        IDelegateExecution execution,
        string eventType,
        string? entityId,
        CancellationToken ct = default)
    {
        var resolver = services.ProcessDefinitionResolver;

        if (resolver is null || string.IsNullOrEmpty(execution.ProcessDefinitionId))
        {
            return;
        }

        var process = await resolver
            .ResolveProcessModelAsync(execution.ProcessDefinitionId, ct)
            .ConfigureAwait(false);

        if (process?.EventListeners is not { Count: > 0 })
        {
            return;
        }

        foreach (var listener in process.EventListeners)
        {
            if (!Subscribes(listener.Events, eventType))
            {
                continue;
            }

            try
            {
                var target = await ListenerInvoker.ResolveAsync<IEngineEventListener>(
                    services, execution, process.Id ?? "?", "事件监听器", eventType,
                    listener.ClassName, listener.DelegateExpression, listener.Expression, ct).ConfigureAwait(false);

                await target.OnEventAsync(new WorkflowEngineEvent(eventType, entityId, execution), ct).ConfigureAwait(false);            }
            catch (Exception exception) when (listener.ThrowOnFailure != true)
            {
                services.Logger?.LogWarning(
                    exception,
                    "流程级事件监听器执行失败（事件 {EventType}，流程 {ProcessId}）—— throwOnFailure=false，继续流程。",
                    eventType,
                    process.Id);
            }
        }
    }

    /// <summary>事件订阅匹配：<see cref="Bpmn.Model.EventListener.Events"/> 为逗号分隔清单。</summary>
    private static bool Subscribes(string? events, string eventType)
    {
        if (string.IsNullOrWhiteSpace(events))
        {
            return false;
        }

        foreach (var name in events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(name, eventType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
