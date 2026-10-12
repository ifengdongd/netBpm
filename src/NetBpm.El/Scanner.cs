namespace NetBpm.El;

/// <summary>
/// EL 词法符号。对照 JUEL 的 <c>org.activiti.core.el.juel.tree.impl.Scanner.Symbol</c>。
/// </summary>
internal enum ElSymbol
{
    /// <summary>求值段之外的纯文本。</summary>
    Text,

    /// <summary><c>${</c>：立即求值段的开始。</summary>
    StartEval,

    /// <summary><c>#{</c>：延迟求值段的开始（JUEL 中与 <c>${</c> 求值行为相同）。</summary>
    StartEvalDeferred,

    /// <summary><c>}</c>：求值段结束。</summary>
    EndEval,

    /// <summary>输入结束。</summary>
    Eof,

    Identifier,
    Integer,
    Float,
    String,

    Dot,
    Comma,
    LBracket,
    RBracket,
    LParen,
    RParen,
    Question,
    Colon,

    Plus,
    Minus,
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
    Not,
    Empty,

    /// <summary>关键字 <c>null</c>。</summary>
    Null,

    /// <summary>关键字 <c>true</c>。</summary>
    True,

    /// <summary>关键字 <c>false</c>。</summary>
    False,

    /// <summary>关键字 <c>instanceof</c>（本子集<b>不支持</b>，解析期响亮失败）。</summary>
    InstanceOf,
}

/// <summary>词法记号。</summary>
/// <param name="Symbol">符号。</param>
/// <param name="Image">原始文本（标识符名 / 字面量内容 / 文本段内容）。</param>
/// <param name="Position">在表达式中的位置（报错用）。</param>
internal readonly record struct ElToken(ElSymbol Symbol, string Image, int Position);

/// <summary>
/// EL 词法分析器。对照 JUEL 的 <c>Scanner</c>。
/// <para>
/// 两种模式交替：<b>文本模式</b>（读取 <c>${</c> / <c>#{</c> 之外的纯文本，处理 <c>\$</c> <c>\#</c> <c>\\</c> 转义）
/// 与 <b>求值模式</b>（读取表达式记号，空白仅在此模式被跳过）。
/// </para>
/// <para>
/// ⚠️ 字符串转义<b>与 JUEL 一致地严格</b>：只允许 <c>\\</c> 与转义引号，<c>"\n"</c> 这类是<b>词法错误</b>
/// （JUEL 的 <c>nextString</c> 会抛 <c>ScanException</c>）。照搬这一点是刻意的 —— 放宽会掩盖语料里的真实写法差异。
/// </para>
/// </summary>
internal sealed class Scanner
{
    private readonly string _input;
    private int _position;
    private bool _inEval;

    public Scanner(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
    }

    /// <summary>当前记号是否位于求值段（<c>${}</c> / <c>#{}</c>）内部。</summary>
    public bool InEvaluation => _inEval;

    /// <summary>当前求值段是否为 <c>#{}</c>（延迟）。</summary>
    public bool IsDeferred { get; private set; }

    /// <summary>取下一个记号。</summary>
    public ElToken Next()
    {
        return _inEval ? NextEval() : NextText();
    }

    /// <summary>
    /// 文本模式：读取直到未转义的 <c>${</c> / <c>#{</c> 或输入结束。
    /// <para>转义（对照 JUEL <c>nextText</c>）：<c>\$</c>→<c>$</c>、<c>\#</c>→<c>#</c>、<c>\\</c>→<c>\</c>。</para>
    /// </summary>
    private ElToken NextText()
    {
        var start = _position;
        var builder = new System.Text.StringBuilder();

        while (_position < _input.Length)
        {
            var current = _input[_position];

            if (current == '\\' && _position + 1 < _input.Length)
            {
                var next = _input[_position + 1];

                if (next is '$' or '#' or '\\')
                {
                    builder.Append(next);
                    _position += 2;
                    continue;
                }
            }

            if (current is '$' or '#'
                && _position + 1 < _input.Length
                && _input[_position + 1] == '{')
            {
                IsDeferred = current == '#';
                _inEval = true;
                _position += 2;

                // 文本段为空时（表达式以 ${ 开头）直接给出求值段开始记号
                if (builder.Length == 0)
                {
                    return new ElToken(
                        IsDeferred ? ElSymbol.StartEvalDeferred : ElSymbol.StartEval,
                        _input[start.._position],
                        start);
                }

                _position -= 2;
                IsDeferred = false;
                _inEval = false;

                return new ElToken(ElSymbol.Text, builder.ToString(), start);
            }

            builder.Append(current);
            _position++;
        }

        return builder.Length == 0
            ? new ElToken(ElSymbol.Eof, string.Empty, _position)
            : new ElToken(ElSymbol.Text, builder.ToString(), start);
    }

