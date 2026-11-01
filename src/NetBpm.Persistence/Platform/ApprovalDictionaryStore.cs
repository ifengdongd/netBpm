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
/// <para>
/// 读路径给 <c>IApprovalVocabulary</c>（解析审批语义），写路径给管理面
/// （<c>PUT /admin/v1/dictionaries/{processKey}/options/{optionId}</c>）。
/// 表不存在时读调用方降级为「只用默认字典」，不阻断流程解析。
/// </para>
/// <para>
/// 🔑 <b>「流程级默认」在库里是空串，不是 NULL</b>。主键含 <c>node_id</c>，
/// 而 MySQL / SQL Server 的主键列都不允许 NULL（MySQL 会静默改成 NOT NULL，
/// SQL Server 直接建表失败），所以「不限定环节」只能落成 <c>''</c>。
/// 对外形态仍然是 <see langword="null"/>（见 <see cref="ApprovalDictionaryEntry.NodeId"/>），
/// 空串只在存储边界出现。
/// </para>
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

    /// <summary>
    /// 读取某租户某流程字典的「水位」：<c>(行数, 最近更新时间)</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 进程内缓存用它做<b>跨副本校验</b>：写路径只能失效写者自己进程的缓存，
    /// 其他副本靠这条聚合查询（走 <c>(tenant_id, process_key)</c> 主键前缀）判断缓存是否过期。
    /// </para>
    /// <para>
    /// 为什么同时看行数：只看 <c>max(updated_at)</c> 的话，删除「不是最新那行」不会改变水位，
    /// 其他副本就会继续按已删除的映射判语义（把退回算成通过）；行数配合时间戳能覆盖增 / 删 / 改。
    /// </para>
    /// </remarks>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ApprovalDictionaryRevision> GetRevisionAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default);

    /// <summary>
    /// 新增或覆盖一条映射（按「租户 + 流程 + 环节 + 选项」定位）。
    /// </summary>
    /// <remarks>
    /// 定位键与主键一致，因此重复写同一行是<b>覆盖</b>而不是新增——
    /// 字典的语义是「这个选项算什么」，同一位置不可能有两个答案。
    /// <c>created_at</c> 只有首次写入时落，之后保持不变（否则「这行是什么时候配的」查不出来）。
    /// </remarks>
    /// <param name="entry">映射（<see cref="ApprovalDictionaryEntry.NodeId"/> 为空表示流程级默认）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落库后的形态（补齐时间戳）。</returns>
    ValueTask<ApprovalDictionaryEntry> UpsertAsync(
        ApprovalDictionaryEntry entry,
        CancellationToken ct = default);

    /// <summary>删除一条映射；不存在返回 <see langword="false"/>。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="nodeId">环节 id；为空表示流程级默认。</param>
    /// <param name="optionId">选项 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        string? nodeId,
        string optionId,
        CancellationToken ct = default);
}

