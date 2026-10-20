using NetBpm.Bpmn.Model;
using NetBpm.El;

namespace NetBpm.Bpmn.Validation;

/// <summary>
/// 规则：所有会被引擎当作表达式求值的属性，必须在<b>部署期</b>就能解析通过。
/// <para>
/// 背景：条件表达式、办理人、多实例实例数等属性在运行期才走 EL。语法写错（少括号、用了
/// 未实现的函数、<c>${a ==}</c> 之类）过去要等流程真正跑到该节点才炸 —— 部署时没有任何提示。
/// 这里直接把解析器（与运行期同一个 <see cref="ExpressionManager"/>）接进校验链，把
/// 「跑起来才发现」提前成「部署就拒绝」。
/// </para>
/// <para>
/// 只做<b>语法</b>检查，不做求值：变量是否存在、类型是否匹配由运行期决定，部署期不可能知道。
/// </para>
/// </summary>
public sealed class ExpressionParseRule : IProcessValidationRule
{
    /// <inheritdoc />
    public string Name => "expression-parseable";

    /// <summary>与运行期同一个解析器（内部按原文缓存 AST，部署期解析结果可被运行期复用）。</summary>
    private static readonly ExpressionManager Expressions = new();

    /// <inheritdoc />
    public IEnumerable<ValidationError> Validate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var errors = new List<ValidationError>();

        foreach (var flow in process.FindFlowElementsOfType<SequenceFlow>())
        {
            Check(errors, flow.Id, nameof(SequenceFlow), flow.ConditionExpression, "条件表达式");
        }

        foreach (var task in process.FindFlowElementsOfType<UserTask>())
        {
            Check(errors, task.Id, nameof(UserTask), task.Assignee, "办理人表达式");
            Check(errors, task.Id, nameof(UserTask), task.Owner, "所有者表达式");
            Check(errors, task.Id, nameof(UserTask), task.DueDate, "到期时间表达式");
            Check(errors, task.Id, nameof(UserTask), task.Priority, "优先级表达式");
            Check(errors, task.Id, nameof(UserTask), task.SkipExpression, "跳过表达式");

            foreach (var candidate in task.CandidateUsers)
            {
                Check(errors, task.Id, nameof(UserTask), candidate, "候选用户表达式");
            }

            foreach (var candidate in task.CandidateGroups)
            {
                Check(errors, task.Id, nameof(UserTask), candidate, "候选组表达式");
            }

            CheckLoopCharacteristics(errors, task);
        }

        foreach (var task in process.FindFlowElementsOfType<ServiceTask>())
        {
            Check(errors, task.Id, nameof(ServiceTask), task.Expression, "服务任务表达式");
            Check(errors, task.Id, nameof(ServiceTask), task.DelegateExpression, "委托表达式");
        }

        // 边界事件的定时值可以写成表达式（在创建定时器时求值）
        foreach (var boundaryEvent in process.FindFlowElementsOfType<BoundaryEvent>())
        {
            if (boundaryEvent.Timer is not { } timer)
            {
                continue;
            }

            Check(errors, boundaryEvent.Id, nameof(BoundaryEvent), timer.TimeDate, "定时器 timeDate 表达式");
            Check(errors, boundaryEvent.Id, nameof(BoundaryEvent), timer.TimeDuration, "定时器 timeDuration 表达式");
            Check(errors, boundaryEvent.Id, nameof(BoundaryEvent), timer.TimeCycle, "定时器 timeCycle 表达式");
        }

        // 监听器可挂在任意元素上（含流程自身）
        foreach (var element in process.FlowElements.OfType<BaseElement>().Append<BaseElement>(process))
        {
            foreach (var listener in element.ExecutionListeners)
            {
                CheckListener(errors, element.Id, listener);
            }

            if (element is not UserTask userTask)
            {
                continue;
            }

            foreach (var listener in userTask.TaskListeners)
            {
                CheckListener(errors, element.Id, listener);
            }
        }

        foreach (var listener in process.EventListeners)
        {
            Check(errors, process.Id, nameof(EventListener), listener.Expression, "事件监听器表达式");
            Check(errors, process.Id, nameof(EventListener), listener.DelegateExpression, "事件监听器委托表达式");
        }

        return errors;
    }

    private static void CheckLoopCharacteristics(List<ValidationError> errors, UserTask task)
    {
        var mi = task.LoopCharacteristics;

        if (mi is null)
        {
            return;
        }

        Check(errors, task.Id, nameof(UserTask), mi.LoopCardinality, "多实例实例数（loopCardinality）表达式");
        Check(errors, task.Id, nameof(UserTask), mi.Collection, "多实例集合（collection）表达式");
        Check(errors, task.Id, nameof(UserTask), mi.CompletionCondition, "多实例完成条件（completionCondition）表达式");
    }

    private static void CheckListener(
        List<ValidationError> errors,
        string? elementId,
        ExecutionListener listener)
    {
        Check(errors, elementId, nameof(ExecutionListener), listener.Expression, "监听器表达式");
        Check(errors, elementId, nameof(ExecutionListener), listener.DelegateExpression, "监听器委托表达式");
    }

    private static void Check(
        List<ValidationError> errors,
        string? elementId,
        string elementType,
        string? expression,
        string description)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        try
        {
            // 纯文本也合法：解析结果是「字面量」，与运行期语义一致（不在这里判空/判变量）
            Expressions.CreateExpression(expression);
        }
        catch (ElException exception)
        {
            errors.Add(new ValidationError(
                "expression-parseable",
                elementId,
                elementType,
                $"{description} '{expression}' 无法解析：{exception.Message}"));
        }
    }
}
