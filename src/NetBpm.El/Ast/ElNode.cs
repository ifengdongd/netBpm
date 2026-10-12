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
}

/// <summary>字面量。对应 JUEL 的 <c>Ast*Literal</c>。</summary>
internal sealed class ElLiteralNode : ElNode
{
    private readonly object? _value;

    public ElLiteralNode(object? value) => _value = value;

    public override ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct) =>
        ValueTask.FromResult(_value);
}
