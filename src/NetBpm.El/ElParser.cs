using System.Globalization;
using NetBpm.El.Ast;

namespace NetBpm.El;

/// <summary>
/// EL 语法分析（递归下降）。语法与优先级**逐条照搬** JUEL 的 <c>tree.impl.Parser</c>：
/// <code>
/// expr  := or ('?' expr ':' expr)?        // 三元右结合
/// or    := and (('||' | 'or') and)*
/// and   := eq  (('&amp;&amp;' | 'and') eq)*
/// eq    := cmp (('==' | '!=' | 'eq' | 'ne') cmp)*
/// cmp   := add (('&lt;' | '&lt;=' | '&gt;' | '&gt;=' | 'lt' | 'le' | 'gt' | 'ge') add)*
/// add   := mul (('+' | '-') mul)*
/// mul   := unary (('*' | '/' | 'div' | '%' | 'mod') unary)*
/// unary := ('-' | '!' | 'not' | 'empty') unary | value
/// value := primary ('.' IDENT ('(' params ')')? | '[' expr ']')*
/// </code>
/// <para>
/// ⚠️ <b>本子集不实现 EL 函数</b>（<c>${fn:length(x)}</c> / <c>${myFunc(x)}</c>）：它们依赖函数映射器，
/// 在 BPMN 条件里极少使用。遇到时<b>解析期响亮失败</b>，不静默当变量处理。
/// </para>
/// </summary>
internal static class ElParser
{
    /// <summary>解析整个表达式文本（可能由多段文本与求值段组成）。</summary>
    public static ElNode Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var scanner = new Scanner(expression);
        var state = new ParserState(scanner, expression);
        var segments = new List<ElNode>();
        bool? deferred = null;

        while (true)
        {
            var token = state.Current;

            if (token.Symbol == ElSymbol.Eof)
            {
                break;
            }

            if (token.Symbol == ElSymbol.Text)
            {
                segments.Add(new ElTextNode(token.Image));
                state.Next();
                continue;
            }

            // ${} 与 #{} 不得混用（JUEL 的 Parser.tree 用首个求值段的标志强制一致）
            if (deferred is null)
            {
                deferred = scanner.IsDeferred;
            }
            else if (deferred != scanner.IsDeferred)
            {
                throw new ElSyntaxException($"'{expression}' 中 ${{}} 与 #{{}} 不能混用。");
            }

            state.Next();
            segments.Add(state.Expression());
            state.Expect(ElSymbol.EndEval);
            state.Next();
        }

