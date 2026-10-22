using System.Text;

namespace NetBpm.El.Ast;

/// <summary>AST 节点基类。对应 JUEL 的 <c>AstNode</c>（求值入口 <c>getValue</c>）。</summary>
internal abstract class ElNode
{
    /// <summary>求值。</summary>
    public abstract ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct);

    /// <summary>是否"纯文本"（JUEL 的 <c>AstText.isLiteralText()</c>）。</summary>
    public virtual bool IsLiteralText => false;

    /// <summary>赋值。仅左值节点（标识符 / 属性 / 索引）支持。</summary>
    public virtual ValueTask SetValueAsync(object? value, ElContext context, CancellationToken ct) =>
        throw new ElException($"表达式不可赋值（不是左值）。");

    /// <summary>
    /// 收集该子树求值时会读取的<b>顶层变量名</b>，写进 <paramref name="names"/>。
    /// <para>
    /// 用途：网关在逐个判定出流条件之前，先把所有条件里出现的变量名一次查回来，
    /// 把「每个变量一次往返」压成「一条 SQL」。
    /// </para>
    /// <para>
    /// 只取顶层标识符（<c>${a.b(c)}</c> 只记 <c>a</c>）—— 与求值口径一致：
    /// 属性 / 方法调用都是在顶层变量拿到对象之后再往下走，不需要额外查库。
    /// </para>
    /// </summary>
    public abstract void CollectVariableNames(ISet<string> names);
}

/// <summary>纯文本段。对应 JUEL 的 <c>AstText</c>。</summary>
internal sealed class ElTextNode : ElNode
{
    private readonly string _text;

    public ElTextNode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
    }

    public override bool IsLiteralText => true;

    public override ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct) =>
        ValueTask.FromResult<object?>(_text);

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        // 纯文本不读变量
    }
}

/// <summary>
/// 复合表达式（一段文本里嵌 <c>${}</c>）：逐段求值后按 <c>toString</c> 拼接。
/// 对应 JUEL 的 <c>AstComposite</c>。
/// </summary>
internal sealed class ElCompositeNode : ElNode
{
    private readonly IReadOnlyList<ElNode> _nodes;

    public ElCompositeNode(IReadOnlyList<ElNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _nodes = nodes;
    }

    public override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        var builder = new StringBuilder();

        foreach (var node in _nodes)
        {
            var value = await node.EvalAsync(context, ct).ConfigureAwait(false);
            builder.Append(TypeConverter.CoerceToString(value));
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        foreach (var node in _nodes)
        {
            node.CollectVariableNames(names);
        }
    }
}

/// <summary>括号表达式 <c>(expr)</c>。对应 JUEL 的 <c>AstNested</c>。</summary>
internal sealed class ElNestedNode : ElNode
{
    private readonly ElNode _inner;

    public ElNestedNode(ElNode inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct) =>
        _inner.EvalAsync(context, ct);

    public override ValueTask SetValueAsync(object? value, ElContext context, CancellationToken ct) =>
        _inner.SetValueAsync(value, context, ct);

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names) =>
        _inner.CollectVariableNames(names);
}

/// <summary>字面量。对应 JUEL 的 <c>Ast*Literal</c>。</summary>
internal sealed class ElLiteralNode : ElNode
{
    private readonly object? _value;

    public ElLiteralNode(object? value) => _value = value;

    public override ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct) =>
        ValueTask.FromResult(_value);

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        // 字面量不读变量
    }
}
