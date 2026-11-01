using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 流程目录的<b>覆盖行</b>：一条流程在一个租户下的名片登记。
/// </summary>
/// <remarks>
/// <para>
/// 全部可空字段的语义统一：<see langword="null"/> 表示<b>跟随引擎 / BPMN 派生</b>，
/// 非 <see langword="null"/> 才是覆盖值。这条约定是「部署即可见」的前提——
/// 新流程不需要有人先登记就能出现在目录里。
/// </para>
/// <para>
/// 刻意<b>不</b>存 <c>deployed_version</c> / <c>definition_checksum</c>：它们是部署的产物，
/// 存进来就会有两份真源，任何一次「部署了但忘了同步」都会让调用方按过期的校验和缓存流程图。
/// 目录服务每次从 <see cref="Deployment.IProcessDefinitionStore"/> 现取。
/// </para>
/// </remarks>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key（= BPMN <c>process/@id</c>）。</param>
/// <param name="Name">展示名；为空取流程定义里的 name。</param>
/// <param name="Category">分类。</param>
/// <param name="Description">说明（允许 Markdown）。</param>
/// <param name="Icon">图标标识或 URL。</param>
/// <param name="PageUrls">业务页面地址模板（键 = <c>start</c> / <c>view</c> / <c>approve</c>）。</param>
/// <param name="EntryPoints">入口声明；为空表示由 BPMN 的 startEvent 派生。</param>
/// <param name="Status">显式状态；为空表示跟随引擎部署态。</param>
/// <param name="SortOrder">展示排序（小的在前）。</param>
/// <param name="Labels">本地化标签（语言 → 展示名）。</param>
/// <param name="ParameterSchemaVersion">参数规范版本；0 表示未声明。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
public sealed record ProcessCatalogEntry(
    string TenantId,
    string ProcessKey,
    string? Name,
    string? Category,
    string? Description,
    string? Icon,
    IReadOnlyDictionary<string, string>? PageUrls,
    IReadOnlyList<ProcessEntryPoint>? EntryPoints,
    ProcessStatus? Status,
    int SortOrder,
    IReadOnlyDictionary<string, string>? Labels,
    int ParameterSchemaVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 流程目录覆盖行的存储（表 <c>nb_process_catalog</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 读写都在这里，但写路径只由管理面（<c>/admin/v1/processes</c>）调用：
/// 公共读路径（<c>/workflow/v1/processes</c>）永远只读，否则一个拿 `workflow:process:read`
/// 的接入方就能改别人看到的名片。
/// </para>
/// <para>
/// 表不存在时不应让目录整体失败：见 <c>ProcessCatalog</c> 的容错处理。老库可能还没跑补表脚本。
/// </para>
/// </remarks>
public interface IProcessCatalogStore
{
    /// <summary>列出某租户的全部覆盖行（一次取回：目录条数是几十量级）。</summary>
    /// <param name="tenantId">租户；<see langword="null"/> 表示不限租户（管理面用）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ProcessCatalogEntry>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>按（租户, 流程 key）查一行；不存在返回 <see langword="null"/>。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessCatalogEntry?> FindAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default);

    /// <summary>
    /// 写入覆盖行（存在则整行替换，不存在则插入）。
    /// </summary>
    /// <remarks>
    /// <b>整行替换</b>而不是逐字段合并：覆盖行的每个字段都独立地表达「覆盖 / 跟随 BPMN」，
    /// 逐字段合并会让「把某个字段改回跟随 BPMN」这件事无法表达（PATCH 语义的经典空洞）。
    /// <see cref="ProcessCatalogEntry.CreatedAt"/> 保留首次写入的值，<see cref="ProcessCatalogEntry.UpdatedAt"/>
    /// 由存储决定。
    /// </remarks>
    /// <param name="entry">覆盖行。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ProcessCatalogEntry> UpsertAsync(ProcessCatalogEntry entry, CancellationToken ct = default);

    /// <summary>删除覆盖行（之后该流程的名片全部回到 BPMN 派生）。不存在返回 <see langword="false"/>。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="processKey">流程 key。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<bool> DeleteAsync(string tenantId, string processKey, CancellationToken ct = default);
}

/// <summary>JSON 列的读写口径（键一律 camelCase，与对外契约一致）。</summary>
internal static class ProcessCatalogJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // 不缩进：这些列会被反复读写，缩进只增加体积
        WriteIndented = false,
    };

    internal static string? Write<T>(T? value) =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    internal static T? Read<T>(string? json)
        where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>
