using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 审批语义字典里的一条映射（表 <c>nb_approval_dictionary</c> 的一行）。
/// </summary>
/// <remarks>
/// <para>
/// 三码模型的落库形态：<see cref="OptionId"/>（流程定义拥有，<c>activiti:value/@id</c>）→
/// <see cref="Semantic"/>（平台稳定）→ <see cref="OutcomeClass"/>（由语义推导的统计口径）。
/// <see cref="NodeId"/> 为空表示<b>流程级默认</b>，非空表示节点级覆盖。
/// </para>
/// <para>
/// 本表只存<b>流程自定义</b>选项的语义声明。数字 1/2/3 与常见英文词的映射由
/// <c>ApprovalSemantics</c> 默认字典覆盖，<b>不重复落库</b>——否则会出现两份真源，
/// 一份改了另一份没改就是「把退回当成通过」的静默错误。
/// </para>
/// </remarks>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key（= BPMN <c>process/@id</c>）。</param>
/// <param name="NodeId">环节 id（BPMN <c>userTask/@id</c>）；为空表示流程级默认。</param>
/// <param name="OptionId">选项 id（<c>activiti:value/@id</c>）。</param>
/// <param name="Semantic">平台语义。</param>
/// <param name="OutcomeClass">统计口径（与语义推导一致才落非空）。</param>
/// <param name="Label">按钮文案；为空取 BPMN <c>activiti:value/@name</c>。</param>
/// <param name="RequiresComment">是否必须填写审批意见。</param>
/// <param name="RequiresTargetNode">是否必须指定目标节点。</param>
/// <param name="AllowedTargetNodes">可选目标节点白名单；为空表示按流程定义默认。</param>
/// <param name="Attributes">流程自定义附加属性。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
public sealed record ApprovalDictionaryEntry(
    string TenantId,
    string ProcessKey,
    string? NodeId,
    string OptionId,
    ApprovalSemantic Semantic,
    ApprovalOutcomeClass OutcomeClass,
    string? Label,
    bool RequiresComment,
    bool RequiresTargetNode,
    IReadOnlyList<string>? AllowedTargetNodes,
    IReadOnlyDictionary<string, string>? Attributes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 审批语义字典的存储（表 <c>nb_approval_dictionary</c>）。
/// </summary>
/// <remarks>
/// 只读接口：字典的<b>写</b>路径属于管理面（<c>/admin/v1/dictionaries</c>），
/// 等管理面落地时再加 <c>UpsertAsync</c>。表不存在时调用方（<c>IApprovalVocabulary</c>）
/// 降级为「只用默认字典」，不阻断流程解析。
/// </remarks>
public interface IApprovalDictionaryStore
{
    /// <summary>列出某租户某流程的全部映射（一次取回：选项条数是几十量级）。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ApprovalDictionaryEntry>> ListAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default);
}

/// <summary>JSON 列的读写口径（键一律 camelCase）。</summary>
internal static class ApprovalDictionaryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal static string? Write<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    internal static T? Read<T>(string? json)
        where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// 数据库版审批语义字典。
/// </summary>
/// <remarks>
/// 列名下划线、属性驼峰，Dapper 不自动映射，SQL 里逐列 <c>as</c> 别名（本仓规矩）。
/// </remarks>
public sealed class DapperApprovalDictionaryStore : IApprovalDictionaryStore
{
    private const string Columns =
        "tenant_id as TenantId, process_key as ProcessKey, node_id as NodeId, option_id as OptionId, " +
        "semantic as SemanticText, outcome_class as OutcomeClassText, label as Label, " +
        "requires_comment as RequiresComment, requires_target_node as RequiresTargetNode, " +
        "allowed_target_nodes_json as AllowedTargetNodesJson, attributes_json as AttributesJson, " +
        "created_at as CreatedAt, updated_at as UpdatedAt";

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperApprovalDictionaryStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ApprovalDictionaryEntry>> ListAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_approval_dictionary "
                + "where tenant_id = @tenantId and process_key = @processKey "
                + "order by node_id, option_id",
                new { tenantId, processKey },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(ToEntry)];
    }

    private static ApprovalDictionaryEntry ToEntry(Row row)
    {
        var semantic = SemanticText.TryParse(row.SemanticText, out var parsed)
            ? parsed
            : ApprovalSemantic.Custom;

        var outcomeClass = OutcomeClassText.TryParse(row.OutcomeClassText, out var parsedOutcome)
            ? parsedOutcome
            : ApprovalSemantics.OutcomeClassOf(semantic);

        return new ApprovalDictionaryEntry(
            row.TenantId,
            row.ProcessKey,
            row.NodeId,
            row.OptionId,
            semantic,
            outcomeClass,
            row.Label,
            row.RequiresComment != 0,
            row.RequiresTargetNode != 0,
            ApprovalDictionaryJson.Read<List<string>>(row.AllowedTargetNodesJson),
            ApprovalDictionaryJson.Read<Dictionary<string, string>>(row.AttributesJson),
            ToOffset(row.CreatedAt),
            ToOffset(row.UpdatedAt));
    }

    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string TenantId { get; set; } = string.Empty;

        public string ProcessKey { get; set; } = string.Empty;

        public string? NodeId { get; set; }

        public string OptionId { get; set; } = string.Empty;

        public string SemanticText { get; set; } = string.Empty;

        public string OutcomeClassText { get; set; } = string.Empty;

        public string? Label { get; set; }

        public int RequiresComment { get; set; }

        public int RequiresTargetNode { get; set; }

        public string? AllowedTargetNodesJson { get; set; }

        public string? AttributesJson { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>
/// 内存版审批语义字典（无库宿主 / 单元测试）。
/// </summary>
public sealed class InMemoryApprovalDictionaryStore : IApprovalDictionaryStore
{
    private readonly ConcurrentDictionary<(string TenantId, string ProcessKey, string? NodeId, string OptionId), ApprovalDictionaryEntry> _entries = new();

    /// <summary>构造。</summary>
    /// <param name="seed">初始映射。</param>
    public InMemoryApprovalDictionaryStore(IEnumerable<ApprovalDictionaryEntry>? seed = null)
    {
        foreach (var entry in seed ?? [])
        {
            _entries[(entry.TenantId, entry.ProcessKey, entry.NodeId, entry.OptionId)] = entry;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ApprovalDictionaryEntry>> ListAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        IReadOnlyList<ApprovalDictionaryEntry> result =
        [
            .. _entries.Values
                .Where(entry => string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal)
                    && string.Equals(entry.ProcessKey, processKey, StringComparison.Ordinal))
                .OrderBy(entry => entry.NodeId, StringComparer.Ordinal)
                .ThenBy(entry => entry.OptionId, StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(result);
    }

    /// <summary>登记 / 覆盖一条映射（测试与无库宿主的一次性引导用）。</summary>
    /// <param name="entry">映射。</param>
    public void Upsert(ApprovalDictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries[(entry.TenantId, entry.ProcessKey, entry.NodeId, entry.OptionId)] = entry;
    }
}

/// <summary>审批语义的文本词汇（与对外 JSON 词汇一致，不存枚举序号）。</summary>
internal static class SemanticText
{
    internal static string ToText(ApprovalSemantic semantic) => semantic switch
    {
        ApprovalSemantic.Approve => "APPROVE",
        ApprovalSemantic.Reject => "REJECT",
        ApprovalSemantic.Return => "RETURN",
        ApprovalSemantic.Delegate => "DELEGATE",
        ApprovalSemantic.Custom => "CUSTOM",
        ApprovalSemantic.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(semantic), semantic, "未知的审批语义。"),
    };

    internal static bool TryParse(string? text, out ApprovalSemantic semantic)
    {
        semantic = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out semantic);
    }
}

/// <summary>审批统计口径的文本词汇。</summary>
internal static class OutcomeClassText
{
    internal static string ToText(ApprovalOutcomeClass outcomeClass) => outcomeClass switch
    {
        ApprovalOutcomeClass.Positive => "POSITIVE",
        ApprovalOutcomeClass.Negative => "NEGATIVE",
        ApprovalOutcomeClass.Neutral => "NEUTRAL",
        _ => throw new ArgumentOutOfRangeException(nameof(outcomeClass), outcomeClass, "未知的统计口径。"),
    };

    internal static bool TryParse(string? text, out ApprovalOutcomeClass outcomeClass)
    {
        outcomeClass = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out outcomeClass);
    }
}
