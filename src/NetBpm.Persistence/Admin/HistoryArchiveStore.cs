using System.Data.Common;
using System.Text.Json;
using Dapper;
using NetBpm.Engine;

namespace NetBpm.Persistence.Admin;

/// <summary>
/// 历史归档请求：把 <b>已结束且结束时间早于 <paramref name="Before"/></b> 的流程实例历史，
/// 从 5 张 <c>ACT_HI_*</c> 表搬运到 <c>admin_history_archive</c>。
/// </summary>
/// <param name="Before">结束时间上界（严格小于）。</param>
/// <param name="DryRun">只统计不写库（预演）。</param>
/// <param name="BatchSize">本次最多归档多少个实例（1..1000）。</param>
/// <param name="TenantId">只归档该租户（<see langword="null"/> 表示不限）。</param>
/// <param name="Operator">操作人（写入归档行）。</param>
public sealed record HistoryArchiveRequest(
    DateTime Before,
    bool DryRun = false,
    int BatchSize = 200,
    string? TenantId = null,
    string? Operator = null);

/// <summary>归档执行结果。</summary>
/// <param name="ArchivedCount">本次归档（或预演将归档）的实例数。</param>
/// <param name="RemainingCount">仍满足条件、留待下次归档的实例数。</param>
/// <param name="DryRun">是否预演。</param>
/// <param name="ProcessInstanceIds">本次涉及的实例 id。</param>
public sealed record HistoryArchiveResult(
    int ArchivedCount,
    int RemainingCount,
    bool DryRun,
    IReadOnlyList<string> ProcessInstanceIds);

/// <summary>归档摘要（列表用，不含 payload）。</summary>
/// <param name="Id">归档行 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="StartUserId">发起人。</param>
/// <param name="StartTime">实例开始时间。</param>
/// <param name="EndTime">实例结束时间。</param>
/// <param name="ArchivedAt">归档时间（UTC）。</param>
/// <param name="ArchivedBy">归档操作人。</param>
public sealed record HistoryArchiveSummary(
    string Id,
    string ProcessInstanceId,
    string? TenantId,
    string? ProcessDefinitionId,
    string? BusinessKey,
    string? StartUserId,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime ArchivedAt,
    string? ArchivedBy);

/// <summary>归档分页结果。</summary>
/// <param name="Items">当前页。</param>
/// <param name="Total">总数。</param>
public sealed record HistoryArchivePage(IReadOnlyList<HistoryArchiveSummary> Items, int Total);

/// <summary>归档详情（含完整 JSON 快照）。</summary>
/// <param name="Summary">摘要。</param>
/// <param name="Payload">历史行快照（JSON）。</param>
public sealed record HistoryArchiveDetail(HistoryArchiveSummary Summary, string Payload);

/// <summary>归档查询条件。</summary>
/// <param name="TenantId">租户。</param>
/// <param name="BusinessKey">业务主键（精确匹配）。</param>
/// <param name="ProcessInstanceId">流程实例 id（精确匹配）。</param>
/// <param name="ArchivedAfter">归档时间下界（含）。</param>
/// <param name="FirstResult">跳过条数。</param>
/// <param name="MaxResults">页大小。</param>
public sealed record HistoryArchiveQuery(
    string? TenantId = null,
    string? BusinessKey = null,
    string? ProcessInstanceId = null,
    DateTime? ArchivedAfter = null,
    int FirstResult = 0,
    int MaxResults = 20);

/// <summary>
/// 历史归档存储：把运行库中的历史行搬进归档表并删除原行，
/// 使 <c>ACT_HI_*</c> 保持轻量，同时保留可检索的完整快照。
/// </summary>
public interface IHistoryArchiveStore
{
    /// <summary>执行（或预演）归档。</summary>
    ValueTask<HistoryArchiveResult> ArchiveAsync(HistoryArchiveRequest request, CancellationToken ct = default);

    /// <summary>分页查询归档记录。</summary>
    ValueTask<HistoryArchivePage> QueryAsync(HistoryArchiveQuery query, CancellationToken ct = default);

    /// <summary>按流程实例 id 取归档详情（含 payload）；不存在返回 <see langword="null"/>。</summary>
    ValueTask<HistoryArchiveDetail?> FindAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>保留策略：删除归档时间早于 <paramref name="before"/> 的记录，返回删除条数。</summary>
    ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default);
}

/// <summary>
/// 内存宿主的历史归档实现：明确返回 501（内存宿主没有持久化历史可归档）。
/// </summary>
public sealed class UnsupportedHistoryArchiveStore : IHistoryArchiveStore
{
    /// <inheritdoc />
    public ValueTask<HistoryArchiveResult> ArchiveAsync(
        HistoryArchiveRequest request,
        CancellationToken ct = default) => throw NotSupported();

