namespace NetBpm.El;

/// <summary>
/// 求值上下文。对应 JUEL 的 <c>ELContext</c>（本子集只保留解析所需的字段）。
/// <para>
/// <see cref="Scope"/> 同时充当 Activiti 的隐式对象 <c>execution</c> —— 因为引擎传给表达式求值的
/// 就是执行对象本身（Activiti 的 <c>VariableScopeElResolver.ExecutionElResolver</c> 同样返回 <c>variableScope</c>）。
/// </para>
/// </summary>
public sealed class ElContext
{
    internal ElContext(IVariableScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
    }

    /// <summary>变量作用域（同时是隐式对象 <c>execution</c>）。</summary>
    public IVariableScope Scope { get; }
}
