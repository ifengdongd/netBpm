namespace NetBpm.El.Ast;

/// <summary>
/// 顶层标识符：先查隐式对象与流程变量，查不到<b>响亮失败</b>。
/// 对应 JUEL 的 <c>AstIdentifier</c>（未 resolve 时抛 <c>PropertyNotFoundException</c>）。
/// </summary>
internal sealed class ElIdentifierNode : ElNode
{
    private readonly string _name;

    public ElIdentifierNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
    }

    public override ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct) =>
        ElResolver.ResolveIdentifierAsync(context, _name, ct);

    public override async ValueTask SetValueAsync(object? value, ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 对应 VariableScopeElResolver.setValue：只在变量已存在时写入
        if (!await context.Scope.HasVariableAsync(_name, ct).ConfigureAwait(false))
        {
            throw new PropertyNotFoundException($"标识符 '{_name}' 不是已存在的变量，无法赋值。");
        }

        await context.Scope.SetVariableAsync(_name, value, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// 属性 / 索引访问的共同父类。对应 JUEL 的 <c>AstProperty</c>。
/// <para>
/// ⚠️ 与 JUEL 一致的两条静默规则：<b>基为 null ⇒ 返回 null</b>；<b>索引为 null 且 strict ⇒ 返回 null</b>。
/// </para>
/// </summary>
internal abstract class ElPropertyNode : ElNode
{
    protected ElPropertyNode(ElNode prefix, string expressionText, bool strict)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        Prefix = prefix;
        ExpressionText = expressionText;
        Strict = strict;
    }

    protected ElNode Prefix { get; }

    protected bool Strict { get; }

    /// <summary>用于报错的原文。</summary>
    protected string ExpressionText { get; }

    public sealed override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseValue = await Prefix.EvalAsync(context, ct).ConfigureAwait(false);

        // 基为 null ⇒ 静默 null（JUEL AstProperty.eval）
        if (baseValue is null)
        {
            return null;
        }

        var (property, isNull) = await GetPropertyAsync(context, ct).ConfigureAwait(false);

        if (isNull && Strict)
        {
            return null;
        }

        return await ResolveAsync(context, baseValue, property, ct).ConfigureAwait(false);
    }

    protected abstract ValueTask<(object? Property, bool IsNull)> GetPropertyAsync(
        ElContext context,
        CancellationToken ct);

    protected abstract ValueTask<object?> ResolveAsync(
        ElContext context,
        object baseValue,
        object? property,
        CancellationToken ct);
}

/// <summary>点号属性访问 <c>${a.b}</c>。对照 JUEL 的 <c>AstDot</c>（恒 strict）。</summary>
internal sealed class ElDotNode : ElPropertyNode
{
    private readonly string _name;

    public ElDotNode(ElNode prefix, string name, string expressionText)
        : base(prefix, expressionText, strict: true)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
    }

    protected override ValueTask<(object? Property, bool IsNull)> GetPropertyAsync(
        ElContext context,
        CancellationToken ct) => ValueTask.FromResult<(object?, bool)>((_name, false));

    protected override ValueTask<object?> ResolveAsync(
        ElContext context,
        object baseValue,
        object? property,
        CancellationToken ct) =>
        ElResolver.ResolvePropertyAsync(context, baseValue, (string)property!, ct);

    public override async ValueTask SetValueAsync(object? value, ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseValue = await Prefix.EvalAsync(context, ct).ConfigureAwait(false);

        if (baseValue is null)
        {
            throw new PropertyNotFoundException(
                $"赋值失败：'{ExpressionText}' 的基表达式求值为 null（对应 JUEL 的 \"Target unreachable\"）。");
        }

        await ElResolver.SetPropertyAsync(context, baseValue, _name, value, ct).ConfigureAwait(false);
    }
}

/// <summary>索引访问 <c>${a[b]}</c>。对照 JUEL 的 <c>AstBracket</c>（默认 <c>NULL_PROPERTIES</c> 关 ⇒ strict）。</summary>
internal sealed class ElBracketNode : ElPropertyNode
{
    private readonly ElNode _index;

    public ElBracketNode(ElNode prefix, ElNode index, string expressionText)
        : base(prefix, expressionText, strict: true)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    protected override async ValueTask<(object? Property, bool IsNull)> GetPropertyAsync(
        ElContext context,
        CancellationToken ct)
    {
        var index = await _index.EvalAsync(context, ct).ConfigureAwait(false);

        return (index, index is null);
    }

    protected override ValueTask<object?> ResolveAsync(
        ElContext context,
        object baseValue,
        object? property,
        CancellationToken ct) =>
        ElResolver.ResolveIndexAsync(context, baseValue, property, ct);
}

/// <summary>实参列表。对应 JUEL 的 <c>AstParameters</c>（先全量求值再调用）。</summary>
internal sealed class ElParametersNode
{
    private readonly IReadOnlyList<ElNode> _parameters;

    public ElParametersNode(IReadOnlyList<ElNode> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _parameters = parameters;
    }

    public async ValueTask<object?[]> EvalAsync(ElContext context, CancellationToken ct)
    {
        var values = new object?[_parameters.Count];

        for (var i = 0; i < _parameters.Count; i++)
        {
            values[i] = await _parameters[i].EvalAsync(context, ct).ConfigureAwait(false);
        }

        return values;
    }
}

/// <summary>
/// 方法调用 <c>${a.b(x)}</c>。对应 JUEL 的 <c>AstMethod</c>。
/// <para>基为 null ⇒ 返回 null（与属性访问的静默规则一致；本子集的默认口径，已在 §15 登记）。</para>
/// </summary>
internal sealed class ElMethodNode : ElNode
{
    private readonly ElNode _prefix;
    private readonly string _method;
    private readonly ElParametersNode _parameters;

    public ElMethodNode(ElNode prefix, string method, ElParametersNode parameters)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(parameters);

        _prefix = prefix;
        _method = method;
        _parameters = parameters;
    }

    public override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var baseValue = await _prefix.EvalAsync(context, ct).ConfigureAwait(false);

        if (baseValue is null)
        {
            return null;
        }

        var arguments = await _parameters.EvalAsync(context, ct).ConfigureAwait(false);

        return await ElResolver.InvokeAsync(context, baseValue, _method, arguments, ct).ConfigureAwait(false);
    }
}