    /// <inheritdoc />
    public ValueTask<HistoryArchivePage> QueryAsync(
        HistoryArchiveQuery query,
        CancellationToken ct = default) => throw NotSupported();

    /// <inheritdoc />
    public ValueTask<HistoryArchiveDetail?> FindAsync(
        string processInstanceId,
        CancellationToken ct = default) => throw NotSupported();

    /// <inheritdoc />
    public ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default) => ValueTask.FromResult(0);

    private static EngineNotImplementedException NotSupported() => new(
        "历史归档",
        "P9",
        "内存宿主不持久化历史（ACT_HI_*），归档只在数据库宿主可用。");
}

/// <summary>
/// 数据库历史归档实现。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// </summary>
/// <remarks>
/// 分批事务：每 <see cref="ArchiveChunkSize"/> 个实例一个事务，快照写入与删除原行同进同退；
/// 单条历史巨大时不会把整个批次锁在同一事务里（此前的实现是每个实例一个事务，
/// 1000 个实例 = 1000 次提交）。归档行对 <c>process_instance_id</c> 建唯一键，重复归档幂等跳过。
/// </remarks>
public sealed class DapperHistoryArchiveStore : IHistoryArchiveStore
{
    private const int MaxBatchSize = 1000;

    /// <summary>一个归档事务覆盖的实例数。</summary>
    private const int ArchiveChunkSize = 50;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDbConnectionFactory _connections;

