using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;

namespace NetBpm.CorpusImporter;

/// <summary>
/// 现成语料导入器（开发计划 §9.3）：把 Activiti 源码树里的测试 BPMN 捞进
/// <c>tests/corpus/from-activiti/</c>，作为对拍的<b>回归基线</b>（§9.3：457 个纯顺序流用例
/// "作为回归基线非常充足"，但网关语义必须自建）。
///
/// <para>
/// 用法：
/// <code>
/// dotnet run --project tests/corpus/importer -- --source E:\DataCode\Activiti-9.0.0
/// dotnet run --project tests/corpus/importer -- --source ... --dry-run    # 只看统计，不写盘
/// dotnet run --project tests/corpus/importer -- --check                   # 漂移校验（需要有源码）
/// </code>
/// </para>
///
/// <para>
/// 🔑 <b>收录口径（宁可少收，不可收进跑不动的）</b>，四道闸门缺一不可：
/// <list type="number">
///   <item><description><b>只含子集元素</b>：出现服务任务/脚本任务/子流程/边界事件/事件定义/
///   监听器/字段注入/多实例/泳道等一律剔除 —— 它们在两端是"实现差异"，进对拍会产生不可判定的差异
///   （与 <c>DifferentialEngine</c> 的语料约束一致）；</description></item>
///   <item><description><b>能被本引擎解析</b>（<see cref="BpmnXmlConverter"/>）；</description></item>
///   <item><description><b>能过本引擎的部署校验</b>（<see cref="ProcessValidator"/>）——
///   过不了就等于收进一份跑不动的语料；</description></item>
///   <item><description><b>至少一个用户任务</b>：否则脚本无事可做，"跑通了"什么也没验证。</description></item>
/// </list>
/// </para>
///
/// <para>
/// 脚本一律是 N 步 <c>completeAnyTask</c>（"完成任一待办，没有待办就收工"）：
/// 这样语料不需要手写脚本、也不依赖某个流程的具体形状，两种引擎按同一条规则推进。
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>脚本步数上限：远超这些测试流程的用户任务数，多出来的步在"没有待办"时自然收工。</summary>
    private const int ScriptSteps = 40;

    private static readonly JsonSerializerOptions CaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>非子集元素/能力的标记（含原因，用于统计"为什么没收"）。</summary>
    private static readonly (string Marker, string Reason)[] NonPortableMarkers =
    [
        ("<serviceTask", "服务任务"),
        ("<sendTask", "发送任务"),
        ("<receiveTask", "接收任务"),
        ("<scriptTask", "脚本任务"),
        ("<businessRuleTask", "业务规则任务"),
        ("<subProcess", "子流程"),
        ("<callActivity", "调用活动"),
        ("<boundaryEvent", "边界事件"),
        ("<intermediateCatchEvent", "中间捕获事件"),
        ("<intermediateThrowEvent", "中间抛出事件"),
        // 抽象任务（BPMN 的 <task>，含带前缀写法）：不在子集元素表内
        ("<task ", "抽象任务 <task>"),
        (":task ", "抽象任务 <task>"),
        ("<inclusiveGateway", "包容网关"),
        ("<eventBasedGateway", "事件网关"),
        ("<complexGateway", "复杂网关"),
        ("<laneSet", "泳道"),
        ("<dataObject", "数据对象"),
        ("<dataStoreReference", "数据存储引用"),
        ("multiInstanceLoopCharacteristics", "多实例"),
        ("<executionListener", "执行监听器"),
        ("<taskListener", "任务监听器"),
        ("activiti:field", "字段注入"),
        ("EventDefinition", "事件定义"),
        ("activiti:class", "服务任务实现（class）"),
        ("activiti:delegateExpression", "服务任务实现（delegateExpression）"),
        ("activiti:expression", "服务任务实现（expression）"),
    ];

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"❌ {exception.Message}");

            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var corpusRoot = Path.Combine(FindRepositoryRoot(), "tests", "corpus", "from-activiti");

        var sourceRoot = Path.GetFullPath(options.Source);

        if (!Directory.Exists(sourceRoot))
        {
            throw new InvalidOperationException(
                $"Activiti 源码目录不存在：{sourceRoot}。用 --source 指定（默认 E:\\DataCode\\Activiti-9.0.0）。"
                + "注意：CI 上没有该源码树，因此本导入器的 --check 只在开发机上跑；"
                + "CI 只校验语料本身（tests/NetBpm.DifferentialTests/CorpusTests 会解析全部语料）。");
        }

        var candidates = Directory
            .EnumerateFiles(sourceRoot, "*.bpmn20.xml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(sourceRoot, "*.bpmn", SearchOption.AllDirectories))
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"源码目录：{sourceRoot}");
        Console.WriteLine($"候选 BPMN：{candidates.Count} 个");

        var stats = new Stats();
        var imported = new List<Imported>();

        foreach (var candidate in candidates)
        {
            var verdict = Inspect(candidate, sourceRoot, stats);

            if (verdict is not null)
            {
                imported.Add(verdict);
            }
        }

        // 名字去重（同名文件在不同目录里很常见）
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var final = new List<Imported>();

        foreach (var item in imported)
        {
            var name = item.Stem;

            if (!used.Add(name))
            {
                name = $"{item.Stem}-{item.Disambiguator}";
                used.Add(name);
            }

            final.Add(item with { Name = name });
        }

        stats.Report();

        if (options.DryRun)
        {
            Console.WriteLine("（--dry-run：未写盘）");

            return 0;
        }

        if (options.Check)
        {
            return Check(corpusRoot, final);
        }

        foreach (var item in final)
        {
            Write(corpusRoot, item);
        }

        var removed = CleanupStale(corpusRoot, final);

        Console.WriteLine($"已导入 {final.Count} 个语料到 {corpusRoot}");

        if (removed > 0)
        {
            // 不留孤儿：被剔除的语料若留在盘上，会被语料装载器捡回来继续跑
            Console.WriteLine($"已清理 {removed} 个不再收录的语料文件");
        }

        return 0;
    }

    /// <summary>删除不再收录的语料文件（口径变化后必须清理，否则收回来的语料会继续参与对拍）。</summary>
    private static int CleanupStale(string corpusRoot, IReadOnlyList<Imported> imported)
    {
        if (!Directory.Exists(corpusRoot))
        {
            return 0;
        }

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in imported)
        {
            expected.Add(Path.Combine(corpusRoot, item.Shape, $"{item.Name}.bpmn20.xml"));
            expected.Add(Path.Combine(corpusRoot, item.Shape, $"{item.Name}.case.json"));
            expected.Add(Path.Combine(corpusRoot, item.Shape, $"{item.Name}.oracle.json"));
        }

        var removed = 0;

        foreach (var path in Directory
            .EnumerateFiles(corpusRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".bpmn20.xml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".case.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".oracle.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (expected.Contains(path))
            {
                continue;
            }

            File.Delete(path);
            removed++;
        }

        // 空目录（形态目录下语料全被剔除时）
        foreach (var directory in Directory.EnumerateDirectories(corpusRoot, "*", SearchOption.AllDirectories))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return removed;
    }

    /// <summary>四道闸门。返回 <see langword="null"/> 表示不收（原因已计入统计）。</summary>
    private static Imported? Inspect(string path, string sourceRoot, Stats stats)
    {
        stats.Scanned++;

        var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        string xml;

        try
        {
            xml = Normalize(File.ReadAllText(path));
        }
        catch (IOException exception)
        {
            stats.Skip($"读取失败：{exception.Message}", relative);

            return null;
        }

        foreach (var (marker, reason) in NonPortableMarkers)
        {
            if (xml.Contains(marker, StringComparison.Ordinal))
            {
                stats.Skip($"含非子集能力：{reason}", relative);

                return null;
            }
        }

        BpmnModel model;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            model = new BpmnXmlConverter().ConvertToBpmnModel(stream);
        }
        catch (Exception exception)
        {
            stats.Skip($"解析失败：{exception.GetType().Name}", relative);

            return null;
        }

        if (model.Processes.Count != 1)
        {
            stats.Skip(model.Processes.Count == 0 ? "没有流程定义" : $"多流程（{model.Processes.Count} 个）", relative);

            return null;
        }

        var process = model.Processes[0];
        var errors = new ProcessValidator().Validate(process);

        if (errors.Count > 0)
        {
            stats.Skip($"未过校验：{errors[0].Message}", relative);

            return null;
        }

        var elements = process.FlowElements;

        if (!elements.OfType<UserTask>().Any())
        {
            stats.Skip("没有用户任务（脚本无事可做）", relative);

            return null;
        }

        // 表达式依赖运行时 Bean / POJO 的语料：播种驱动不了，应当在**导入期**就剔除，
        // 而不是等运行期报"未知方法"再当成"不适用"（那会掩盖真实的解析器缺陷）。
        if (UnsupportedExpression(process) is { } unsupportedReason)
        {
            stats.Skip($"表达式子集外：{unsupportedReason}", relative);

            return null;
        }

        var hasParallel = elements.OfType<ParallelGateway>().Any();
        var hasExclusive = elements.OfType<ExclusiveGateway>().Any();
        var shape = (hasParallel, hasExclusive) switch
        {
            (false, false) => "sequence",
            (true, false) => "parallel",
            (false, true) => "exclusive",
            (true, true) => "mixed",
        };

        stats.Accept(shape);

        var stem = Path.GetFileName(path);
        stem = stem.EndsWith(".bpmn20.xml", StringComparison.OrdinalIgnoreCase)
            ? stem[..^".bpmn20.xml".Length]
            : Path.GetFileNameWithoutExtension(stem);

        return new Imported(
            Sanitize(stem),
            shape,
            relative,
            xml,
            Disambiguator: Sanitize(Path.GetDirectoryName(relative)?.Replace('/', '-') ?? "root"),
            UserTasks: elements.OfType<UserTask>().Count())
        {
            Variables = SeedVariables(process),
        };
    }

    private static void Write(string corpusRoot, Imported item)
    {
        var directory = Path.Combine(corpusRoot, item.Shape);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, $"{item.Name}.bpmn20.xml"), item.Bpmn);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = item.Name,
            ["group"] = $"from-activiti/{item.Shape}",
            ["description"] = $"导入自 Activiti 源码：{item.SourcePath}（{item.UserTasks} 个用户任务）",
            ["actions"] = Enumerable.Range(0, ScriptSteps).Select(_ => (object)new { kind = "completeAnyTask" }),
            ["resourceName"] = $"{item.Name}.bpmn20.xml",
        };

        if (item.Variables.Count > 0)
        {
            // 变量表：确定性播种（见 SeedVariables 的说明）
            payload["variables"] = item.Variables.ToDictionary(
                pair => pair.Key,
                pair => (object?)pair.Value,
                StringComparer.Ordinal);
        }

        File.WriteAllText(
            Path.Combine(directory, $"{item.Name}.case.json"),
            JsonSerializer.Serialize(payload, CaseJsonOptions) + "\n");
    }

    /// <summary>
    /// 从 BPMN 里**静态提取**条件与属性表达式引用的标识符，并按确定值播种。
    /// <para>
    /// 🔑 为什么需要：真实的 Activiti 测试在启动前会把变量塞进流程（<c>startProcessInstanceByKey(key, vars)</c>），
    /// 而语料脚本不会替它们编数据；少了变量，<c>${input == 1}</c> 会按 JUEL 语义抛"无法解析标识符"
    /// （这是<b>正确</b>行为，不是缺陷）。播种只为让条件"可判定"。
    /// </para>
    /// <para>
    /// 值统一取字符串 <c>"1"</c>：字符串在 JUEL 里既可被当数字强转（<c>"1" &gt; 100</c> 不报错），
    /// 又不会让 <c>==</c> 落到布尔分支抛异常 —— 换成 <c>true</c>/数字都会在别的比较里炸掉。
    /// 它<b>不表达任何业务含义</b>，只保证两条引擎面对同一份输入。
    /// </para>
    /// </summary>
    private static Dictionary<string, string> SeedVariables(Process process)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var dateNames = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var flow in process.FlowElements.OfType<SequenceFlow>())
        {
            Collect(flow.ConditionExpression, names);
        }

        foreach (var userTask in process.FlowElements.OfType<UserTask>())
        {
            Collect(userTask.Assignee, names);
            Collect(userTask.Owner, names);
            Collect(userTask.FormKey, names);
            Collect(userTask.Priority, names);
            Collect(userTask.Category, names);
            Collect(userTask.SkipExpression, names);

            // dueDate 的表达式必须求值成日期（引擎只接受 Date 或 ISO-8601 串），播种值要按类型给
            Collect(userTask.DueDate, names);
            Collect(userTask.DueDate, dateNames);

            foreach (var candidate in userTask.CandidateUsers)
            {
                Collect(candidate, names);
            }

            foreach (var candidate in userTask.CandidateGroups)
            {
                Collect(candidate, names);
            }
        }

        return names.ToDictionary(
            name => name,
            name => dateNames.Contains(name) ? SeedDate : SeedValue,
            StringComparer.Ordinal);
    }

    /// <summary>通用播种值（见 <c>SeedVariables</c> 的说明）。</summary>
    private const string SeedValue = "1";

    /// <summary>日期类表达式的播种值（ISO-8601，引擎与 JUEL 都认）。</summary>
    private const string SeedDate = "2026-01-01T00:00:00";

    /// <summary>EL 关键字与隐式对象（这些不是流程变量）。</summary>
    private static readonly HashSet<string> ElReservedWords = new(StringComparer.Ordinal)
    {
        "true", "false", "null", "empty", "and", "or", "not", "div", "mod",
        "eq", "ne", "lt", "gt", "le", "ge", "instanceof", "execution",
        // 发起人变量由引擎在启动时写入（activiti:initiator），不需要也无法播种
        "initiator",
    };

    /// <summary>属性访问 / 方法调用（<c>base.name</c>）：<c>base</c> 不是 <c>execution</c> 就驱动不了。</summary>
    private static readonly System.Text.RegularExpressions.Regex MemberAccess = new(
        @"(?<![A-Za-z0-9_$])(?<base>[A-Za-z_$][A-Za-z0-9_$]*)\s*\.\s*[A-Za-z_$]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>裸函数调用 <c>f(...)</c>（本子集不支持 EL 函数）。</summary>
    private static readonly System.Text.RegularExpressions.Regex BareCall = new(
        @"(?<![A-Za-z0-9_$.])[A-Za-z_$][A-Za-z0-9_$]*\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 语料里的表达式是否依赖运行时对象（Spring Bean 方法、POJO / JSON 属性）。
    /// <para>
    /// 这些在真实部署里由 Spring 容器与业务对象提供，对拍脚手架没有也不该伪造它们 ——
    /// 收进来只会得到"未知方法/未知属性"的执行失败。
    /// </para>
    /// </summary>
    private static string? UnsupportedExpression(Process process)
    {
        foreach (var text in ExpressionTexts(process))
        {
            var stripped = StripStringLiterals(text);

            foreach (System.Text.RegularExpressions.Match match in MemberAccess.Matches(stripped))
            {
                if (!string.Equals(match.Groups["base"].Value, "execution", StringComparison.Ordinal))
                {
                    return $"属性/方法访问 '{match.Value.Trim()}'（基不是 execution）";
                }
            }

            if (BareCall.IsMatch(stripped))
            {
                return $"EL 函数调用 '{BareCall.Match(stripped).Value.Trim()}'";
            }
        }

        return null;
    }

    /// <summary>语料里所有可能是表达式的文本（条件 + 用户任务属性）。</summary>
    private static IEnumerable<string> ExpressionTexts(Process process)
    {
        foreach (var flow in process.FlowElements.OfType<SequenceFlow>())
        {
            if (!string.IsNullOrEmpty(flow.ConditionExpression))
            {
                yield return flow.ConditionExpression;
            }
        }

        foreach (var userTask in process.FlowElements.OfType<UserTask>())
        {
            // ⚠️ 任务属性里凡是"可以是表达式"的都要算上：漏掉一个（例如 formKey）就会在运行期
            // 冒出一个没播种的标识符，表现为"无法解析标识符"，很像是引擎缺陷
            foreach (var attribute in new[]
            {
                userTask.Assignee,
                userTask.Owner,
                userTask.FormKey,
                userTask.DueDate,
                userTask.Priority,
                userTask.Category,
                userTask.SkipExpression,
            })
            {
                if (!string.IsNullOrEmpty(attribute))
                {
                    yield return attribute;
                }
            }

            foreach (var candidate in userTask.CandidateUsers.Concat(userTask.CandidateGroups))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>把字符串字面量替换成不含点号与括号的占位，避免正则误判。</summary>
    private static string StripStringLiterals(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            if (current is '\'' or '"')
            {
                var quote = current;
                index++;

                while (index < text.Length && text[index] != quote)
                {
                    index += text[index] == '\\' ? 2 : 1;
                }

                builder.Append("str");
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    /// <summary>从一段（可能是 <c>${...}</c> 的）文本里抽出<b>顶层</b>标识符。</summary>
    private static void Collect(string? text, SortedSet<string> names)
    {
        if (string.IsNullOrEmpty(text)
            || (!text.Contains("${", StringComparison.Ordinal) && !text.Contains("#{", StringComparison.Ordinal)))
        {
            // 不含求值段 ⇒ 是字面量（例如用户名字符串），不是变量引用
            return;
        }

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            // 跳过定界符 ${ / #{（'$' 本身是合法的 Java 标识符首字符，不排除会把 "${input}" 里的 '$' 当成变量）
            if (current is '$' or '#' && index + 1 < text.Length && text[index + 1] == '{')
            {
                index++;
                continue;
            }

            // 跳过字符串字面量（含转义）
            if (current is '\'' or '"')
            {
                var quote = current;
                index++;

                while (index < text.Length && text[index] != quote)
                {
                    index += text[index] == '\\' ? 2 : 1;
                }

                continue;
            }

            if (!char.IsLetter(current) && current is not ('_' or '$'))
            {
                continue;
            }

            var start = index;

            while (index + 1 < text.Length && (char.IsLetterOrDigit(text[index + 1]) || text[index + 1] is '_' or '$'))
            {
                index++;
            }

            var name = text[start..(index + 1)];
            var previous = start > 0 ? text[start - 1] : '\0';
            var next = index + 1 < text.Length ? text[index + 1] : '\0';

            // 属性访问（a.b）与函数（f(x)、ns:f(x)）都不是流程变量
            if (previous == '.' || next is '(' or ':')
            {
                continue;
            }

            if (ElReservedWords.Contains(name))
            {
                continue;
            }

            names.Add(name);
        }
    }

    /// <summary>漂移校验：磁盘上的语料是否与导入器一致（需要有 Activiti 源码）。</summary>
    private static int Check(string corpusRoot, IReadOnlyList<Imported> imported)
    {
        var drift = new List<string>();

        foreach (var item in imported)
        {
            var directory = Path.Combine(corpusRoot, item.Shape);

            if (!Matches(Path.Combine(directory, $"{item.Name}.bpmn20.xml"), item.Bpmn))
            {
                drift.Add($"{item.Shape}/{item.Name}.bpmn20.xml");
            }

            var casePath = Path.Combine(directory, $"{item.Name}.case.json");

            if (!File.Exists(casePath))
            {
                drift.Add($"{item.Shape}/{item.Name}.case.json");
            }
        }

        if (drift.Count == 0)
        {
            Console.WriteLine($"漂移校验通过：{imported.Count} 个现成语料与导入器一致。");

            return 0;
        }

        Console.Error.WriteLine($"❌ 语料与导入器不一致（{drift.Count} 个文件），请重新导入：");

        foreach (var file in drift.Take(20))
        {
            Console.Error.WriteLine("  - " + file);
        }

        return 1;
    }

    private static bool Matches(string path, string expected) =>
        File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(expected);

    /// <summary>行尾统一为 LF：Windows 导入、Linux 校验必须逐字节一致。</summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-');
        }

        return builder.ToString();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed record Imported(
        string Stem,
        string Shape,
        string SourcePath,
        string Bpmn,
        string Disambiguator,
        int UserTasks)
    {
        /// <summary>去重后的语料名（默认与文件主干名一致）。</summary>
        public string Name { get; init; } = Stem;

        /// <summary>启动变量（让条件可判定的确定性播种，见 <c>SeedVariables</c>）。</summary>
        public Dictionary<string, string> Variables { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class Options
    {
        public string Source { get; private init; } = @"E:\DataCode\Activiti-9.0.0";

        public bool DryRun { get; private init; }

        public bool Check { get; private init; }

        public static Options Parse(string[] args)
        {
            var source = @"E:\DataCode\Activiti-9.0.0";
            var dryRun = false;
            var check = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--source":
                        source = args[++index];
                        break;
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "--check":
                        check = true;
                        break;
                    default:
                        throw new InvalidOperationException($"未知参数：{args[index]}");
                }
            }

            return new Options { Source = source, DryRun = dryRun, Check = check };
        }
    }

    /// <summary>统计（"为什么没收"和"收了多少"都要能回答）。</summary>
    private sealed class Stats
    {
        /// <summary>每个剔除原因留几条样本：数量只说明规模，样本才说明"要不要去修"。</summary>
        private const int SamplesPerReason = 3;

        private readonly Dictionary<string, int> _skips = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _samples = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _shapes = new(StringComparer.Ordinal);

        public int Scanned { get; set; }

        public void Skip(string reason, string? sample = null)
        {
            _skips[reason] = _skips.GetValueOrDefault(reason) + 1;

            if (sample is null)
            {
                return;
            }

            if (!_samples.TryGetValue(reason, out var list))
            {
                list = [];
                _samples[reason] = list;
            }

            if (list.Count < SamplesPerReason)
            {
                list.Add(sample);
            }
        }

        public void Accept(string shape) => _shapes[shape] = _shapes.GetValueOrDefault(shape) + 1;

        public void Report()
        {
            var accepted = _shapes.Values.Sum();

            Console.WriteLine();
            Console.WriteLine($"收录：{accepted} / {Scanned}");

            foreach (var shape in new[] { "sequence", "exclusive", "parallel", "mixed" })
            {
                Console.WriteLine($"  {shape,-9} {_shapes.GetValueOrDefault(shape)}");
            }

            Console.WriteLine("剔除原因（按数量降序）：");

            foreach (var (reason, count) in _skips.OrderByDescending(pair => pair.Value).Take(14))
            {
                Console.WriteLine($"  {count,5}  {reason}");

                foreach (var sample in _samples.GetValueOrDefault(reason, []))
                {
                    Console.WriteLine($"          例：{sample}");
                }
            }

            Console.WriteLine();
        }
    }
}
