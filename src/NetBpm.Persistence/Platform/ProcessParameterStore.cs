using System.Text.Json;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 流程参数规范索引的存储（表 <c>nb_process_parameter</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>真源是 BPMN</b>：这张表存的是派生结果的物化，不是权威数据。因此接口只有
/// 「整组替换」与「按定义版本读」两块——没有 update 单行，因为参数规范的变更天然是整组的
/// （删一个参数、改一个类型都会让整份 schema 的版本语义变掉）。
/// </para>
/// <para>
/// 读的时候必须带 <paramref name="definitionVersion"/>：索引行记着自己是从哪一版定义派生的，
/// 与当前生效版本不一致就当作没有（重新派生 + 重建），避免拿旧版本的参数去校验新版本的单据。
/// </para>
/// </remarks>
public interface IProcessParameterStore
{
    /// <summary>
    /// 读某流程某定义版本的参数规范；索引缺失或版本不一致时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="definitionVersion">当前生效的定义版本（索引行的版本必须与它一致才可用）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ParameterSchema?> FindAsync(
        string tenantId,
        string processKey,
        int definitionVersion,
        CancellationToken ct = default);

    /// <summary>
    /// 用一份参数规范替换某流程<b>某一版定义</b>的索引行，返回写入行数。
    /// </summary>
    /// <remarks>
    /// 「先删该版本的旧行，再插新行」在<b>同一事务</b>里完成：中途失败只会回到旧索引，
    /// 不会出现「删了旧的、新的没插进去」导致参数校验突然全线放行。
    /// 其它版本的行<b>不删</b>：它们解释的是还停在那些版本上的历史实例。
    /// </remarks>
    /// <param name="tenantId">租户。</param>
    /// <param name="definitionVersion">这份规范派生自哪一版定义。</param>
    /// <param name="schema">参数规范（<see cref="ParameterSchema.Parameters"/> 为空时只删不插）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<int> ReplaceAsync(
        string tenantId,
        int definitionVersion,
        ParameterSchema schema,
        CancellationToken ct = default);
}

/// <summary>参数索引里 JSON 列的口径（键一律 camelCase，与对外契约一致）。</summary>
internal static class ProcessParameterJson
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

