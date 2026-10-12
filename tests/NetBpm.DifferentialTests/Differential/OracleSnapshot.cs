using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetBpm.DifferentialTests.Differential;

/// <summary>
/// 一份三维度快照（开发计划 §9.2）。<b>两端共用同一份 JSON 契约</b>：
/// Java 预言机写、本引擎读（或反过来做自洽性检查）。
/// <para>
/// ① <see cref="Api"/>：规范化后的 API 视图（不看各自的 DTO 形状，只看语义）；
/// ② <see cref="Tables"/>：<c>ACT_*</c> 行快照（表结构相同，见 §7.1）；
/// ③ <see cref="Events"/>：动作序列（<c>ACTIVITY_*</c> / <c>SEQUENCEFLOW_TAKEN</c>…）。
/// </para>
/// </summary>
public sealed record OracleSnapshot
{
    /// <summary>语料标识（<c>group/name</c>）。</summary>
    public string CaseName { get; init; } = string.Empty;

    /// <summary>维度①：API 返回值视图。</summary>
    public ApiView Api { get; init; } = new();

    /// <summary>维度②：表名 → 行（行内为「列名 → 字符串值」，<see langword="null"/> 表示 NULL）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Tables { get; init; } =
        new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>维度③：事件序列。</summary>
    public IReadOnlyList<TraceEvent> Events { get; init; } = [];
}

/// <summary>
/// 维度①的<b>规范化视图</b>：两端各自从自己的 API/查询里产出同一形状。
/// <para>
/// 为什么不直接比 API 返回值：Java 与 C# 的 DTO 形状天然不同（枚举、时间类型、字段命名），
/// 直接比会淹没在"结构性差异"里。§9.4 要求"结构性差异 100% 消除"，因此先把两侧都规范化，
/// 再比语义 —— 余下的差异才是真的语义差异。
/// </para>
/// </summary>
public sealed record ApiView
{
    /// <summary>流程实例（按 businessKey 排序）。</summary>
    public IReadOnlyList<ApiProcessInstance> ProcessInstances { get; init; } = [];

    /// <summary>活跃待办（按 taskDefinitionKey + assignee 排序）。</summary>
    public IReadOnlyList<ApiTask> Tasks { get; init; } = [];

    /// <summary>流程变量（按 name 排序）。</summary>
    public IReadOnlyList<ApiVariable> Variables { get; init; } = [];
}

/// <param name="BusinessKey">业务主键（两端一致，便于对齐实例）。</param>
/// <param name="IsEnded">实例是否已结束。</param>
/// <param name="UnfinishedActivityIds">
/// 当前**未结束**的活动 id（有序去重）：运行时即"流程停在哪几个节点上"。
/// <para>
/// 定义刻意选它而不是"活动执行数"：它两端都能用<b>同一条 API 语义</b>取得
/// （C# 侧 <c>IHistoryManager.FindActivityInstancesAsync</c> 里 <c>EndTime == null</c> 的记录；
/// Java 侧 <c>HistoricActivityInstanceQuery.unfinished()</c>），
/// 而"执行行数"属于维度②的职责，放在这里会把两个维度搅在一起。
/// </para>
/// </param>
public sealed record ApiProcessInstance(
    string? BusinessKey,
    bool IsEnded,
    IReadOnlyList<string> UnfinishedActivityIds);

/// <param name="TaskDefinitionKey">BPMN 元素 id。</param>
/// <param name="Assignee">办理人。</param>
public sealed record ApiTask(string? TaskDefinitionKey, string? Assignee);

/// <param name="Name">变量名。</param>
/// <param name="Type">变量类型名（与 <c>ACT_RU_VARIABLE.TYPE_</c> 同口径）。</param>
/// <param name="Value">值的字符串表示（跨语言可比；<see langword="null"/> 表示 null 变量）。</param>
public sealed record ApiVariable(string Name, string? Type, string? Value);

/// <summary>
/// 维度③的一个事件。
/// <para>
/// <see cref="Type"/> 一律取 Activiti <c>ActivitiEventType</c> 的名字（如
/// <c>PROCESS_STARTED</c> / <c>ACTIVITY_COMPLETED</c> / <c>SEQUENCEFLOW_TAKEN</c>），
/// 这样"事件序列"这一维度才有意义 —— 否则两端各说各话。
/// </para>
/// </summary>
/// <param name="Type">事件类型（<c>ActivitiEventType</c> 名）。</param>
/// <param name="ActivityId">活动/流程元素 id（无则为 <see langword="null"/>）。</param>
/// <param name="TaskDefinitionKey">任务节点 id（任务类事件）。</param>
/// <param name="FlowId">顺序流 id（<c>SEQUENCEFLOW_TAKEN</c>）。</param>
/// <param name="VariableName">变量名（变量类事件）。</param>
public sealed record TraceEvent(
    string Type,
    string? ActivityId = null,
    string? TaskDefinitionKey = null,
    string? FlowId = null,
    string? VariableName = null);

/// <summary>快照的 JSON 读写（两端共用的契约）。</summary>
public static class OracleSnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(OracleSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static OracleSnapshot Read(string json) =>
        JsonSerializer.Deserialize<OracleSnapshot>(json, Options)
        ?? throw new InvalidOperationException("预言机快照 JSON 反序列化失败。");

    public static void WriteFile(string path, OracleSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Write(snapshot));
    }
}
