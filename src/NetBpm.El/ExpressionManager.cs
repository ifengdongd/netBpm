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
    /// <summary>缓存条目上限（默认 2048）。</summary>
    /// <remarks>
    /// 表达式来自流程定义，正常模型只有几十条；上限存在只为兜底「生成式模型 / 恶意上传
    /// 让缓存无限增长」这一类内存泄漏。淘汰按插入顺序（FIFO）：BPMN 的表达式集合稳定，
    /// 不需要精确 LRU 的代价。
    /// </remarks>
    public const int DefaultMaxCacheSize = 2048;

    private readonly ConcurrentDictionary<string, IExpression> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();
    private readonly int _maxCacheSize;

    /// <param name="maxCacheSize">缓存条目上限，见 <see cref="DefaultMaxCacheSize"/>。</param>
    public ExpressionManager(int maxCacheSize = DefaultMaxCacheSize)
    {
        if (maxCacheSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCacheSize),
                maxCacheSize,
                "表达式缓存上限必须为正整数。");
        }

        _maxCacheSize = maxCacheSize;
    }

    /// <summary>当前缓存的表达式数量（诊断 / 测试用）。</summary>
    public int CacheCount => _cache.Count;

    /// <inheritdoc />
    public IExpression CreateExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrEmpty(expression);

        // key 必须与解析口径一致：解析前 trim，缓存 key 若用原文，
        // "${a}" 与 " ${a}" 会各存一份 AST，且带空白的 key 永远命中不了下一次相同的写法。
        var text = expression.Trim();

        if (_cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var created = new ValueExpression(text, ElParser.Parse(text));

        if (!_cache.TryAdd(text, created))
        {
            // 并发下别的线程先写入：用既有的，保证「同一文本 ⇒ 同一个实例」
            return _cache.TryGetValue(text, out var existing) ? existing : created;
        }

        _insertionOrder.Enqueue(text);
        TrimToCapacity();

        return created;
    }

    /// <summary>
    /// 文本中是否含求值段（<c>${</c> / <c>#{</c>）。
    /// <para>没有求值段的纯文本是合法的"字面量表达式"，只不过求值出来还是它自己。</para>
    /// </summary>
    public bool IsExpression(string? expression) =>
        !string.IsNullOrEmpty(expression)
        && (expression.Contains("${", StringComparison.Ordinal) || expression.Contains("#{", StringComparison.Ordinal));

    /// <summary>超限时按插入顺序淘汰，直到回到上限内。</summary>
    private void TrimToCapacity()
    {
        // 并发下多个线程可能同时进入：多淘汰几条只会丢缓存（下次重新解析），不会丢正确性。
        while (_cache.Count > _maxCacheSize && _insertionOrder.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }

    /// <summary>
    /// 表达式实例。对应 JUEL 的 <c>TreeValueExpression</c>：解析结果（AST）在创建时就固定下来，
    /// 求值只带一个 <see cref="ElContext"/>。
    /// </summary>
    private sealed class ValueExpression : IExpression
    {
        private readonly ElNode _root;

        private IReadOnlyCollection<string>? _variableNames;

        public ValueExpression(string text, ElNode root)
        {
            ExpressionText = text;
            _root = root;
        }

        public string ExpressionText { get; }

        /// <inheritdoc />
        public IReadOnlyCollection<string> GetVariableNames()
        {
            // 表达式实例不可变、且按原文复用，因此变量名集合只需提取一次；
            // 这里的懒加载天然是幂等的（并发算出两份等价集合也不影响正确性）。
            if (_variableNames is { } cached)
            {
                return cached;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            _root.CollectVariableNames(names);

            var snapshot = (IReadOnlyCollection<string>)names.ToArray();
            _variableNames = snapshot;

            return snapshot;
        }

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
