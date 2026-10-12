namespace NetBpm.El;

/// <summary>
/// 变量作用域。由流程执行（<c>IDelegateExecution</c>）实现，是表达式求值的取值来源。
/// </summary>
public interface IVariableScope
{
    /// <summary>
    /// 发起人（认证用户）。对应 Activiti 隐式对象 <c>initiator</c>
    /// （<c>ProcessInitiatorELResolver</c> 返回流程实例的 startUserId，未认证时为 null）。
    /// <para>仅在流程执行上非空；纯字典作用域（测试用）恒为 null。</para>
    /// </summary>
    string? StartUserId => null;

    ValueTask<object?> GetVariableAsync(string name, CancellationToken ct = default);

    ValueTask<bool> HasVariableAsync(string name, CancellationToken ct = default);

    ValueTask SetVariableAsync(string name, object? value, CancellationToken ct = default);

    ValueTask<IDictionary<string, object?>> GetVariablesAsync(CancellationToken ct = default);

    ValueTask SetVariablesAsync(IDictionary<string, object?> variables, CancellationToken ct = default);
}

/// <summary>
/// 表达式实例。对应 Activiti / JUEL 的 <c>Expression</c>。
/// </summary>
public interface IExpression
{
    /// <summary>表达式原文（含 <c>${}</c> / <c>#{}</c> 包装）。</summary>
    string ExpressionText { get; }

    /// <summary>求值。</summary>
    ValueTask<object?> GetValueAsync(IVariableScope scope, CancellationToken ct = default);

    /// <summary>赋值（用于 <c>SetValue</c> 语义的表达式）。</summary>
    ValueTask SetValueAsync(object? value, IVariableScope scope, CancellationToken ct = default);
}

/// <summary>
/// 表达式管理器。对应 Activiti 的 <c>ExpressionManager</c>。
/// </summary>
public interface IExpressionManager
{
    /// <summary>创建（并缓存）表达式。</summary>
    IExpression CreateExpression(string expression);

    /// <summary>是否为受支持的表达式写法（含 <c>${}</c> / <c>#{}</c>）。</summary>
    bool IsExpression(string? expression);
}

/// <summary>
/// 表达式语言相关异常。对照 Activiti 的 <c>ActivitiException</c> 在 EL 场景下的语义。
/// </summary>
public class ElException : Exception
{
    public ElException(string message)
        : base(message)
    {
    }

    public ElException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 预留能力的表达式异常：P3 尚未实现该语法时抛出，避免静默求值错误。
/// </summary>
public sealed class ElNotSupportedException : ElException
{
    public ElNotSupportedException(string message)
        : base(message)
    {
    }
}
