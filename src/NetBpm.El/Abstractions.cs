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

    /// <summary>
    /// 删除变量。对应 Activiti <c>VariableScopeImpl.removeVariable</c>：
    /// 本作用域有则删本地，否则沿作用域链上溯；到顶仍无则无操作。
    /// </summary>
    /// <returns>是否删除了某个作用域上的变量。</returns>
    ValueTask<bool> RemoveVariableAsync(string name, CancellationToken ct = default);
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

    /// <summary>
    /// 求值时会读取的<b>顶层变量名</b>集合（如 <c>${amount > 1000}</c> → <c>["amount"]</c>）。
    /// <para>
    /// 用于「批量预取」：调用方可以先拿到所有需要的变量名，一次性从库里取回，
    /// 避免每个变量一次往返（网关多出流场景下的典型 N+1）。
    /// </summary></para>
    /// <para>
    /// 返回的集合<b>只读</b>且可安全缓存（表达式实例本身由 <see cref="IExpressionManager"/> 缓存）。
    /// </para>
    /// </summary>
    IReadOnlyCollection<string> GetVariableNames();
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
/// 已实现的语法之外、遇到<b>明确不支持</b>的写法时抛出，避免静默求值错误。
/// </summary>
/// <remarks>
/// ⚠️ 2026-11-02 复核：P3 的预留语法已全部实现，当前 <c>src/</c> 里**没有 throw 点**
/// （唯一的引用是 <c>TaskBehaviors</c> 里的一处 catch：求值失败时回退原文而不是让提交失败）。
/// 保留它是给将来看：新增语法时若选择「先解析、暂不执行」，就要用它显式失败，
/// 而不是返回一个看起来正常的结果。别把它当成「EL 还有缺口」的信号 —— 现在没有。
/// </remarks>
public sealed class ElNotSupportedException : ElException
{
    public ElNotSupportedException(string message)
        : base(message)
    {
    }
}
