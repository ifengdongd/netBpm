using System.Collections.Concurrent;
using NetBpm.El.Ast;

namespace NetBpm.El;

/// <summary>
/// 表达式管理器。对应 Activiti 的 <c>org.activiti.engine.impl.el.ExpressionManager</c>
/// （解析结果缓存 + 整串 <c>trim()</c> 后交给 JUEL 解析）。
/// <para>
/// 表达式语言本身见 <see cref="ElParser"/>（语法/优先级）、<see cref="TypeConverter"/>（隐式转换）、
/// <see cref="BooleanOperations"/>（相等与比较）、<see cref="NumberOperations"/>（算术）、
/// <see cref="ElResolver"/>（标识符/属性/索引/方法）。
/// </para>
/// <para>
/// ⚠️ 与 Activiti 一致：**整串先 trim 再解析**，因此 <c>"Hello ${name} "</c> 的尾随空格会丢失。
/// 照搬这一点是刻意的 —— 它是真实引擎的行为，对拍时不该出现"我们更宽松"的假差异。
/// </para>
/// </summary>
public sealed class ExpressionManager : IExpressionManager
{
    private readonly ConcurrentDictionary<string, IExpression> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IExpression CreateExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression);

        return _cache.GetOrAdd(expression, static text => new ValueExpression(text, ElParser.Parse(text.Trim())));
    }

    /// <summary>
    /// 文本中是否含求值段（<c>${</c> / <c>#{</c>）。
    /// <para>没有求值段的纯文本是合法的"字面量表达式"，只不过求值出来还是它自己。</para>
    /// </summary>
    public bool IsExpression(string? expression) =>
        !string.IsNullOrEmpty(expression)
        && (expression.Contains("${", StringComparison.Ordinal) || expression.Contains("#{", StringComparison.Ordinal));

    /// <summary>
    /// 表达式实例。对应 JUEL 的 <c>TreeValueExpression</c>：解析结果（AST）在创建时就固定下来，
    /// 求值只带一个 <see cref="ElContext"/>。
    /// </summary>
    private sealed class ValueExpression : IExpression
    {
        private readonly ElNode _root;

        public ValueExpression(string text, ElNode root)
        {
            ExpressionText = text;
            _root = root;
        }

        public string ExpressionText { get; }

        /// <summary>是否为纯文本（JUEL 的 <c>isLiteralText()</c>）。</summary>
        public bool IsLiteralText => _root.IsLiteralText;

        public async ValueTask<object?> GetValueAsync(IVariableScope scope, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(scope);

            try
            {
                return await _root.EvalAsync(new ElContext(scope), ct).ConfigureAwait(false);
            }
            catch (PropertyNotFoundException exception)
            {
                // 对应 JuelExpression：把解析类异常包成"表达式里用了未知属性/方法"
                throw new ElException(
                    $"表达式里用到了未知属性：{ExpressionText}（{exception.Message}）",
                    exception);
            }
            catch (MethodNotFoundException exception)
            {
                throw new ElException(
                    $"表达式里用到了未知方法：{ExpressionText}（{exception.Message}）",
                    exception);
            }
        }

        public ValueTask SetValueAsync(object? value, IVariableScope scope, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(scope);

            return _root.SetValueAsync(value, new ElContext(scope), ct);
        }
    }
}
