using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 监听器的解析与调用。对应 Activiti 的 <c>ExecutionListener</c> / <c>TaskListener</c> 调用逻辑。
/// <para>
/// 实现三种来源（与服务任务同一套 <see cref="IActivityDelegateResolver"/>）：
/// <c>class</c>（.NET 类型名）→ <c>delegateExpression</c>（容器中的服务名，支持 <c>${name}</c>）→
/// <c>expression</c>（EL 求值，结果须实现监听器契约）。
/// </para>
/// </summary>
public static class ListenerInvoker
{
    public const string StartEvent = "start";
    public const string TakeEvent = "take";
    public const string EndEvent = "end";
    public const string CreateEvent = "create";
    public const string AssignmentEvent = "assignment";
    public const string CompleteEvent = "complete";
    public const string DeleteEvent = "delete";

    /// <summary>
    /// 触发元素上匹配 <paramref name="eventName"/> 的执行监听器（按配置顺序逐个调用）。
    /// 对应 Activiti 的 <c>executeExecutionListeners</c>。
    /// <para>回调期间 <see cref="IDelegateExecution.EventName"/> 为本次事件名，返回后还原。</para>
    /// </summary>
    public static async ValueTask NotifyExecutionListenersAsync(
        IEngineServices services,
        IDelegateExecution execution,
        FlowElement element,
        string eventName,
        CancellationToken ct = default)
    {
        foreach (var listener in element.ExecutionListeners)
        {
            if (!Matches(listener.Event, eventName))
            {
                continue;
            }

            var target = await ResolveAsync<IExecutionListener>(
                services, execution, element.Id ?? "?", "执行监听器", eventName,
                listener.ClassName, listener.DelegateExpression, listener.Expression, ct).ConfigureAwait(false);

            var previous = execution.EventName;
            execution.EventName = eventName;

            try
            {
                await target.NotifyAsync(execution, ct).ConfigureAwait(false);
            }
            finally
            {
                execution.EventName = previous;
            }
        }
    }

    /// <summary>触发用户任务上匹配事件的任务监听器（按配置顺序逐个调用）。</summary>
    public static async ValueTask NotifyTaskListenersAsync(
        IEngineServices services,
        TaskEntity task,
        IDelegateExecution execution,
        IList<TaskListener> listeners,
        string eventName,
        CancellationToken ct = default)
    {
        foreach (var listener in listeners)
        {
            if (!Matches(listener.Event, eventName))
            {
                continue;
            }

            var target = await ResolveAsync<ITaskListener>(
                services, execution, task.TaskDefinitionKey ?? task.Id, "任务监听器", eventName,
                listener.ClassName, listener.DelegateExpression, listener.Expression, ct).ConfigureAwait(false);

            await target.NotifyAsync(new TaskListenerContext(eventName, task, execution), ct).ConfigureAwait(false);
        }
    }

    private static bool Matches(string? configured, string eventName) =>
        string.Equals(configured, eventName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按监听器配置解析出实现（class → delegateExpression → expression，配置了哪个用哪个）。
    /// 解析不到或类型不符都响亮失败 —— 监听器语义丢失属于最危险的静默偏差。
    /// <para><c>internal</c> 供 <see cref="Services.EngineEventDispatcher"/> 复用（事件监听器同机制）。</para>
    /// </summary>
    internal static async ValueTask<TListener> ResolveAsync<TListener>(
        IEngineServices services,
        IDelegateExecution execution,
        string elementId,
        string listenerKind,
        string eventName,
        string? className,
        string? delegateExpression,
        string? expression,
        CancellationToken ct)
    {
        var hasClass = !string.IsNullOrWhiteSpace(className);
        var hasDelegate = !string.IsNullOrWhiteSpace(delegateExpression);
        var hasExpression = !string.IsNullOrWhiteSpace(expression);

        if (!hasClass && !hasDelegate && !hasExpression)
        {
            throw new EngineException(
                $"元素 '{elementId}' 的{listenerKind}（事件 {eventName}）没有指定实现方式。");
        }

        var resolver = services.ActivityDelegateResolver
            ?? throw new EngineException(
                $"元素 '{elementId}' 的{listenerKind}（事件 {eventName}）使用了实现引用，但未注册 IActivityDelegateResolver。");

        object? target = (hasClass, hasDelegate, hasExpression) switch
        {
            (true, _, _) => resolver.ResolveByClassName(className!.Trim()),
            (_, true, _) => resolver.ResolveByExpressionName(delegateExpression!.Trim()),
            _ => await services.ExpressionManager
                .CreateExpression(expression!.Trim())
                .GetValueAsync(execution, ct)
                .ConfigureAwait(false),
        };

        if (target is null)
        {
            throw new EngineException(
                $"元素 '{elementId}' 的{listenerKind}（事件 {eventName}）无法解析到已注册的实现。");
        }

        return target is TListener typed
            ? typed
            : throw new EngineException(
                $"元素 '{elementId}' 的{listenerKind}（事件 {eventName}）解析出的对象不是 {typeof(TListener).Name}" +
                $"（实际类型：{target.GetType().FullName}）。");
    }
}
