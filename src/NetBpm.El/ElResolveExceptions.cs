namespace NetBpm.El;

/// <summary>
/// 无法解析标识符 / 属性。对照 JUEL 的 <c>PropertyNotFoundException</c>。
/// <para>
/// 🔑 它与"静默 null"的边界必须记牢（照搬 JUEL）：
/// <list type="bullet">
///   <item><description><b>抛</b>：顶层标识符解析不出（<c>${unknown}</c>）、<c>a.b</c> 的 <c>b</c> 解析不出；</description></item>
///   <item><description><b>静默 null</b>：基为 null（<c>${a.b}</c> 而 <c>a</c> 为 null）、Map 缺键、List/数组越界、<c>m[null]</c>。</description></item>
/// </list>
/// 前者是"写错了"，后者是"数据本来就没有" —— 混为一谈会让表达式错误静默变成"条件为假"。
/// </para>
/// </summary>
public sealed class PropertyNotFoundException : ElException
{
    public PropertyNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>无法解析方法。对照 JUEL 的 <c>MethodNotFoundException</c>。</summary>
public sealed class MethodNotFoundException : ElException
{
    public MethodNotFoundException(string message)
        : base(message)
    {
    }
}
