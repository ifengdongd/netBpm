using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// CA1305：本文件用插值拼接 XML 文本，插值项只有**整数索引**与固定中文字面量。
// int 在任何区域设置下的默认格式都是同一个十进制表示（没有千分位/小数点），因此不存在
// 区域设置风险；若将来往插值里放浮点数或日期，必须改回 CultureInfo.InvariantCulture（并删掉本抑制）。
#pragma warning disable CA1305

namespace NetBpm.CorpusGenerator;

/// <summary>
/// 自建语料生成器（开发计划 §9.3：「自建语料由脚本生成，不要手写」，保证组合覆盖是穷举的
/// 而不是"想起来的那几个"）。
///
/// <para>
/// 用法：<c>dotnet run --project tests/corpus/generator -- &lt;语料根目录&gt;</c>
/// （默认 <c>tests/corpus</c>）。生成结果：<c>&lt;root&gt;/authored/&lt;类别&gt;/&lt;stem&gt;.bpmn20.xml</c>
/// + 同名 <c>.case.json</c>。
/// </para>
/// <para>
/// 🔑 <b>只生成"行为完全由引擎决定"的元素</b>（事件 / 网关 / 用户任务 / 手工任务 / 顺序流），
/// 不使用服务任务与脚本任务 —— 它们依赖两端各自的委托/脚本实现，属实现差异而非引擎语义差异，
/// 放进对拍会产生不可判定的差异（见 <c>DifferentialEngine</c> 的语料约束）。
/// </para>
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions CaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        var check = args.Contains("--check", StringComparer.Ordinal);

        var corpusRoot = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            ?? "tests/corpus";

        if (!Path.IsPathRooted(corpusRoot))
        {
            corpusRoot = Path.Combine(FindRepositoryRoot(), corpusRoot);
        }

        if (check)
        {
            return Check(corpusRoot);
        }

        var written = 0;

        foreach (var spec in BuildSpecs())
        {
            Write(corpusRoot, spec);
            written++;
        }

        Console.WriteLine($"已生成 {written} 个自建语料到 {corpusRoot}");

        return 0;
    }

    /// <summary>按分支数穷举各组合（分支 2 与 3 覆盖"两条流"与"多条流 + 默认流"两类形态）。</summary>
    private static IEnumerable<CaseSpec> BuildSpecs()
    {
        yield return SequentialTwoUserTasks();

        for (var branches = 2; branches <= 3; branches++)
        {
            yield return ParallelForkJoin(branches);
            yield return ExclusiveAllFalseUsesDefault(branches);
            yield return ExclusiveFirstTrueWins(branches);
            yield return NestedParallelThenExclusive(branches);
            yield return PartialJoinImpedance(branches);
            yield return SingleEntryParallelImpedance(branches);
        }
    }

    // ─────────────────────────── 语料形态 ───────────────────────────

    /// <summary>基线：两个用户任务顺序推进（保证最基础路径不退化）。</summary>
    private static CaseSpec SequentialTwoUserTasks() => new(
        "sequence/two-user-tasks",
        "顺序流基线：申请 → 审批 → 结束",
        Process("seq-two-user-tasks", """
            <startEvent id="start" />
            <userTask id="apply" name="申请" activiti:assignee="applicant" />
            <userTask id="review" name="审批" activiti:assignee="manager" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="review" />
            <sequenceFlow id="f3" sourceRef="review" targetRef="end" />
            """),
        Actions: [Complete("applicant"), Complete("manager")]);

    /// <summary>并行分叉 + Join：Join 必须等齐 N 条分支才放行归档任务。</summary>
    private static CaseSpec ParallelForkJoin(int branches)
    {
        var body = new StringBuilder();
        var actions = new List<object>();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <parallelGateway id="fork" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <userTask id="sign{index}" name="会签{index}" activiti:assignee="u{index}" />""");
            actions.Add(Complete($"u{index}"));
        }

        body.AppendLine("""  <parallelGateway id="join" />""");
        body.AppendLine("""  <userTask id="archive" name="归档" activiti:assignee="archivist" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f0" sourceRef="start" targetRef="fork" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <sequenceFlow id="fork{index}" sourceRef="fork" targetRef="sign{index}" />""");
            body.AppendLine($"""  <sequenceFlow id="join{index}" sourceRef="sign{index}" targetRef="join" />""");
        }

        body.AppendLine("""  <sequenceFlow id="fArchive" sourceRef="join" targetRef="archive" />""");
        body.AppendLine("""  <sequenceFlow id="fEnd" sourceRef="archive" targetRef="end" />""");

        actions.Add(Complete("archivist"));

        return new CaseSpec(
            $"gateway/parallel-fork-join-{branches}",
            $"{branches} 条并行分支的 fork/join：Join 必须等齐全部分支",
            Process($"parallel-fork-join-{branches}", body.ToString().TrimEnd()),
            actions);
    }

    /// <summary>排他网关 + 默认流：条件全假必须走默认流。</summary>
    private static CaseSpec ExclusiveAllFalseUsesDefault(int branches)
    {
        var body = new StringBuilder();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <userTask id="apply" name="申请" activiti:assignee="applicant" />""");
        body.AppendLine("""  <exclusiveGateway id="gw" default="flowFallback" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <manualTask id="branch{index}" name="分支{index}" />""");
        }

        body.AppendLine("""  <manualTask id="fallback" name="默认分支" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />""");
        body.AppendLine("""  <sequenceFlow id="f2" sourceRef="apply" targetRef="gw" />""");

        for (var index = 1; index <= branches; index++)
        {
            var expression = Condition($"c{index}");

            body.AppendLine($"""  <sequenceFlow id="cond{index}" sourceRef="gw" targetRef="branch{index}">""");
            body.AppendLine($"    {expression}");
            body.AppendLine("""  </sequenceFlow>""");
            body.AppendLine($"""  <sequenceFlow id="out{index}" sourceRef="branch{index}" targetRef="end" />""");
        }

        body.AppendLine("""  <sequenceFlow id="flowFallback" sourceRef="gw" targetRef="fallback" />""");
        body.AppendLine("""  <sequenceFlow id="fallbackOut" sourceRef="fallback" targetRef="end" />""");

        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var index = 1; index <= branches; index++)
        {
            variables[$"c{index}"] = false;
        }

        return new CaseSpec(
            $"condition/all-false-uses-default-{branches}",
            $"{branches} 条条件全为假 → 必须走默认流（排他网关语义）",
            Process($"all-false-default-{branches}", body.ToString().TrimEnd()),
            [Complete("applicant")],
            variables);
    }

    /// <summary>排他网关：多条条件同时为真时，取<b>第一条</b>为真的流。</summary>
    private static CaseSpec ExclusiveFirstTrueWins(int branches)
    {
        var body = new StringBuilder();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <userTask id="apply" name="申请" activiti:assignee="applicant" />""");
        body.AppendLine("""  <exclusiveGateway id="gw" default="flowFallback" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <manualTask id="branch{index}" name="分支{index}" />""");
        }

        body.AppendLine("""  <manualTask id="fallback" name="默认分支" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />""");
        body.AppendLine("""  <sequenceFlow id="f2" sourceRef="apply" targetRef="gw" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <sequenceFlow id="cond{index}" sourceRef="gw" targetRef="branch{index}">""");
            body.AppendLine($"    {Condition($"c{index}")}");
            body.AppendLine("""  </sequenceFlow>""");
            body.AppendLine($"""  <sequenceFlow id="out{index}" sourceRef="branch{index}" targetRef="end" />""");
        }

        body.AppendLine("""  <sequenceFlow id="flowFallback" sourceRef="gw" targetRef="fallback" />""");
        body.AppendLine("""  <sequenceFlow id="fallbackOut" sourceRef="fallback" targetRef="end" />""");

        // 全部为真 ⇒ 只能走 cond1（"取第一个条件为真的流"，风险 R2）
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var index = 1; index <= branches; index++)
        {
            variables[$"c{index}"] = true;
        }

        return new CaseSpec(
            $"condition/first-true-wins-{branches}",
            $"{branches} 条条件同时为真 → 必须只走第一条（风险 R2 的核心语义）",
            Process($"first-true-wins-{branches}", body.ToString().TrimEnd()),
            [Complete("applicant")],
            variables);
    }

    /// <summary>嵌套结构：并行分支内部再放排他网关（组合覆盖）。</summary>
    /// <remarks>
    /// ⚠️ 排他网关的各条分支必须先在 <c>aMerge</c> 汇合，再进并行 <c>join</c> —— 否则
    /// 并行 Join 的"期望入流数"（图上的入流条数）会大于"运行期可能到达数"（排他只走一条），
    /// 形成<b>必然死锁</b>的图。这正是 §9.3 把"网关组合"列为必建语料的价值：这类图靠肉眼很容易漏掉。
    /// </remarks>
    private static CaseSpec NestedParallelThenExclusive(int branches)
    {
        var body = new StringBuilder();
        var actions = new List<object>();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <parallelGateway id="fork" />""");
        body.AppendLine("""  <exclusiveGateway id="gwA" default="flowAFallback" />""");
        body.AppendLine("""  <manualTask id="aFallback" name="A 默认" />""");
        body.AppendLine("""  <manualTask id="aMerge" name="A 汇合" />""");
        body.AppendLine("""  <userTask id="b1" name="B 分支" activiti:assignee="b1user" />""");
        body.AppendLine("""  <parallelGateway id="join" />""");
        body.AppendLine("""  <userTask id="archive" name="归档" activiti:assignee="archivist" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f0" sourceRef="start" targetRef="fork" />""");
        body.AppendLine("""  <sequenceFlow id="fA" sourceRef="fork" targetRef="gwA" />""");
        body.AppendLine("""  <sequenceFlow id="fB" sourceRef="fork" targetRef="b1" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <manualTask id="a{index}" name="A{index}" />""");
            body.AppendLine($"""  <sequenceFlow id="condA{index}" sourceRef="gwA" targetRef="a{index}">""");
            body.AppendLine($"    {Condition($"a{index}")}");
            body.AppendLine("""  </sequenceFlow>""");
            body.AppendLine($"""  <sequenceFlow id="mergeA{index}" sourceRef="a{index}" targetRef="aMerge" />""");
        }

        body.AppendLine("""  <sequenceFlow id="flowAFallback" sourceRef="gwA" targetRef="aFallback" />""");
        body.AppendLine("""  <sequenceFlow id="mergeFallback" sourceRef="aFallback" targetRef="aMerge" />""");
        body.AppendLine("""  <sequenceFlow id="joinA" sourceRef="aMerge" targetRef="join" />""");
        body.AppendLine("""  <sequenceFlow id="joinB" sourceRef="b1" targetRef="join" />""");
        body.AppendLine("""  <sequenceFlow id="fArchive" sourceRef="join" targetRef="archive" />""");
        body.AppendLine("""  <sequenceFlow id="fEnd" sourceRef="archive" targetRef="end" />""");

        actions.Add(Complete("b1user"));
        actions.Add(Complete("archivist"));

        // A 分支条件全假 ⇒ 走 aFallback ⇒ 汇合后与 b1 一起到达 join
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);

        for (var index = 1; index <= branches; index++)
        {
            variables[$"a{index}"] = false;
        }

        return new CaseSpec(
            $"gateway/nested-parallel-then-exclusive-{branches}",
            "并行分叉内嵌排他网关（排他分支先合流再进 Join）：Join 的两条入流分别来自 A 汇合点与 B 分支",
            Process($"nested-parallel-exclusive-{branches}", body.ToString().TrimEnd()),
            actions,
            variables);
    }

    /// <summary>阻抗用例：只完成部分分支 ⇒ Join 不达成，后续节点绝不能出现。</summary>
    private static CaseSpec PartialJoinImpedance(int branches)
    {
        var body = new StringBuilder();
        var actions = new List<object>();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <parallelGateway id="fork" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <userTask id="sign{index}" name="会签{index}" activiti:assignee="u{index}" />""");
        }

        body.AppendLine("""  <parallelGateway id="join" />""");
        body.AppendLine("""  <userTask id="archive" name="归档" activiti:assignee="archivist" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f0" sourceRef="start" targetRef="fork" />""");

        for (var index = 1; index <= branches; index++)
        {
            body.AppendLine($"""  <sequenceFlow id="fork{index}" sourceRef="fork" targetRef="sign{index}" />""");
            body.AppendLine($"""  <sequenceFlow id="join{index}" sourceRef="sign{index}" targetRef="join" />""");
        }

        body.AppendLine("""  <sequenceFlow id="fArchive" sourceRef="join" targetRef="archive" />""");
        body.AppendLine("""  <sequenceFlow id="fEnd" sourceRef="archive" targetRef="end" />""");

        // 只完成前 branches-1 个分支：Join 缺一条入流，不能放行
        for (var index = 1; index < branches; index++)
        {
            actions.Add(Complete($"u{index}"));
        }

        return new CaseSpec(
            $"impedance/partial-join-{branches}",
            $"{branches} 条分支只完成 {branches - 1} 条 ⇒ Join 不得达成，归档任务不得出现",
            Process($"partial-join-{branches}", body.ToString().TrimEnd()),
            actions);
    }

    /// <summary>阻抗用例：单入单出的并行网关（Join 期望值大于实际入流数）。</summary>
    private static CaseSpec SingleEntryParallelImpedance(int branches)
    {
        var body = new StringBuilder();

        body.AppendLine("""  <startEvent id="start" />""");
        body.AppendLine("""  <parallelGateway id="single" />""");
        body.AppendLine("""  <userTask id="after" name="之后" activiti:assignee="u1" />""");
        body.AppendLine("""  <endEvent id="end" />""");
        body.AppendLine("""  <sequenceFlow id="f1" sourceRef="start" targetRef="single" />""");
        body.AppendLine("""  <sequenceFlow id="f2" sourceRef="single" targetRef="after" />""");
        body.AppendLine("""  <sequenceFlow id="f3" sourceRef="after" targetRef="end" />""");

        return new CaseSpec(
            $"impedance/single-entry-parallel-{branches}",
            $"并行网关只有 1 条入流 1 条出流（fork/join 退化形态）",
            Process($"single-entry-parallel-{branches}", body.ToString().TrimEnd()),
            [Complete("u1")]);
    }

    // ─────────────────────────── 生成与写出 ───────────────────────────

    private static string Process(string id, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/corpus">
          <process id="{id}" name="{id}" isExecutable="true">
        {body}
          </process>
        </definitions>
        """;

    private static object Complete(string assignee) => new { kind = "completeTask", assignee };

    /// <summary>条件表达式元素（<c>${名字}</c>）。单独构造是为了避开原始字符串与插值语法的冲突。</summary>
    private static string Condition(string variableName) =>
        $"<conditionExpression xsi:type=\"tFormalExpression\">${{{variableName}}}</conditionExpression>";

    private static void Write(string corpusRoot, CaseSpec spec)
    {
        var (bpmnFile, caseFile, bpmn, caseJson) = Render(corpusRoot, spec);

        Directory.CreateDirectory(Path.GetDirectoryName(bpmnFile)!);

        File.WriteAllText(bpmnFile, bpmn);
        File.WriteAllText(caseFile, caseJson);
    }

    /// <summary>
    /// 渲染（不落盘）。行尾固定用 <c>\n</c> 而不是 <c>Environment.NewLine</c>：
    /// 语料是入库资产，Windows 生成、Linux 校验必须逐字节一致，否则 CI 会因换行符飘红。
    /// </summary>
    private static (string BpmnFile, string CaseFile, string Bpmn, string CaseJson) Render(
        string corpusRoot,
        CaseSpec spec)
    {
        // 布局：authored/<类别>/<stem>.bpmn20.xml + <stem>.case.json
        // 语料标识 = authored/<类别>/<stem>（两端一致，也是 businessKey）
        var categoryDirectory = Path.GetDirectoryName(spec.Path)?.Replace('\\', '/') ?? string.Empty;
        var stem = Path.GetFileName(spec.Path);
        var directory = Path.Combine(corpusRoot, "authored", categoryDirectory);

        var payload = new
        {
            name = stem,
            group = $"authored/{categoryDirectory}",
            description = spec.Description,
            variables = spec.Variables.Count == 0 ? null : spec.Variables,
            actions = spec.Actions,
            resourceName = $"{stem}.bpmn20.xml",
        };

        return (
            Path.Combine(directory, $"{stem}.bpmn20.xml"),
            Path.Combine(directory, $"{stem}.case.json"),
            spec.Bpmn + "\n",
            JsonSerializer.Serialize(payload, CaseJsonOptions) + "\n");
    }

    /// <summary>
    /// 漂移校验：磁盘上的语料是否与生成器一致。
    /// <para>语料是资产，手改过、或改了生成器却没重新生成，都必须让 CI 失败 —— 否则"脚本生成"
    /// 这条约定会悄悄退化回"手写语料"。</para>
    /// </summary>
    private static int Check(string corpusRoot)
    {
        var drift = new List<string>();

        foreach (var spec in BuildSpecs())
        {
            var (bpmnFile, caseFile, bpmn, caseJson) = Render(corpusRoot, spec);

            if (!Matches(bpmnFile, bpmn))
            {
                drift.Add(bpmnFile);
            }

            if (!Matches(caseFile, caseJson))
            {
                drift.Add(caseFile);
            }
        }

        if (drift.Count == 0)
        {
            Console.WriteLine($"漂移校验通过：语料与生成器一致（{BuildSpecs().Count()} 个语料）。");
            return 0;
        }

        Console.Error.WriteLine($"❌ 语料与生成器不一致（{drift.Count} 个文件），请重新生成：");

        foreach (var file in drift)
        {
            Console.Error.WriteLine("  - " + file);
        }

        return 1;
    }

    private static bool Matches(string path, string expected) =>
        File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(expected);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetBpm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <param name="Path">相对 <c>authored/</c> 的路径（如 <c>gateway/parallel-fork-join-2</c>）。</param>
    /// <param name="Description">用例意图（会写进 case.json，供差异清单给结论）。</param>
    /// <param name="Bpmn">BPMN 原文。</param>
    /// <param name="Actions">操作脚本。</param>
    /// <param name="Variables">启动变量。</param>
    private sealed record CaseSpec(
        string Path,
        string Description,
        string Bpmn,
        IReadOnlyList<object> Actions,
        IReadOnlyDictionary<string, object?>? Variables = null)
    {
        public IReadOnlyDictionary<string, object?> Variables { get; } =
            Variables ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
