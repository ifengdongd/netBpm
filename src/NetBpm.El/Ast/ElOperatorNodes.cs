namespace NetBpm.El.Ast;

/// <summary>二元运算符。</summary>
internal enum ElBinaryOperator
{
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    Eq,
    Ne,
    Lt,
    Gt,
    Le,
    Ge,
    And,
    Or,
}

/// <summary>
/// 二元运算。对应 JUEL 的 <c>AstBinary</c> 及其派生类
/// （算术在 <c>NumberOperations</c>、比较与相等在 <c>BooleanOperations</c>、逻辑在 <c>AstBinary</c> 内短路）。
/// </summary>
internal sealed class ElBinaryNode : ElNode
{
    private readonly ElBinaryOperator _operator;
    private readonly ElNode _left;
    private readonly ElNode _right;
    private readonly string _operatorText;

    public ElBinaryNode(ElBinaryOperator @operator, ElNode left, ElNode right, string operatorText)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        _operator = @operator;
        _left = left;
        _right = right;
        _operatorText = operatorText;
    }

    public override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 短路：JUEL 的 AND/OR 先对左操作数做 toBoolean
        if (_operator is ElBinaryOperator.And or ElBinaryOperator.Or)
        {
            var left = TypeConverter.CoerceToBoolean(await _left.EvalAsync(context, ct).ConfigureAwait(false));

            if (_operator == ElBinaryOperator.And)
            {
                if (!left)
                {
                    return false;
                }
            }
            else if (left)
            {
                return true;
            }

            return TypeConverter.CoerceToBoolean(await _right.EvalAsync(context, ct).ConfigureAwait(false));
        }

        var o1 = await _left.EvalAsync(context, ct).ConfigureAwait(false);
        var o2 = await _right.EvalAsync(context, ct).ConfigureAwait(false);

        return _operator switch
        {
            ElBinaryOperator.Add => NumberOperations.Add(o1, o2),
            ElBinaryOperator.Sub => NumberOperations.Sub(o1, o2),
            ElBinaryOperator.Mul => NumberOperations.Mul(o1, o2),
            ElBinaryOperator.Div => NumberOperations.Div(o1, o2),
            ElBinaryOperator.Mod => NumberOperations.Mod(o1, o2),
            ElBinaryOperator.Eq => BooleanOperations.Eq(o1, o2),
            ElBinaryOperator.Ne => BooleanOperations.Ne(o1, o2),
            ElBinaryOperator.Lt => BooleanOperations.Lt(o1, o2),
            ElBinaryOperator.Gt => BooleanOperations.Gt(o1, o2),
            ElBinaryOperator.Le => BooleanOperations.Le(o1, o2),
            ElBinaryOperator.Ge => BooleanOperations.Ge(o1, o2),
            _ => throw new ElException($"未知运算符 '{_operatorText}'。"),
        };
    }

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        _left.CollectVariableNames(names);
        _right.CollectVariableNames(names);
    }
}

/// <summary>一元运算符。</summary>
internal enum ElUnaryOperator
{
    /// <summary>取负。</summary>
    Negate,

    /// <summary>逻辑非。</summary>
    Not,

    /// <summary>空值判定。</summary>
    Empty,
}

/// <summary>一元运算。对应 JUEL 的 <c>AstUnary</c>。</summary>
internal sealed class ElUnaryNode : ElNode
{
    private readonly ElUnaryOperator _operator;
    private readonly ElNode _operand;

    public ElUnaryNode(ElUnaryOperator @operator, ElNode operand)
    {
        ArgumentNullException.ThrowIfNull(operand);

        _operator = @operator;
        _operand = operand;
    }

    public override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = await _operand.EvalAsync(context, ct).ConfigureAwait(false);

        return _operator switch
        {
            ElUnaryOperator.Negate => NumberOperations.Neg(value),
            ElUnaryOperator.Not => !TypeConverter.CoerceToBoolean(value),
            ElUnaryOperator.Empty => BooleanOperations.Empty(value),
            _ => throw new ElException("未知的一元运算符。"),
        };
    }

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _operand.CollectVariableNames(names);
    }
}

/// <summary>三目 <c>${a ? b : c}</c>。对应 JUEL 的 <c>AstConditional</c>（两分支各自惰性求值）。</summary>
internal sealed class ElConditionalNode : ElNode
{
    private readonly ElNode _condition;
    private readonly ElNode _yes;
    private readonly ElNode _no;

    public ElConditionalNode(ElNode condition, ElNode yes, ElNode no)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(yes);
        ArgumentNullException.ThrowIfNull(no);

        _condition = condition;
        _yes = yes;
        _no = no;
    }

    public override async ValueTask<object?> EvalAsync(ElContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var condition = TypeConverter.CoerceToBoolean(await _condition.EvalAsync(context, ct).ConfigureAwait(false));

        return condition
            ? await _yes.EvalAsync(context, ct).ConfigureAwait(false)
            : await _no.EvalAsync(context, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void CollectVariableNames(ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        // 三目是惰性求值，但预取阶段无法判定走哪个分支，取并集最稳妥：
        // 多查一个变量只是多读一行，漏查则会在求值时退化成 N+1。
        _condition.CollectVariableNames(names);
        _yes.CollectVariableNames(names);
        _no.CollectVariableNames(names);
    }
}