/// <summary>数据库版参数规范索引。</summary>
/// <remarks>
/// 列名下划线、属性驼峰，Dapper 不自动映射，SQL 里逐列 <c>as</c> 别名（本仓规矩）。
/// </remarks>
public sealed class DapperProcessParameterStore : IProcessParameterStore
{
    private const string Columns =
        "api_name as ApiName, variable_name as VariableName, display_name as DisplayName, "
        + "type as TypeText, required as Required, default_value as DefaultValue, "
        + "description as Description, writable_by_json as WritableByJson, "
        + "visible_to_json as VisibleToJson, rules_json as RulesJson, "
        + "sort_order as SortOrder, schema_version as SchemaVersion";

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperProcessParameterStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<ParameterSchema?> FindAsync(
        string tenantId,
        string processKey,
        int definitionVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_process_parameter "
                + "where tenant_id = @tenantId and process_key = @processKey "
                + "and definition_version = @definitionVersion order by sort_order asc, api_name asc",
                new { tenantId, processKey, definitionVersion },
                cancellationToken: ct))
            .ConfigureAwait(false);

        var list = rows.AsList();

        if (list.Count == 0)
        {
            return null;
        }

        return new ParameterSchema(
            processKey,
            list[0].SchemaVersion,
            [.. list.Select(ToDefinition)],
            ReservedParameterPrefixes);
    }

    /// <inheritdoc />
    public async ValueTask<int> ReplaceAsync(
        string tenantId,
        int definitionVersion,
        ParameterSchema schema,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema.ProcessKey);

        await using var connection = _connections.CreateConnection();

        // 连接由工厂打开（见 DbConnectionFactory.CreateConnection）
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_process_parameter "
            + "where tenant_id = @tenantId and process_key = @processKey and definition_version = @definitionVersion",
            new { tenantId, processKey = schema.ProcessKey, definitionVersion },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var updatedAt = DateTime.UtcNow;
        var written = 0;

        foreach (var definition in schema.Parameters)
        {
            written += await connection.ExecuteAsync(new CommandDefinition(
                "insert into nb_process_parameter "
                + "(tenant_id, process_key, api_name, variable_name, display_name, type, required, "
                + "default_value, description, writable_by_json, visible_to_json, rules_json, "
                + "sort_order, schema_version, definition_version, updated_at) "
                + "values (@tenantId, @processKey, @apiName, @variableName, @displayName, @type, @required, "
                + "@defaultValue, @description, @writableBy, @visibleTo, @rules, "
                + "@sortOrder, @schemaVersion, @definitionVersion, @updatedAt)",
                new
                {
                    tenantId,
                    processKey = schema.ProcessKey,
                    apiName = definition.ApiName,
                    variableName = definition.VariableName,
                    displayName = definition.DisplayName,
                    type = definition.Type.ToString().ToUpperInvariant(),
                    // MySQL 的 tinyint(1) 与 SQL Server 的 bit 都不接受 bool 以外的表达，
                    // 统一成 0/1 让两个方言走同一条 SQL
                    required = definition.Required ? 1 : 0,
                    defaultValue = Truncate(definition.DefaultValue, 1000),
                    description = Truncate(definition.Description, 1000),
                    writableBy = ProcessParameterJson.Write(definition.WritableBy),
                    visibleTo = ProcessParameterJson.Write(definition.VisibleTo),
                    rules = ProcessParameterJson.Write(definition.Constraints),
                    sortOrder = definition.SortOrder,
                    schemaVersion = schema.Version,
                    definitionVersion,
                    updatedAt,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return written;
    }

    private static ParameterDefinition ToDefinition(Row row) =>
        new(
            ApiName: row.ApiName,
            VariableName: row.VariableName,
            DisplayName: row.DisplayName,
            Type: Enum.TryParse<ParameterType>(row.TypeText, ignoreCase: true, out var type)
                ? type
                : ParameterType.String,
            Required: row.Required != 0,
            DefaultValue: row.DefaultValue,
            Description: row.Description,
            Constraints: ProcessParameterJson.Read<ParameterConstraints>(row.RulesJson),
            WritableBy: ProcessParameterJson.Read<List<string>>(row.WritableByJson),
            VisibleTo: ProcessParameterJson.Read<List<string>>(row.VisibleToJson),
            SortOrder: row.SortOrder);

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } text && text.Length > maxLength ? text[..maxLength] : value;

    /// <summary>平台保留的命名空间前缀（与目录派生同口径）。</summary>
    private static readonly IReadOnlyList<string> ReservedParameterPrefixes = ["param.", "form."];

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string ApiName { get; set; } = string.Empty;

        public string? VariableName { get; set; }

        public string? DisplayName { get; set; }

        public string TypeText { get; set; } = "STRING";

        public int Required { get; set; }

        public string? DefaultValue { get; set; }

        public string? Description { get; set; }

        public string? WritableByJson { get; set; }

        public string? VisibleToJson { get; set; }

        public string? RulesJson { get; set; }

        public int SortOrder { get; set; }

        public int SchemaVersion { get; set; }
    }
}

/// <summary>
/// 内存版参数规范索引（无库宿主 / 单元测试）。
/// </summary>
public sealed class InMemoryProcessParameterStore : IProcessParameterStore
{
    private readonly Dictionary<(string Tenant, string ProcessKey, int Version), ParameterSchema> _index = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<ParameterSchema?> FindAsync(
        string tenantId,
        string processKey,
        int definitionVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        lock (_gate)
        {
            return ValueTask.FromResult(
                _index.TryGetValue((tenantId, processKey, definitionVersion), out var schema)
                    ? schema
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> ReplaceAsync(
        string tenantId,
        int definitionVersion,
        ParameterSchema schema,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(schema);

        lock (_gate)
        {
            _index[(tenantId, schema.ProcessKey, definitionVersion)] = schema;

            return ValueTask.FromResult(schema.Parameters.Count);
        }
    }
}
