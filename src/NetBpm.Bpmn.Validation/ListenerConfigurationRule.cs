using NetBpm.Bpmn.Model;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 规则 10：监听器定义的形状校验 —— 事件必填（且取值合法）、实现方式必填。
/// <para>
/// 覆盖执行监听器（事件 start / take / end）、任务监听器（create / assignment / complete / delete）
/// 与流程级事件监听器（events 逗号分隔，如 <c>TASK_COMPLETED</c>）。
/// </para>
/// <para>
/// 实现能否解析成已注册的委托由部署期的 <c>ServiceTaskDelegateGuard</c> 预检 ——
/// 那需要容器，不属于模型校验的职责。
/// </para>
/// </summary>
public sealed class ListenerConfigurationRule : IProcessValidationRule
{
    private const string RuleName = "listener-configuration";

    public string Name => RuleName;

    private static readonly HashSet<string> ExecutionEvents =
        new(StringComparer.OrdinalIgnoreCase) { "start", "take", "end" };

    private static readonly HashSet<string> TaskEvents = new(StringComparer.OrdinalIgnoreCase)
        { "create", "assignment", "complete", "delete" };

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // 流程自身 + 全部流程元素都可能是执行监听器宿主
        foreach (var element in process.FlowElements.OfType<BaseElement>().Append<BaseElement>(process))
        {
            foreach (var listener in element.ExecutionListeners)
            {
                foreach (var error in ValidateExecutionListener(element.Id, listener))
                {
                    yield return error;
                }
            }
        }

        foreach (var userTask in process.FindFlowElementsOfType<UserTask>())
        {
            foreach (var listener in userTask.TaskListeners)
            {
                foreach (var error in ValidateTaskListener(userTask.Id, listener))
                {
                    yield return error;
                }
            }
        }

        foreach (var listener in process.EventListeners)
        {
            if (string.IsNullOrWhiteSpace(listener.Events))
            {
                yield return new ValidationError(
                    RuleName,
                    process.Id,
                    nameof(EventListener),
                    "事件监听器（activiti:eventListener）必须声明 events（如 TASK_COMPLETED）。");
            }

            if (HasNoImplementation(listener.ClassName, listener.DelegateExpression, listener.Expression))
            {
                yield return new ValidationError(
                    RuleName,
                    process.Id,
                    nameof(EventListener),
                    "事件监听器（activiti:eventListener）必须指定实现方式之一：class / delegateExpression / expression。");
            }
        }
    }

    private static IEnumerable<ValidationError> ValidateExecutionListener(string? elementId, ExecutionListener listener)
    {
        if (string.IsNullOrWhiteSpace(listener.Event))
        {
            yield return new ValidationError(
                RuleName,
                elementId,
                nameof(ExecutionListener),
                "执行监听器（activiti:executionListener）必须声明 event（start / take / end）。");
        }
        else if (!ExecutionEvents.Contains(listener.Event))
        {
            yield return new ValidationError(
                RuleName,
                elementId,
                nameof(ExecutionListener),
                $"执行监听器的 event 只支持 start / take / end，实际为 '{listener.Event}'。");
        }

        foreach (var error in ValidateImplementation(elementId, nameof(ExecutionListener), listener.ClassName, listener.DelegateExpression, listener.Expression))
        {
            yield return error;
        }
    }

    private static IEnumerable<ValidationError> ValidateTaskListener(string? elementId, TaskListener listener)
    {
        if (string.IsNullOrWhiteSpace(listener.Event))
        {
            yield return new ValidationError(
                RuleName,
                elementId,
                nameof(TaskListener),
                "任务监听器（activiti:taskListener）必须声明 event（create / assignment / complete / delete）。");
        }
        else if (!TaskEvents.Contains(listener.Event))
        {
            yield return new ValidationError(
                RuleName,
                elementId,
                nameof(TaskListener),
                $"任务监听器的 event 只支持 create / assignment / complete / delete，实际为 '{listener.Event}'。");
        }

        foreach (var error in ValidateImplementation(elementId, nameof(TaskListener), listener.ClassName, listener.DelegateExpression, listener.Expression))
        {
            yield return error;
        }
    }

    private static IEnumerable<ValidationError> ValidateImplementation(
        string? elementId,
        string listenerKind,
        string? className,
        string? delegateExpression,
        string? expression)
    {
        if (HasNoImplementation(className, delegateExpression, expression))
        {
            yield return new ValidationError(
                RuleName,
                elementId,
                listenerKind,
                $"{listenerKind}必须指定实现方式之一：class / delegateExpression / expression。");
        }
    }

    private static bool HasNoImplementation(string? className, string? delegateExpression, string? expression) =>
        string.IsNullOrWhiteSpace(className)
        && string.IsNullOrWhiteSpace(delegateExpression)
        && string.IsNullOrWhiteSpace(expression);
}