    public DapperHistoryArchiveStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<HistoryArchiveResult> ArchiveAsync(
        HistoryArchiveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var take = Math.Clamp(request.BatchSize, 1, MaxBatchSize);

        await using var connection = _connections.CreateConnection();

        var candidates = await FindCandidatesAsync(
                connection,
                request,
                take + 1,
                _connections.DatabaseType,
                ct)
            .ConfigureAwait(false);
        var selected = candidates.Take(take).ToArray();

        if (request.DryRun)
        {
            var pending = await CountCandidatesAsync(connection, request, ct).ConfigureAwait(false);

            return new HistoryArchiveResult(
                selected.Length,
                Math.Max(0, pending - selected.Length),
                true,
                [.. selected.Select(candidate => candidate.ProcessInstanceId)]);
        }

        if (selected.Length == 0)
        {
            return new HistoryArchiveResult(0, 0, false, []);
        }

        var archived = new List<string>(selected.Length);

        // 每 ArchiveChunkSize 个实例一个事务：既不是「一个实例一次提交」，也不是「整批一个大事务」
        for (var offset = 0; offset < selected.Length; offset += ArchiveChunkSize)
        {
            var end = Math.Min(offset + ArchiveChunkSize, selected.Length);

            await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            for (var index = offset; index < end; index++)
            {
                var candidate = selected[index];

                await ArchiveOneAsync(connection, transaction, candidate, request.Operator, ct).ConfigureAwait(false);
                archived.Add(candidate.ProcessInstanceId);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // 归档后重新数：并发归档 / 新结束的实例都可能改变剩余量
        var remaining = await CountCandidatesAsync(connection, request, ct).ConfigureAwait(false);

        return new HistoryArchiveResult(archived.Count, Math.Max(0, remaining), false, archived);
    }

    /// <inheritdoc />
    public async ValueTask<HistoryArchivePage> QueryAsync(
        HistoryArchiveQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (where, parameters) = BuildWhere(query);

        await using var connection = _connections.CreateConnection();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from admin_history_archive{where}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        if (total == 0)
        {
            return new HistoryArchivePage([], 0);
        }

        var paging = SqlPaging.Build(_connections.DatabaseType, query.FirstResult, query.MaxResults, parameters);
        var sql = $"select {Columns} from admin_history_archive{where} order by archived_at desc, id desc{paging}";

        var rows = await connection.QueryAsync<ArchiveRow>(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return new HistoryArchivePage([.. rows.Select(Map)], total);
    }

    /// <inheritdoc />
    public async ValueTask<HistoryArchiveDetail?> FindAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        await using var connection = _connections.CreateConnection();

        var rows = await connection.QueryAsync<ArchiveRow>(new CommandDefinition(
            $"select {Columns}, payload as Payload from admin_history_archive where process_instance_id = @processInstanceId",
            new { processInstanceId },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows
            .Select(row => new HistoryArchiveDetail(Map(row), row.Payload ?? string.Empty))
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from admin_history_archive where archived_at < @before",
            new { before },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ArchiveCandidate>> FindCandidatesAsync(
        DbConnection connection,
        HistoryArchiveRequest request,
        int take,
        DatabaseType databaseType,
        CancellationToken ct)
    {
        var sql = CandidateSql(request.TenantId, databaseType, take);

        var rows = await connection.QueryAsync<ArchiveCandidate>(new CommandDefinition(
            sql,
            new { before = request.Before, tenantId = request.TenantId, take },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows];
    }

    private static async Task<int> CountCandidatesAsync(
        DbConnection connection,
        HistoryArchiveRequest request,
        CancellationToken ct)
    {
        var where = CandidateWhere(request.TenantId);

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_HI_PROCINST h{where}",
            new { before = request.Before, tenantId = request.TenantId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string CandidateSql(string? tenantId, DatabaseType databaseType, int take)
    {
        var where = CandidateWhere(tenantId);

        var columns =
            "h.PROC_INST_ID_ as ProcessInstanceId, h.PROC_DEF_ID_ as ProcessDefinitionId, " +
            "h.BUSINESS_KEY_ as BusinessKey, h.START_USER_ID_ as StartUserId, " +
            "h.START_TIME_ as StartTime, h.END_TIME_ as EndTime, h.TENANT_ID_ as TenantId";

        return databaseType switch
        {
            DatabaseType.SqlServer =>
                $"select top ({take}) {columns} from ACT_HI_PROCINST h{where} order by h.END_TIME_ asc",

            DatabaseType.MySql =>
                $"select {columns} from ACT_HI_PROCINST h{where} order by h.END_TIME_ asc limit {take}",

            _ => throw new NotSupportedException($"不支持的数据库方言：{databaseType}"),
        };
    }

    private static string CandidateWhere(string? tenantId)
    {
        var where =
            " where h.END_TIME_ is not null and h.END_TIME_ < @before" +
            " and not exists (select 1 from admin_history_archive a where a.process_instance_id = h.PROC_INST_ID_)";

        return string.IsNullOrWhiteSpace(tenantId)
            ? where
            : where + " and h.TENANT_ID_ = @tenantId";
    }

    private static async Task ArchiveOneAsync(
        DbConnection connection,
        DbTransaction transaction,
        ArchiveCandidate candidate,
        string? archivedBy,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var archivedAt = DateTime.UtcNow;

        var payload = await BuildPayloadAsync(connection, transaction, candidate.ProcessInstanceId, ct)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                Id = id,
                candidate.ProcessInstanceId,
                candidate.TenantId,
                candidate.ProcessDefinitionId,
                candidate.BusinessKey,
                candidate.StartUserId,
                candidate.StartTime,
                candidate.EndTime,
                ArchivedAt = archivedAt,
                ArchivedBy = archivedBy,
                Payload = JsonSerializer.Serialize(payload, PayloadJsonOptions),
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await DeleteHistoryAsync(connection, transaction, candidate.ProcessInstanceId, ct).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, object?>> BuildPayloadAsync(
        DbConnection connection,
        DbTransaction transaction,
        string processInstanceId,
        CancellationToken ct)
    {
        async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAsync(string sql, object parameters)
        {
            var rows = await connection
                .QueryAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct))
                .ConfigureAwait(false);

            return [.. rows.Select(row => Normalize((object)row))];
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["processInstance"] = await ReadAsync(
                "select * from ACT_HI_PROCINST where PROC_INST_ID_ = @id",
                new { id = processInstanceId }).ConfigureAwait(false),

            ["activities"] = await ReadAsync(
                "select * from ACT_HI_ACTINST where PROC_INST_ID_ = @id order by START_TIME_, ID_",
                new { id = processInstanceId }).ConfigureAwait(false),

            ["tasks"] = await ReadAsync(
                "select * from ACT_HI_TASKINST where PROC_INST_ID_ = @id order by START_TIME_, ID_",
                new { id = processInstanceId }).ConfigureAwait(false),

            ["variables"] = await ReadAsync(
                "select * from ACT_HI_VARINST where PROC_INST_ID_ = @id order by NAME_, ID_",
                new { id = processInstanceId }).ConfigureAwait(false),

            ["identityLinks"] = await ReadAsync(
                """
                select * from ACT_HI_IDENTITYLINK
                where PROC_INST_ID_ = @id
                   or TASK_ID_ in (select ID_ from ACT_HI_TASKINST where PROC_INST_ID_ = @id)
                """,
                new { id = processInstanceId }).ConfigureAwait(false),
        };
    }

    private static async Task DeleteHistoryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string processInstanceId,
        CancellationToken ct)
    {
        var parameters = new { id = processInstanceId };

        // 先删身份关系（子查询依赖 ACT_HI_TASKINST 仍在），再删变量字节行，最后删主行
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from ACT_HI_IDENTITYLINK
            where PROC_INST_ID_ = @id
               or TASK_ID_ in (select ID_ from ACT_HI_TASKINST where PROC_INST_ID_ = @id)
            """,
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from ACT_GE_BYTEARRAY
            where ID_ in (select BYTEARRAY_ID_ from ACT_HI_VARINST
                          where PROC_INST_ID_ = @id and BYTEARRAY_ID_ is not null)
            """,
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_HI_VARINST where PROC_INST_ID_ = @id",
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_HI_TASKINST where PROC_INST_ID_ = @id",
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_HI_ACTINST where PROC_INST_ID_ = @id",
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_HI_PROCINST where PROC_INST_ID_ = @id",
            parameters,
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <summary>把 Dapper 行转成可序列化的字典：<c>DBNull</c> → null、字节 → Base64、时间统一 UTC。</summary>
    private static Dictionary<string, object?> Normalize(object row)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pair in (IDictionary<string, object>)row)
        {
            result[pair.Key] = pair.Value switch
            {
                null or DBNull => null,
                byte[] bytes => Convert.ToBase64String(bytes),
                DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
                _ => pair.Value,
            };
        }

        return result;
    }

    private static (string Where, DynamicParameters Parameters) BuildWhere(HistoryArchiveQuery query)
    {
        var where = new System.Text.StringBuilder(" where 1 = 1");
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.TenantId))
        {
            where.Append(" and tenant_id = @tenantId");
            parameters.Add("tenantId", query.TenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.BusinessKey))
        {
            where.Append(" and business_key = @businessKey");
            parameters.Add("businessKey", query.BusinessKey);
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            where.Append(" and process_instance_id = @processInstanceId");
            parameters.Add("processInstanceId", query.ProcessInstanceId);
        }

        if (query.ArchivedAfter is { } archivedAfter)
        {
            where.Append(" and archived_at >= @archivedAfter");
            parameters.Add("archivedAfter", archivedAfter);
        }

        return (where.ToString(), parameters);
    }

    private static HistoryArchiveSummary Map(ArchiveRow row) =>
        new(
            row.Id,
            row.ProcessInstanceId,
            row.TenantId,
            row.ProcessDefinitionId,
            row.BusinessKey,
            row.StartUserId,
            ToUtc(row.StartTime),
            ToUtc(row.EndTime),
            DateTime.SpecifyKind(row.ArchivedAt, DateTimeKind.Utc),
            row.ArchivedBy);

    private static DateTime? ToUtc(DateTime? value) =>
        value is { } dateTime ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : null;

    // 显式别名：Dapper 默认不做「下划线 → 驼峰」映射
    private const string Columns =
        "id as Id, process_instance_id as ProcessInstanceId, tenant_id as TenantId, " +
        "process_definition_id as ProcessDefinitionId, business_key as BusinessKey, " +
        "start_user_id as StartUserId, start_time as StartTime, end_time as EndTime, " +
        "archived_at as ArchivedAt, archived_by as ArchivedBy";

    private const string InsertSql = """
        insert into admin_history_archive
            (id, process_instance_id, tenant_id, process_definition_id, business_key, start_user_id,
             start_time, end_time, archived_at, archived_by, payload)
        values
            (@Id, @ProcessInstanceId, @TenantId, @ProcessDefinitionId, @BusinessKey, @StartUserId,
             @StartTime, @EndTime, @ArchivedAt, @ArchivedBy, @Payload)
        """;

    /// <summary>归档候选（从 <c>ACT_HI_PROCINST</c> 读出）。</summary>
    private sealed class ArchiveCandidate
    {
        public string ProcessInstanceId { get; set; } = string.Empty;

        public string? ProcessDefinitionId { get; set; }

        public string? BusinessKey { get; set; }

        public string? StartUserId { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public string? TenantId { get; set; }
    }

    private sealed class ArchiveRow
    {
        public string Id { get; set; } = string.Empty;

        public string ProcessInstanceId { get; set; } = string.Empty;

        public string? TenantId { get; set; }

        public string? ProcessDefinitionId { get; set; }

        public string? BusinessKey { get; set; }

        public string? StartUserId { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public DateTime ArchivedAt { get; set; }

        public string? ArchivedBy { get; set; }

        /// <summary>历史行快照（只有详情查询会选它）。</summary>
        public string? Payload { get; set; }
    }
}