/// <summary>
/// 字典缓存的水位：行数 + 最近更新时间。
/// </summary>
/// <param name="Count">该租户该流程的映射行数。</param>
/// <param name="UpdatedAt">最近一次写入时间；没有行时为 <see cref="DateTime.MinValue"/>。</param>
public readonly record struct ApprovalDictionaryRevision(long Count, DateTime UpdatedAt)
{
    /// <summary>空字典的水位。</summary>
    public static ApprovalDictionaryRevision Empty { get; } = new(0, DateTime.MinValue);
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

    /// <inheritdoc />
    public async ValueTask<ApprovalDictionaryRevision> GetRevisionAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var row = await connection
            .QuerySingleAsync<RevisionRow>(new CommandDefinition(
                "select count(*) as Total, max(updated_at) as LastUpdated from nb_approval_dictionary "
                + "where tenant_id = @tenantId and process_key = @processKey",
                new { tenantId, processKey },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return new ApprovalDictionaryRevision(row.Total, row.LastUpdated ?? DateTime.MinValue);
    }

    /// <inheritdoc />
    public async ValueTask<ApprovalDictionaryEntry> UpsertAsync(
        ApprovalDictionaryEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ProcessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.OptionId);

        // 「不限定环节」落库是空串（见接口注释：主键列不能为 NULL）
        var nodeKey = entry.NodeId ?? string.Empty;

        // 列是 datetime(3)：先截到毫秒再写，返回给调用方的时间才与库里回读的一致。
        // 不截的话「写入返回 07:22:28.7946627、回读 07:22:28.795」，
        // 调用方拿两边做比较会凭空多出几微秒的差异。
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 先读再写（同一事务内）：created_at 只在首次写入时落。
        // 不用方言 upsert 语法（MySQL on duplicate key / SQL Server merge），
        // 与 nb_process_catalog 同一个理由：一行数据，多一次 select 不值得为它分方言。
        var createdAt = await connection
            .ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "select created_at from nb_approval_dictionary "
                + "where tenant_id = @tenantId and process_key = @processKey "
                + "and node_id = @nodeKey and option_id = @optionId",
                new { entry.TenantId, entry.ProcessKey, nodeKey, entry.OptionId },
                transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var parameters = new
        {
            entry.TenantId,
            entry.ProcessKey,
            NodeKey = nodeKey,
            entry.OptionId,
            Semantic = SemanticText.ToText(entry.Semantic),
            OutcomeClass = OutcomeClassText.ToText(entry.OutcomeClass),
            entry.Label,
            RequiresComment = entry.RequiresComment ? 1 : 0,
            RequiresTargetNode = entry.RequiresTargetNode ? 1 : 0,
            AllowedTargetNodesJson = ApprovalDictionaryJson.Write(entry.AllowedTargetNodes),
            AttributesJson = ApprovalDictionaryJson.Write(entry.Attributes),
            CreatedAt = createdAt ?? now,
            UpdatedAt = now,
        };

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(UpdateSql, parameters, transaction, cancellationToken: ct))
            .ConfigureAwait(false);

        if (affected == 0)
        {
            await connection
                .ExecuteAsync(new CommandDefinition(InsertSql, parameters, transaction, cancellationToken: ct))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return entry with
        {
            CreatedAt = ToOffset(parameters.CreatedAt),
            UpdatedAt = ToOffset(now),
        };
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        string? nodeId,
        string optionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                "delete from nb_approval_dictionary "
                + "where tenant_id = @tenantId and process_key = @processKey "
                + "and node_id = @nodeKey and option_id = @optionId",
                new { tenantId, processKey, nodeKey = nodeId ?? string.Empty, optionId },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return affected > 0;
    }

    private const string UpdateSql =
        "update nb_approval_dictionary set semantic = @Semantic, outcome_class = @OutcomeClass, "
        + "label = @Label, requires_comment = @RequiresComment, "
        + "requires_target_node = @RequiresTargetNode, "
        + "allowed_target_nodes_json = @AllowedTargetNodesJson, attributes_json = @AttributesJson, "
        + "updated_at = @UpdatedAt "
        + "where tenant_id = @TenantId and process_key = @ProcessKey "
        + "and node_id = @NodeKey and option_id = @OptionId";

    private const string InsertSql =
        "insert into nb_approval_dictionary "
        + "(tenant_id, process_key, node_id, option_id, semantic, outcome_class, label, "
        + "requires_comment, requires_target_node, allowed_target_nodes_json, attributes_json, "
        + "created_at, updated_at) "
        + "values (@TenantId, @ProcessKey, @NodeKey, @OptionId, @Semantic, @OutcomeClass, @Label, "
        + "@RequiresComment, @RequiresTargetNode, @AllowedTargetNodesJson, @AttributesJson, "
        + "@CreatedAt, @UpdatedAt)";

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
            // 库里的「流程级默认」是空串（主键列不能为 NULL），对外的形态是 null
            string.IsNullOrEmpty(row.NodeId) ? null : row.NodeId,
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

    /// <summary>水位聚合的查询结果（属性名与 SQL 别名一致）。</summary>
    private sealed class RevisionRow
    {
        public long Total { get; set; }

        public DateTime? LastUpdated { get; set; }
    }

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

    /// <inheritdoc />
    public ValueTask<ApprovalDictionaryRevision> GetRevisionAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        var matched = _entries.Values
            .Where(entry => string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(entry.ProcessKey, processKey, StringComparison.Ordinal))
            .ToArray();

        var revision = matched.Length == 0
            ? ApprovalDictionaryRevision.Empty
            : new ApprovalDictionaryRevision(matched.Length, matched.Max(entry => entry.UpdatedAt.UtcDateTime));

        return ValueTask.FromResult(revision);
    }

    /// <inheritdoc />
    public ValueTask<ApprovalDictionaryEntry> UpsertAsync(
        ApprovalDictionaryEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return ValueTask.FromResult(Upsert(entry));
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        string? nodeId,
        string optionId,
        CancellationToken ct = default) =>
        ValueTask.FromResult(_entries.TryRemove((tenantId, processKey, nodeId, optionId), out _));

    /// <summary>登记 / 覆盖一条映射（测试与无库宿主的一次性引导用）。</summary>
    /// <param name="entry">映射。</param>
    /// <returns>落库后的形态（<c>createdAt</c> 保留首次写入的值）。</returns>
    public ApprovalDictionaryEntry Upsert(ApprovalDictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var key = (entry.TenantId, entry.ProcessKey, entry.NodeId, entry.OptionId);

        // 与数据库版同口径：created_at 只在首次写入时落，覆盖写保留原值
        var createdAt = _entries.TryGetValue(key, out var existing) ? existing.CreatedAt : entry.CreatedAt;

        var stored = entry with { CreatedAt = createdAt };

        _entries[key] = stored;

        return stored;
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