    /// <summary>求值模式：跳过空白后读取一个记号。</summary>
    private ElToken NextEval()
    {
        while (_position < _input.Length && char.IsWhiteSpace(_input[_position]))
        {
            _position++;
        }

        if (_position >= _input.Length)
        {
            throw new ElSyntaxException($"表达式未闭合（缺少 '}}'）：'{_input}'");
        }

        var start = _position;
        var current = _input[_position];

        switch (current)
        {
            case '}':
                _position++;
                _inEval = false;

                return new ElToken(ElSymbol.EndEval, "}", start);

            case '.':
                // JUEL：'.' 后跟数字时是小数点（.5），否则是属性分隔符
                if (_position + 1 < _input.Length && char.IsAsciiDigit(_input[_position + 1]))
                {
                    return NextNumber();
                }

                _position++;

                return new ElToken(ElSymbol.Dot, ".", start);

            case ',': _position++; return new ElToken(ElSymbol.Comma, ",", start);
            case '[': _position++; return new ElToken(ElSymbol.LBracket, "[", start);
            case ']': _position++; return new ElToken(ElSymbol.RBracket, "]", start);
            case '(': _position++; return new ElToken(ElSymbol.LParen, "(", start);
            case ')': _position++; return new ElToken(ElSymbol.RParen, ")", start);
            case '?': _position++; return new ElToken(ElSymbol.Question, "?", start);
            case ':': _position++; return new ElToken(ElSymbol.Colon, ":", start);
            case '+': _position++; return new ElToken(ElSymbol.Plus, "+", start);
            case '-': _position++; return new ElToken(ElSymbol.Minus, "-", start);
            case '*': _position++; return new ElToken(ElSymbol.Mul, "*", start);
            case '/': _position++; return new ElToken(ElSymbol.Div, "/", start);
            case '%': _position++; return new ElToken(ElSymbol.Mod, "%", start);
            case '"':
            case '\'':
                return NextString();

            case '=':
                if (Peek(1) == '=')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.Eq, "==", start);
                }

                throw new ElSyntaxException($"词法错误：'{_input}' 第 {_position} 个字符处的 '=' 只能是 '=='。");