/// 数据库版流程目录覆盖表。
/// </summary>
/// <remarks>
/// 列名是下划线、属性是驼峰，Dapper 不做自动映射，因此 SQL 里逐列 <c>as</c> 别名
/// （本仓所有手工 SQL 的同一条规矩，见 <c>DapperClientRegistry</c>）。
/// </remarks>
public sealed class DapperProcessCatalogStore : IProcessCatalogStore
{
    private const string Columns =
        "tenant_id as TenantId, process_key as ProcessKey, name as Name, category as Category, " +
        "description as Description, icon as Icon, page_urls_json as PageUrlsJson, " +
        "entry_points_json as EntryPointsJson, status as StatusText, sort_order as SortOrder, " +
        "labels_json as LabelsJson, parameter_schema_version as ParameterSchemaVersion, " +
        "created_at as CreatedAt, updated_at as UpdatedAt";

    private const string InsertSql =
        "insert into nb_process_catalog "
        + "(tenant_id, process_key, name, category, description, icon, page_urls_json, entry_points_json, "
        + "status, sort_order, labels_json, parameter_schema_version, created_at, updated_at) "
        + "values (@TenantId, @ProcessKey, @Name, @Category, @Description, @Icon, @PageUrlsJson, @EntryPointsJson, "
        + "@Status, @SortOrder, @LabelsJson, @ParameterSchemaVersion, @CreatedAt, @UpdatedAt)";

    // 更新不动 created_at：它是「这条覆盖行第一次出现」的证据，不该被后续编辑改写
    private const string UpdateSql =
        "update nb_process_catalog set name = @Name, category = @Category, description = @Description, "
        + "icon = @Icon, page_urls_json = @PageUrlsJson, entry_points_json = @EntryPointsJson, "
        + "status = @Status, sort_order = @SortOrder, labels_json = @LabelsJson, "
        + "parameter_schema_version = @ParameterSchemaVersion, updated_at = @UpdatedAt "
        + "where tenant_id = @TenantId and process_key = @ProcessKey";

