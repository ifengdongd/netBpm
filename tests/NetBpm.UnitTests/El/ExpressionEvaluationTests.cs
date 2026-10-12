using NetBpm.El;

namespace NetBpm.UnitTests.El;

/// <summary>
/// 表达式语言的行为验收（开发计划 §5.2 / P3：≥200 条用例）。
/// <para>
/// 🔑 期望值**逐条取自 JUEL fork 的源码**（<c>misc/TypeConverterImpl.java</c>、<c>misc/BooleanOperations.java</c>、
/// <c>misc/NumberOperations.java</c>、<c>tree/impl/ast/*</c>），不是"看起来合理"的猜测 ——
/// 因为流程分支条件判错会直接导致业务走错分支（风险 R2）。
/// </para>
/// <para>
/// 表里刻意塞满了反直觉的用例：<c>"1" + "2"</c> 是 <b>3</b>、<c>5/2</c> 是 <b>2.5</b>、
/// <c>null &lt; 1</c> 是 <b>false</b>、<c>"abc" == 1</c> 会<b>抛异常</b>、
/// <c>1.0</c> 的字符串形式是 <c>"1.0"</c>。
/// </para>
/// </summary>
public sealed class ExpressionEvaluationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>索引访问用的数组（必须是数组：与 JUEL 的 <c>ArrayELResolver</c> 对齐）。</summary>
    private static readonly int[] Numbers = [3, 1, 2];

    /// <summary>正常求值：表达式 → 期望值（类型也参与断言：<c>3L</c> 与 <c>3</c> 不同）。</summary>
    public static TheoryData<string, object?> Cases => new()
    {
        // ── 字面量、文本、定界符 ────────────────────────────────────────────────
        { "${true}", true },
        { "${false}", false },
        { "${null}", null },
        { "${1}", 1L },
        { "${1.5}", 1.5d },
        { "${'text'}", "text" },
        { "${'a\\'b'}", "a'b" },
        { "${'a\\\\b'}", "a\\b" },
        { "hello", "hello" },
        { "hello world", "hello world" },
        { "${1}${2}", "12" },
        { "#{1}", 1L },
        { "#{true}", true },
        { "x#{1}", "x1" },
        { "\"quoted\"", "\"quoted\"" },

        // ── 复合表达式：逐段 toString 拼接 ──────────────────────────────────────
        { "a${1}b", "a1b" },
        { "${1}${1.0}", "11.0" },
        { "x=${flag}", "x=true" },
        { "x${null}y", "xy" },
        { "amount is ${amount}", "amount is 10" },
        { "empty=${emptyList}", "empty=[]" },
        { "\\${amount}", "${amount}" },

        // ── Java 风格的数值 toString（必须是复合表达式才会走 toString）────────────
        { "v${1.0}", "v1.0" },
        { "v${2.5}", "v2.5" },
        { "v${10.0}", "v10.0" },
        { "v${10000000.0}", "v1.0E7" },
        { "v${0.001}", "v0.001" },
        { "v${0.0001}", "v1.0E-4" },
        { "v${1.0E20}", "v1.0E20" },
        { "-${1}", "-1" },
        { "${1.0}", 1.0d },
        { "${2.5}", 2.5d },
        { "${10000000.0}", 1.0E7d },
        { "${0.0001}", 0.0001d },
        { "list=${items}", "list=[a, b, c]" },
        { "map=${order}", "map={amount=5, code=A-1}" },

        // ── 算术：+ - * / % ─────────────────────────────────────────────────────
        { "${1 + 2}", 3L },
        { "${1 + 2 * 3}", 7L },
        { "${(1 + 2) * 3}", 9L },
        { "${10 - 3}", 7L },
        { "${6 * 7}", 42L },
        { "${5 / 2}", 2.5d },                                  // 恒为浮点除
        { "${5 div 2}", 2.5d },
        { "${4 / 2}", 2.0d },                                  // 即便整除也是 double
        { "${1 / 0}", double.PositiveInfinity },               // 不抛异常
        { "${-1 / 0}", double.NegativeInfinity },
        { "${0 / 0}", double.NaN },
        { "${5 % 2}", 1L },
        { "${5 mod 2}", 1L },
        { "${-5 % 3}", -2L },                                  // 符号跟随被除数
        { "${5.5 % 2}", 1.5d },
        { "${1 + 1.0}", 2.0d },
        { "${1 + '2'}", 3L },                                  // 字符串被当数字
        { "${'1' + '2'}", 3L },                                // ⚠️ 不是 "12"
        { "${'1.0' + 1}", 2.0d },
        { "${null + 5}", 5L },
        { "${5 + null}", 5L },
        { "${null + null}", 0L },
        { "${null + 5.0}", 5.0d },
        { "${'0.5' * 4}", 2.0d },
        { "${amount + amount}", 20L },
        { "${total - 1}", 99L },

        // ── 一元运算 ────────────────────────────────────────────────────────────
        { "${-1}", -1L },
        { "${-'2'}", -2L },
        { "${-amount}", -10 },
        { "${!true}", false },
        { "${!flag}", false },
        { "${not flag}", false },
        { "${!null}", true },
        { "${!''}", true },
        { "${-'1.5'}", -1.5d },

        // ── 相等：JUEL 的强转顺序是关键 ─────────────────────────────────────────
        { "${1 == 1}", true },
        { "${1 == 2}", false },
        { "${1 == 1.0}", true },
        { "${1 != 2}", true },
        { "${1 eq 1}", true },
        { "${1 ne 2}", true },
        { "${null == null}", true },
        { "${nothing == null}", true },
        { "${null == 1}", false },
        { "${nothing == 'x'}", false },
        { "${'1' == 1}", true },                               // ⚠️ 字符串按数字比
        { "${'1' == 1.0}", true },                             // 浮点分支先命中
        { "${'abc' == 'abc'}", true },
        { "${'abc' == 'def'}", false },
        { "${name == 'kermit'}", true },
        { "${true == 'true'}", true },
        { "${true == 'xyz'}", false },
        { "${flag == true}", true },
        { "${nothing != null}", false },
        { "${items == emptyList}", false },
        { "${order == order}", true },
        { "${nothing == nothing}", true },

        // ── 比较：null 一律 false，不抛异常 ─────────────────────────────────────
        { "${1 < 2}", true },
        { "${2 < 1}", false },
        { "${2 > 1}", true },
        { "${1 <= 1}", true },
        { "${1 >= 1}", true },
        { "${1 <= 2}", true },
        { "${2 >= 1}", true },
        { "${1 lt 2}", true },
        { "${2 gt 1}", true },
        { "${1 le 1}", true },
        { "${1 ge 1}", true },
        { "${'a' < 'b'}", true },                              // 字典序
        { "${'B' < 'a'}", true },                              // 码元序：'B'(66) < 'a'(97)
        { "${name > 'a'}", true },
        { "${1 < 1.5}", true },
        { "${null < 1}", false },                              // ⚠️ 不抛异常
        { "${null > 1}", false },
        { "${null <= 1}", false },
        { "${nothing >= 1}", false },
        { "${null <= null}", true },
        { "${1 > null}", false },
        { "${amount < 11}", true },
        { "${'2' > '10'}", true },                             // 字符串比字面序

        // ── 逻辑与短路 ──────────────────────────────────────────────────────────
        { "${true && true}", true },
        { "${true && false}", false },
        { "${true or false}", true },
        { "${false || false}", false },
        { "${flag and true}", true },
        { "${1 == 1 && 2 == 2}", true },
        { "${name && flag}", false },                          // 非 "true" 的字符串 ⇒ false
        { "${'true' && flag}", true },
        { "${false && unknownVariable}", false },              // 短路：右侧不求值
        { "${true || unknownVariable}", true },                // 短路：右侧不求值
        { "${false || flag}", true },
        { "${'' || 'true'}", true },

        // ── empty ───────────────────────────────────────────────────────────────
        { "${empty nothing}", true },
        { "${empty ''}", true },
        { "${empty name}", false },
        { "${empty emptyList}", true },
        { "${empty items}", false },
        { "${empty order}", false },
        { "${not empty items}", true },

        // ── 三目 ────────────────────────────────────────────────────────────────
        { "${flag ? 'yes' : 'no'}", "yes" },
        { "${!flag ? 'yes' : 'no'}", "no" },
        { "${amount > 5 ? 'big' : 'small'}", "big" },
        { "${true ? 1 : unknownVariable}", 1L },               // 惰性：未选中的分支不求值
        { "${false ? unknownVariable : 2}", 2L },
        { "${false ? 1 : true ? 2 : 3}", 2L },                 // 右结合
        { "${false ? unknownVariable : 2}", 2L },              // 惰性：未选中的分支不求值

        // ── 属性与索引 ──────────────────────────────────────────────────────────
        { "${order.amount}", 5 },
        { "${order.code}", "A-1" },
        { "${order['code']}", "A-1" },
        { "${order[\"code\"]}", "A-1" },
        { "${items[0]}", "a" },
        { "${items[2]}", "c" },
        { "${items[10]}", null },                              // 越界 ⇒ 静默 null
        { "${numbers[1]}", 1 },
        { "${order.unknownProp}", null },                      // ⚠️ Map 缺键是静默 null（与 POJO 属性不同）
        { "${nothing.anything}", null },                       // 基为 null ⇒ 静默 null
        { "${nothing[0]}", null },
        { "${user.name}", "gonzo" },
        { "${user.role}", "admin" },
        { "${user.department.code}", "HR" },
        { "${execution.processInstanceId}", "pi-1" },

        // ── 方法调用 ────────────────────────────────────────────────────────────
        { "${name.length()}", 6L },
        { "${name.isEmpty()}", false },
        { "${name.contains('erm')}", true },
        { "${name.startsWith('ker')}", true },
        { "${name.endsWith('mit')}", true },
        { "${name.endsWith('xyz')}", false },
        { "${name.toUpperCase()}", "KERMIT" },
        { "${'ABC'.toLowerCase()}", "abc" },
        { "${name.substring(1)}", "ermit" },
        { "${name.substring(1, 3)}", "er" },
        { "${name.indexOf('m')}", 3L },
        { "${name.charAt(0)}", 'k' },
        { "${name.equals('kermit')}", true },
        { "${name.equalsIgnoreCase('KERMIT')}", true },
        { "${items.size()}", 3L },
        { "${items.isEmpty()}", false },
        { "${items.contains('a')}", true },
        { "${items.contains('z')}", false },
        { "${emptyList.isEmpty()}", true },
        { "${order.get('code')}", "A-1" },
        { "${order.containsKey('code')}", true },
        { "${user.hasRole('admin')}", true },
        { "${user.hasRole('guest')}", false },
        { "${user.greet('hi')}", "hi gonzo" },
        { "${execution.processInstanceId}", "pi-1" },
        { "${execution.id}", "exec-1" },
        { "${execution.getVariable('amount')}", 10 },
        { "${execution.hasVariable('amount')}", true },
        { "${execution.hasVariable('nope')}", false },

        // ── 优先级与结合性 ──────────────────────────────────────────────────────
        { "${1 + 2 == 3}", true },
        { "${1 + 2 * 3 == 7}", true },
        { "${true || false && false}", true },                 // && 优先于 ||
        { "${!true || true}", true },
        { "${-1 + 2}", 1L },
        { "${1 < 2 == true}", true },
        { "${1 == 1 ? 2 : 3}", 2L },
        { "${amount * 2 + 1}", 21L },
        { "${amount > 5 && amount < 20}", true },
    };

    /// <summary>响亮失败：表达式 → 期望的消息片段（断言"必须报错"，不静默给个默认值）。</summary>
    public static TheoryData<string, string> FailingCases => new()
    {
        // 语法
        { "${1 +}", "语法错误" },
        { "${}", "语法错误" },
        { "${(1}", "语法错误" },
        { "${1 ? 2}", "语法错误" },
        { "${1 2}", "语法错误" },
        { "${a b}", "语法错误" },
        { "${1 eq}", "语法错误" },
        { "${'unclosed}", "词法错误" },
        { "${'\\n'}", "词法错误" },                            // ⚠️ JUEL 只支持 \\ 与转义引号
        { "${x instanceof String}", "instanceof" },
        { "${fn:length(items)}", "命名空间函数" },
        { "${myFunction(1)}", "函数" },

        // 标识符解析不出 ⇒ 响亮失败（不是"条件为假"）
        { "${unknownVariable}", "未知属性" },
        { "${user.unknownField}", "未知属性" },
        { "${items.unknownMethod()}", "未知方法" },
        { "${name.charAt()}", "未知方法" },
        { "${name[0]}", "未知属性" },                          // JUEL 不支持字符串下标

        // 类型不兼容
        { "${true + 1}", "Boolean" },
        { "${null + true}", "Boolean" },
        { "${amount && flag}", "Boolean" },                    // 数字不能当布尔
        { "${items + 1}", "Long" },
        { "${!amount}", "Boolean" },
        { "${-items}", "取负" },
        { "${amount < items}", "Long" },                       // 整数分支先命中，转 Long 失败
        { "${nothing + items}", "Long" },
        { "${name - 1}", "Double" },                           // ⚠️ "kermit" 含小写 e ⇒ 走浮点分支（JUEL 的 isDotEe 大小写敏感）
        { "${nothing * name}", "Double" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Evaluate_ShouldMatchJuelSemantics(string expression, object? expected)
    {
        var manager = new ExpressionManager();
        var actual = await manager.CreateExpression(expression).GetValueAsync(CreateScope(), Ct);

        Assert.Equal(expected, actual);

        if (expected is not null)
        {
            Assert.Equal(expected.GetType(), actual?.GetType());
        }
    }

    [Theory]
    [MemberData(nameof(FailingCases))]
    public async Task Evaluate_ShouldFailLoudly(string expression, string expectedMessagePart)
    {
        var manager = new ExpressionManager();
        var exception = await Assert.ThrowsAnyAsync<ElException>(
            async () => await manager.CreateExpression(expression).GetValueAsync(CreateScope(), Ct));

        Assert.Contains(expectedMessagePart, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MixedDelimiters_ShouldFailAtParseTime()
    {
        var manager = new ExpressionManager();

        // JUEL 用首个求值段的定界符强制后续一致
        await Assert.ThrowsAsync<ElSyntaxException>(() => Task.FromResult(manager.CreateExpression("${1}#{2}")));
    }

    [Fact]
    public async Task PlainText_ShouldBeLiteralText()
    {
        var manager = new ExpressionManager();

        Assert.Equal("plain", await manager.CreateExpression("plain").GetValueAsync(CreateScope(), Ct));
        Assert.True(((ExpressionManager)manager).IsExpression("${x}"));
        Assert.False(manager.IsExpression("plain"));
    }

    [Fact]
    public async Task SetValue_ShouldWriteExistingVariableOnly()
    {
        var manager = new ExpressionManager();
        var scope = CreateScope();

        await manager.CreateExpression("${amount}").SetValueAsync(99, scope, Ct);
        Assert.Equal(99, await scope.GetVariableAsync("amount", Ct));

        // Activiti：只在变量已存在时写入
        await Assert.ThrowsAsync<PropertyNotFoundException>(
            async () => await manager.CreateExpression("${brandNew}").SetValueAsync(1, scope, Ct));
    }

    private static TestScope CreateScope()
    {
        var scope = new TestScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["amount"] = 10,
            ["total"] = 100L,
            ["ratio"] = 0.5d,
            ["flag"] = true,
            ["name"] = "kermit",
            ["nothing"] = null,
            ["order"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["amount"] = 5,
                ["code"] = "A-1",
            },
            ["items"] = new List<object?> { "a", "b", "c" },
            ["emptyList"] = new List<object?>(),
            ["numbers"] = Numbers,
            ["user"] = new TestUser("gonzo", "admin", new TestDepartment("HR")),
        });

        return scope;
    }

    /// <summary>
    /// 带属性与变量的作用域：<c>execution</c> 隐式对象求值出的就是它，
    /// 因此它需要 <c>ProcessInstanceId</c> / <c>Id</c> 这类属性（对应 <c>${execution.processInstanceId}</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这两个属性**刻意保留为实例属性**（尽管不读实例数据）：EL 的属性解析只走实例属性
    /// （与 Java 的 <c>BeanELResolver</c> 一致），改成 <c>static</c> 会让 <c>${execution.id}</c> 解析失败，
    /// 测的就不是真实语义了。
    /// </remarks>
    private sealed class TestScope : SimpleVariableScope
    {
        public TestScope(IDictionary<string, object?> variables)
            : base(variables)
        {
        }

#pragma warning disable CA1822 // 必须是实例属性（见类型注释）
        public string ProcessInstanceId => "pi-1";

        public string Id => "exec-1";
#pragma warning restore CA1822
    }

    private sealed record TestUser(string Name, string Role, TestDepartment Department)
    {
        public bool HasRole(string role) => string.Equals(Role, role, StringComparison.Ordinal);

        public string Greet(string greeting) => $"{greeting} {Name}";
    }

    private sealed record TestDepartment(string Code);
}