        return segments.Count switch
        {
            0 => new ElTextNode(string.Empty),
            1 => segments[0],
            _ => new ElCompositeNode(segments),
        };
    }

    /// <summary>
    /// 解析状态（带前瞻缓冲）。
    /// <para>
    /// ⚠️ 前瞻是<b>必需</b>的：<c>IDENT ':'</c> 既可能是命名空间函数 <c>fn:length(x)</c>，
    /// 也可能是三目里的 <c>?</c> 分支后紧跟的 <c>:</c>。只有看到 <c>IDENT ':' IDENT '('</c>
    /// 才能判定为命名空间函数（JUEL 的 <c>nonliteral()</c> 同样做 4 个记号的前瞻）。
    /// </para>
    /// </summary>
    private sealed class ParserState
    {
        /// <summary>
        /// 表达式嵌套层数上限。
        /// <para>
        /// 🔑 递归下降解析器必须自己设上限：<c>${{{{{{…</c> 这类输入在没有上限时会一直递归，
        /// 最终 <see cref="StackOverflowException"/> —— 该异常<b>不可捕获</b>，进程直接退出。
        /// 而部署期校验就会解析表达式，等于「能提交 BPMN 的人可以打崩引擎进程」。
        /// </para>
        /// <para>100 层足够覆盖任何人工可读的条件表达式（每层嵌套实际消耗 2~3 个单位）。</para>
        /// </summary>
        private const int MaxExpressionDepth = 100;

        private readonly Scanner _scanner;
        private readonly string _expression;
        private readonly List<ElToken> _tokens = [];
        private int _index;
        private int _depth;

        public ParserState(Scanner scanner, string expression)
        {
            _scanner = scanner;
            _expression = expression;
        }

        /// <summary>
        /// 进入一层递归。配合 <c>using var</c> 使用：作用域结束自动回退，
        /// 成功路径与异常路径都不会让计数漂移。
        /// </summary>
        private DepthScope EnterDepth()
        {
            if (++_depth > MaxExpressionDepth)
            {
                throw new ElSyntaxException(
                    $"表达式嵌套层数超过上限 {MaxExpressionDepth}（'{_expression}'）；" +
                    "请简化条件表达式，过深的嵌套会耗尽解析栈。");
            }

            return new DepthScope(this);
        }

        /// <summary>递归深度的作用域（结构体，不产生装箱）。</summary>
        private readonly struct DepthScope : IDisposable
        {
            private readonly ParserState _state;

            internal DepthScope(ParserState state) => _state = state;

            public void Dispose() => _state._depth--;
        }

        public ElToken Current => At(0);

        public void Next() => _index++;

        /// <summary>取相对当前位置偏移 <paramref name="offset"/> 的记号（按需从扫描器取）。</summary>
        private ElToken At(int offset)
        {
            while (_tokens.Count <= _index + offset)
            {
                _tokens.Add(_scanner.Next());
            }

            return _tokens[_index + offset];
        }

        public void Expect(ElSymbol symbol)
        {
            if (Current.Symbol != symbol)
            {
                throw new ElSyntaxException(
                    $"语法错误：'{_expression}' 中期望 {Describe(symbol)}，实际是 '{Current.Image}'。");
            }
        }

        public ElNode Expression()
        {
            using var depth = EnterDepth();

            var condition = Or();

            if (Current.Symbol != ElSymbol.Question)
            {
                return condition;
            }

            Next();
            var yes = Expression();
            Expect(ElSymbol.Colon);
            Next();
            var no = Expression();

            return new ElConditionalNode(condition, yes, no);
        }

        private ElNode Or() => Binary(And, ElSymbol.Or, ElBinaryOperator.Or, "||");

        private ElNode And() => Binary(Eq, ElSymbol.And, ElBinaryOperator.And, "&&");

        private ElNode Eq() => Binary(
            Compare,
            [
                (ElSymbol.Eq, ElBinaryOperator.Eq, "=="),
                (ElSymbol.Ne, ElBinaryOperator.Ne, "!="),
            ]);

        private ElNode Compare() => Binary(
            Add,
            [
                (ElSymbol.Lt, ElBinaryOperator.Lt, "<"),
                (ElSymbol.Gt, ElBinaryOperator.Gt, ">"),
                (ElSymbol.Le, ElBinaryOperator.Le, "<="),
                (ElSymbol.Ge, ElBinaryOperator.Ge, ">="),
            ]);

        private ElNode Add() => Binary(
            Multiply,
            [
                (ElSymbol.Plus, ElBinaryOperator.Add, "+"),
                (ElSymbol.Minus, ElBinaryOperator.Sub, "-"),
            ]);

        private ElNode Multiply() => Binary(
            Unary,
            [
                (ElSymbol.Mul, ElBinaryOperator.Mul, "*"),
                (ElSymbol.Div, ElBinaryOperator.Div, "/"),
                (ElSymbol.Mod, ElBinaryOperator.Mod, "%"),
            ]);

        private ElNode Unary()
        {
            // 一元的链式写法（!!!!x）是纯递归、不消耗记号的，必须计入深度
            using var depth = EnterDepth();

            switch (Current.Symbol)
            {
                case ElSymbol.Minus:
                    Next();

                    return new ElUnaryNode(ElUnaryOperator.Negate, Unary());

                case ElSymbol.Not:
                    Next();

                    return new ElUnaryNode(ElUnaryOperator.Not, Unary());

                case ElSymbol.Empty:
                    Next();

                    return new ElUnaryNode(ElUnaryOperator.Empty, Unary());

                default:
                    return Value();
            }
        }

        private ElNode Value()
        {
            var node = Primary();

            while (true)
            {
                switch (Current.Symbol)
                {
                    case ElSymbol.Dot:
                        {
                            Next();
                            Expect(ElSymbol.Identifier);
                            var name = Current.Image;
                            Next();

                            // '.' 后跟 '(' ⇒ 方法调用（JUEL 的 METHOD_INVOCATIONS 特性，默认开）
                            node = Current.Symbol == ElSymbol.LParen
                                ? new ElMethodNode(node, name, Parameters())
                                : new ElDotNode(node, name, _expression);

                            break;
                        }

                    case ElSymbol.LBracket:
                        {
                            Next();
                            var index = Expression();
                            Expect(ElSymbol.RBracket);
                            Next();
                            node = new ElBracketNode(node, index, _expression);

                            break;
                        }

                    case ElSymbol.LParen:
                        throw new ElSyntaxException(
                            $"本子集不支持 EL 函数调用（'{_expression}'）；函数依赖函数映射器，条件表达式里请改用变量与运算符。");

                    default:
                        return node;
                }
            }
        }

        private ElNode Primary()
        {
            // '(' → Expression → ... → Primary 是嵌套括号的递归路径
            using var depth = EnterDepth();

            switch (Current.Symbol)
            {
                case ElSymbol.Integer:
                    {
                        var value = long.Parse(Current.Image, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        Next();

                        return new ElLiteralNode(value);
                    }

                case ElSymbol.Float:
                    {
                        var value = double.Parse(Current.Image, NumberStyles.Float, CultureInfo.InvariantCulture);
                        Next();

                        return new ElLiteralNode(value);
                    }

                case ElSymbol.String:
                    {
                        var value = Current.Image;
                        Next();

                        return new ElLiteralNode(value);
                    }

                case ElSymbol.True:
                    Next();

                    return new ElLiteralNode(true);

                case ElSymbol.False:
                    Next();

                    return new ElLiteralNode(false);

                case ElSymbol.Null:
                    Next();

                    return new ElLiteralNode(null);

                case ElSymbol.Identifier:
                    {
                        var name = Current.Image;

                        // 命名空间函数 fn:length(x)：必须看到完整的 IDENT ':' IDENT '(' 才判定，
                        // 否则会把三目的 ':' 误吞掉
                        if (At(1).Symbol == ElSymbol.Colon
                            && At(2).Symbol == ElSymbol.Identifier
                            && At(3).Symbol == ElSymbol.LParen)
                        {
                            throw new ElSyntaxException(
                                $"本子集不支持 EL 命名空间函数（'{_expression}' 中的 '{name}:'）。");
                        }

                        Next();

                        return new ElIdentifierNode(name);
                    }

                case ElSymbol.LParen:
                    {
                        Next();
                        var inner = Expression();
                        Expect(ElSymbol.RParen);
                        Next();

                        return new ElNestedNode(inner);
                    }

                case ElSymbol.InstanceOf:
                    throw new ElSyntaxException($"本子集不支持 instanceof 运算符（'{_expression}'）。");

                default:
                    throw new ElSyntaxException(
                        $"语法错误：'{_expression}' 中的 '{Current.Image}' 不是合法的表达式起点。");
            }
        }

        private ElParametersNode Parameters()
        {
            Expect(ElSymbol.LParen);
            Next();

            var parameters = new List<ElNode>();

            if (Current.Symbol != ElSymbol.RParen)
            {
                parameters.Add(Expression());

                while (Current.Symbol == ElSymbol.Comma)
                {
                    Next();
                    parameters.Add(Expression());
                }
            }

            Expect(ElSymbol.RParen);
            Next();

            return new ElParametersNode(parameters);
        }

        private ElNode Binary(
            Func<ElNode> next,
            ElSymbol symbol,
            ElBinaryOperator @operator,
            string operatorText) =>
            Binary(next, [(symbol, @operator, operatorText)]);

        private ElNode Binary(
            Func<ElNode> next,
            IReadOnlyList<(ElSymbol Symbol, ElBinaryOperator Operator, string Text)> operators)
        {
            var left = next();

            while (true)
            {
                var matched = operators.FirstOrDefault(candidate => candidate.Symbol == Current.Symbol);

                if (matched.Text is null)
                {
                    return left;
                }

                Next();
                left = new ElBinaryNode(matched.Operator, left, next(), matched.Text);
            }
        }

        private static string Describe(ElSymbol symbol) => symbol switch
        {
            ElSymbol.EndEval => "'}'",
            ElSymbol.RParen => "')'",
            ElSymbol.RBracket => "']'",
            ElSymbol.Colon => "':'",
            ElSymbol.Identifier => "标识符",
            _ => symbol.ToString(),
        };
    }
}