    private const string DeleteSql =
        "delete from nb_process_catalog where tenant_id = @tenantId and process_key = @processKey";

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperProcessCatalogStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ProcessCatalogEntry>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var sql = $"select {Columns} from nb_process_catalog";
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            sql += " where tenant_id = @tenantId";
            parameters.Add("tenantId", tenantId);
        }

        sql += " order by sort_order, process_key";

        await using var connection = _connections.CreateConnection();

        var rows = await connection
            .QueryAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct))
            .ConfigureAwait(false);

        return [.. rows.Select(ToEntry)];
    }

    /// <inheritdoc />
    public async ValueTask<ProcessCatalogEntry?> FindAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var row = await connection
            .QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                $"select {Columns} from nb_process_catalog where tenant_id = @tenantId and process_key = @processKey",
                new { tenantId, processKey },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return row is null ? null : ToEntry(row);
    }

    /// <inheritdoc />
    public async ValueTask<ProcessCatalogEntry> UpsertAsync(
        ProcessCatalogEntry entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ProcessKey);

        var now = DateTime.UtcNow;

        await using var connection = _connections.CreateConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 先读再写（同一事务内）：created_at 只在首次写入时落，之后保持不变。
        // 不用「update 影响 0 行就 insert」是因为两个方言的 upsert 语法不一样
        // （MySQL on duplicate key / SQL Server merge），而这只是一行数据，多一次 select 不值得为它分方言。
        var createdAt = await connection
            .ExecuteScalarAsync<DateTime?>(new CommandDefinition(
                "select created_at from nb_process_catalog where tenant_id = @tenantId and process_key = @processKey",
                new { tenantId = entry.TenantId, processKey = entry.ProcessKey },
                transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var parameters = new
        {
            entry.TenantId,
            entry.ProcessKey,
            entry.Name,
            entry.Category,
            entry.Description,
            entry.Icon,
            PageUrlsJson = ProcessCatalogJson.Write(entry.PageUrls),
            EntryPointsJson = ProcessCatalogJson.Write(entry.EntryPoints),
            Status = entry.Status is { } status ? ProcessStatusText.ToText(status) : null,
            entry.SortOrder,
            LabelsJson = ProcessCatalogJson.Write(entry.Labels),
            entry.ParameterSchemaVersion,
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

        return entry with { CreatedAt = PlatformTimestamps.ToOffset(parameters.CreatedAt), UpdatedAt = PlatformTimestamps.ToOffset(now) };
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        await using var connection = _connections.CreateConnection();

        var affected = await connection
            .ExecuteAsync(new CommandDefinition(
                DeleteSql,
                new { tenantId, processKey },
                cancellationToken: ct))
            .ConfigureAwait(false);

        return affected > 0;
    }

    private static ProcessCatalogEntry ToEntry(Row row) => new(
        row.TenantId,
        row.ProcessKey,
        row.Name,
        row.Category,
        row.Description,
        row.Icon,
        ProcessCatalogJson.Read<Dictionary<string, string>>(row.PageUrlsJson),
        ProcessCatalogJson.Read<List<ProcessEntryPoint>>(row.EntryPointsJson),
        ProcessStatusText.TryParse(row.StatusText, out var status) ? status : null,
        row.SortOrder,
        ProcessCatalogJson.Read<Dictionary<string, string>>(row.LabelsJson),
        row.ParameterSchemaVersion,
        ToOffset(row.CreatedAt),
        ToOffset(row.UpdatedAt));

    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string TenantId { get; set; } = string.Empty;

        public string ProcessKey { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Category { get; set; }

        public string? Description { get; set; }

        public string? Icon { get; set; }

        public string? PageUrlsJson { get; set; }

        public string? EntryPointsJson { get; set; }

        public string? StatusText { get; set; }

        public int SortOrder { get; set; }

        public string? LabelsJson { get; set; }

        public int ParameterSchemaVersion { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}

/// <summary>
/// 内存版流程目录覆盖表（无库宿主 / 单元测试）。
/// </summary>
public sealed class InMemoryProcessCatalogStore : IProcessCatalogStore
{
    private readonly ConcurrentDictionary<(string TenantId, string ProcessKey), ProcessCatalogEntry> _entries = new();

    /// <summary>构造。</summary>
    /// <param name="seed">初始覆盖行；为空表示空表（目录全部由 BPMN 派生）。</param>
    public InMemoryProcessCatalogStore(IEnumerable<ProcessCatalogEntry>? seed = null)
    {
        foreach (var entry in seed ?? [])
        {
            _entries[(entry.TenantId, entry.ProcessKey)] = entry;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProcessCatalogEntry>> ListAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<ProcessCatalogEntry> result =
        [
            .. _entries.Values
                .Where(entry => tenantId is null
                    || string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal))
                .OrderBy(entry => entry.SortOrder)
                .ThenBy(entry => entry.ProcessKey, StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public ValueTask<ProcessCatalogEntry?> FindAsync(
        string tenantId,
        string processKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return ValueTask.FromResult(_entries.GetValueOrDefault((tenantId, processKey)));
    }

    /// <summary>登记 / 覆盖一行（测试与无库宿主的一次性引导用）。</summary>
    /// <param name="entry">覆盖行。</param>
    public void Upsert(ProcessCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _entries[(entry.TenantId, entry.ProcessKey)] = entry;
    }

    /// <inheritdoc />
    public ValueTask<ProcessCatalogEntry> UpsertAsync(ProcessCatalogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ProcessKey);

        var key = (entry.TenantId, entry.ProcessKey);
        var now = DateTimeOffset.UtcNow;

        // 与 Dapper 版同口径：created_at 只在首次写入时定，updated_at 每次刷新
        var createdAt = _entries.TryGetValue(key, out var existing) ? existing.CreatedAt : now;
        var stored = entry with { CreatedAt = createdAt, UpdatedAt = now };

        _entries[key] = stored;

        return ValueTask.FromResult(stored);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string tenantId, string processKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);

        return ValueTask.FromResult(_entries.TryRemove((tenantId, processKey), out _));
    }
}

/// <summary>目录状态的文本词汇（与对外 JSON 词汇一致，不存枚举序号）。</summary>
internal static class ProcessStatusText
{
    internal static string ToText(ProcessStatus status) => status switch
    {
        ProcessStatus.Draft => "DRAFT",
        ProcessStatus.Active => "ACTIVE",
        ProcessStatus.Suspended => "SUSPENDED",
        ProcessStatus.Retired => "RETIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "未知的流程目录状态。"),
    };

    internal static bool TryParse(string? text, out ProcessStatus status)
    {
        status = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out status);
    }
}
