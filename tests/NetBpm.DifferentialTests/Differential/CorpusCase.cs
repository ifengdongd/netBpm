using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 一个对拍语料 = 一份 BPMN + 一段**操作脚本**（开发计划 §9.3）。
/// <para>
/// 语料目录约定：<c>tests/corpus/&lt;group&gt;/&lt;name&gt;.bpmn20.xml</c> +
/// 同名 <c>.case.json</c>（本文件的反序列化目标）。两端的输入必须完全一致，
/// 因此操作脚本必须是<b>声明式</b>的：由「按办理人找待办并完成」这类动作组成，
/// 不允许出现平台相关的代码。
/// </para>
/// </summary>
public sealed record CorpusCase
{
    /// <summary>语料名（默认取文件名，两端一致）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>语料分组（如 <c>authored/gateway</c>、<c>from-activiti/parallel</c>）。</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>用例意图（写清它压的是哪条语义，便于差异清单给结论）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>启动变量（两端同名同值）。</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>操作脚本，按顺序执行。</summary>
    public IReadOnlyList<CaseAction> Actions { get; init; } = [];

    /// <summary>BPMN 资源名（部署时用；两端只需一致，不影响语义）。</summary>
    public string ResourceName { get; init; } = "case.bpmn20.xml";
}

/// <summary>
/// 操作脚本中的一步。
/// <para>刻意只保留最小动作集：对拍的价值在引擎语义，不在 API 覆盖面。</para>
/// </summary>
public sealed record CaseAction
{
    /// <summary>动作类型：<c>completeTask</c> / <c>completeAllTasks</c>。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>按办理人定位待办。</summary>
    public string? Assignee { get; init; }

    /// <summary>按 BPMN 元素 id（<c>TASK_DEF_KEY_</c>）定位待办；与 <see cref="Assignee"/> 取交集。</summary>
    public string? TaskDefinitionKey { get; init; }

    /// <summary>完成时写入的变量。</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// <c>completeAllTasks</c> 时最多完成几个（默认不限）。
    /// <para>用于"并行分支只完成一部分"这种阻抗用例。</para>
    /// </summary>
    public int? Limit { get; init; }
}

/// <summary>语料仓储：从磁盘加载语料。</summary>
public static class CorpusRepository
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>枚举语料根目录下的全部语料（<c>*.case.json</c> 为主索引）。</summary>
    public static IReadOnlyList<CorpusEntry> Load(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);

        if (!Directory.Exists(corpusRoot))
        {
            return [];
        }

        var entries = new List<CorpusEntry>();

        foreach (var caseFile in Directory.EnumerateFiles(corpusRoot, "*.case.json", SearchOption.AllDirectories))
        {
            var caseDirectory = Path.GetDirectoryName(caseFile)!;
            var stem = Path.GetFileName(caseFile)[..^".case.json".Length];
            var bpmnFile = Path.Combine(caseDirectory, $"{stem}.bpmn20.xml");

            if (!File.Exists(bpmnFile))
            {
                throw new InvalidOperationException($"语料 '{caseFile}' 缺少同名 BPMN 文件：{bpmnFile}");
            }

            var parsed = JsonSerializer.Deserialize<CorpusCase>(File.ReadAllText(caseFile), JsonOptions)
                ?? throw new InvalidOperationException($"语料 '{caseFile}' 反序列化失败。");

            // 分组 = 相对语料根的目录名（用 '/' 归一，便于跨平台对拍）
            var group = Path.GetRelativePath(corpusRoot, caseDirectory).Replace('\\', '/');
            group = group == "." ? string.Empty : group;

            entries.Add(new CorpusEntry(
                string.IsNullOrEmpty(parsed.Name) ? stem : parsed.Name,
                stem,
                group,
                bpmnFile,
                caseFile,
                parsed));
        }

        return [.. entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal)];
    }
}

/// <summary>一条语料（文件位置 + 内容）。</summary>
/// <param name="Name">语料名。</param>
/// <param name="Stem">文件主干名（<c>&lt;stem&gt;.case.json</c> / <c>&lt;stem&gt;.bpmn20.xml</c>）。</param>
/// <param name="Group">分组（相对语料根的目录）。</param>
/// <param name="BpmnFile">BPMN 文件绝对路径。</param>
/// <param name="CaseFile">case.json 绝对路径。</param>
/// <param name="Case">语料内容。</param>
public sealed record CorpusEntry(
    string Name,
    string Stem,
    string Group,
    string BpmnFile,
    string CaseFile,
    CorpusCase Case)
{
    /// <summary>两端共用的语料标识（如 <c>authored/gateway/nested-fork-join</c>）。</summary>
    public string FullName => string.IsNullOrEmpty(Group) ? Name : $"{Group}/{Name}";

    /// <summary>
    /// 预言机快照路径（与语料同目录的 <c>&lt;stem&gt;.oracle.json</c>）。
    /// <para>Java 预言机写它、对拍执行器读它 —— 同一份文件即"真值"。</para>
    /// </summary>
    public string OracleFile => Path.Combine(Path.GetDirectoryName(CaseFile)!, $"{Stem}.oracle.json");

    /// <summary>读 BPMN 原文。</summary>
    public string ReadBpmn() => File.ReadAllText(BpmnFile);
}

/// <summary>
/// 语料里 JSON 写的变量 → CLR 基本类型。
/// <para>
/// ⚠️ 必须有这一步：System.Text.Json 把 <c>object?</c> 反序列化成 <see cref="System.Text.Json.JsonElement"/>，
/// 直接交给引擎会因"不支持的变量类型"失败（与 REST 层踩过的是同一个坑，见 §15.5 R1）。
/// </para>
/// </summary>
public static class CorpusVariables
{
    public static Dictionary<string, object?>? ToClr(IReadOnlyDictionary<string, object?>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(variables.Count, StringComparer.Ordinal);

        foreach (var (name, value) in variables)
        {
            result[name] = Convert(value);
        }

        return result;
    }

    private static object? Convert(object? value) => value switch
    {
        System.Text.Json.JsonElement element => FromJson(element),
        _ => value,
    };

    private static object? FromJson(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
        _ => throw new InvalidOperationException(
            $"语料变量只支持标量（string / number / boolean / null），收到 {element.ValueKind}。"),
    };
}