            case '!':
                if (Peek(1) == '=')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.Ne, "!=", start);
                }

                _position++;

                return new ElToken(ElSymbol.Not, "!", start);

            case '<':
                if (Peek(1) == '=')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.Le, "<=", start);
                }

                _position++;

                return new ElToken(ElSymbol.Lt, "<", start);

            case '>':
                if (Peek(1) == '=')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.Ge, ">=", start);
                }

                _position++;

                return new ElToken(ElSymbol.Gt, ">", start);

            case '&':
                if (Peek(1) == '&')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.And, "&&", start);
                }

                throw new ElSyntaxException($"词法错误：'{_input}' 第 {_position} 个字符处的 '&' 只能是 '&&'。");

            case '|':
                if (Peek(1) == '|')
                {
                    _position += 2;

                    return new ElToken(ElSymbol.Or, "||", start);
                }

                throw new ElSyntaxException($"词法错误：'{_input}' 第 {_position} 个字符处的 '|' 只能是 '||'。");
        }

        if (char.IsAsciiDigit(current))
        {
            return NextNumber();
        }

        if (IsIdentifierStart(current))
        {
            return NextIdentifier();
        }

        throw new ElSyntaxException($"词法错误：'{_input}' 第 {_position} 个字符处无法识别的 '{current}'。");
    }

    private char Peek(int offset) =>
        _position + offset < _input.Length ? _input[_position + offset] : '\0';

    /// <summary>标识符与关键字（对照 JUEL 的关键字表）。</summary>
    private ElToken NextIdentifier()
    {
        var start = _position;
        _position++;

        while (_position < _input.Length && IsIdentifierPart(_input[_position]))
        {
            _position++;
        }

        var image = _input[start.._position];

        var symbol = image switch
        {
            "null" => ElSymbol.Null,
            "true" => ElSymbol.True,
            "false" => ElSymbol.False,
            "empty" => ElSymbol.Empty,
            "div" => ElSymbol.Div,
            "mod" => ElSymbol.Mod,
            "not" => ElSymbol.Not,
            "and" => ElSymbol.And,
            "or" => ElSymbol.Or,
            "eq" => ElSymbol.Eq,
            "ne" => ElSymbol.Ne,
            "lt" => ElSymbol.Lt,
            "gt" => ElSymbol.Gt,
            "le" => ElSymbol.Le,
            "ge" => ElSymbol.Ge,
            "instanceof" => ElSymbol.InstanceOf,
            _ => ElSymbol.Identifier,
        };

        return new ElToken(symbol, image, start);
    }

    /// <summary>数字字面量：整数 → <c>long</c>，含小数点或指数 → <c>double</c>（对照 JUEL 的 <c>nextNumber</c>）。</summary>
    private ElToken NextNumber()
    {
        var start = _position;
        var isFloat = false;

        while (_position < _input.Length && char.IsAsciiDigit(_input[_position]))
        {
            _position++;
        }

        if (_position < _input.Length && _input[_position] == '.')
        {
            isFloat = true;
            _position++;

            while (_position < _input.Length && char.IsAsciiDigit(_input[_position]))
            {
                _position++;
            }
        }

        if (_position < _input.Length && (_input[_position] == 'e' || _input[_position] == 'E'))
        {
            var lookahead = _position + 1;

            if (lookahead < _input.Length && (_input[lookahead] == '+' || _input[lookahead] == '-'))
            {
                lookahead++;
            }

            if (lookahead < _input.Length && char.IsAsciiDigit(_input[lookahead]))
            {
                isFloat = true;
                _position = lookahead;

                while (_position < _input.Length && char.IsAsciiDigit(_input[_position]))
                {
                    _position++;
                }
            }
        }

        var image = _input[start.._position];

        return new ElToken(isFloat ? ElSymbol.Float : ElSymbol.Integer, image, start);
    }

    /// <summary>
    /// 字符串字面量。⚠️ 转义<b>只允许</b> <c>\\</c> 与转义引号，其余一律词法错误（与 JUEL 一致）。
    /// </summary>
    private ElToken NextString()
    {
        var start = _position;
        var quote = _input[_position];
        _position++;

        var builder = new System.Text.StringBuilder();

        while (true)
        {
            if (_position >= _input.Length)
            {
                throw new ElSyntaxException($"词法错误：'{_input}' 中第 {start} 个字符起的字符串未闭合。");
            }

            var current = _input[_position];

            if (current == '\\')
            {
                if (_position + 1 >= _input.Length)
                {
                    throw new ElSyntaxException($"词法错误：'{_input}' 中字符串以 '\\' 结尾。");
                }

                var escaped = _input[_position + 1];

                if (escaped == '\\' || escaped == quote)
                {
                    builder.Append(escaped);
                    _position += 2;
                    continue;
                }

                throw new ElSyntaxException(
                    $"词法错误：不支持的转义 '\\{escaped}'（只支持 '\\{quote}' 与 '\\\\'）：'{_input}'。");
            }

            if (current == quote)
            {
                _position++;

                return new ElToken(ElSymbol.String, builder.ToString(), start);
            }

            builder.Append(current);
            _position++;
        }
    }

    /// <summary>Java 标识符首字符（<c>isJavaIdentifierStart</c>）：字母、<c>_</c>、<c>$</c>。</summary>
    private static bool IsIdentifierStart(char value) =>
        char.IsLetter(value) || value is '_' or '$';

    /// <summary>Java 标识符后续字符（<c>isJavaIdentifierPart</c>）。</summary>
    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';
}
