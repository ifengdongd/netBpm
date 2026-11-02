using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using NetBpm.Platform.Contracts;

namespace NetBpm.Persistence.Platform;

/// <summary>
/// 参数修订历史的存储（表 <c>nb_parameter_snapshot</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>append-only</b>：接口刻意没有 update / delete 单个修订。参数是业务单据的事实，
/// 改一条历史等于改掉「当时提交的是多少」这个结论。
/// </para>
/// <para>
/// <see cref="AppendAsync"/> 的 <c>Revision</c> 由存储层分配（读当前最大值 +1，撞主键就重读重试）：
/// 修订号是并发安全的产物，不该由业务决定。调用方拿到的返回值里带着真实修订号。
/// </para>
/// </remarks>
public interface IParameterSnapshotStore
{
    /// <summary>追加一版参数修订，返回带真实修订号的那一版。</summary>
    /// <param name="revision">待落库的修订（<see cref="ParameterRevision.Revision"/> 会被忽略）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ParameterSnapshotRecord> AppendAsync(
        ParameterRevision revision,
        CancellationToken ct = default);

    /// <summary>取某实例最新一版修订；没有任何修订时返回 <see langword="null"/>。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<ParameterSnapshotRecord?> LatestAsync(string instanceId, CancellationToken ct = default);

    /// <summary>按实例读修订历史（修订号倒序：最近的在前）。</summary>
    /// <param name="instanceId">流程实例 id。</param>
    /// <param name="limit">最多取多少版。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<ParameterSnapshotRecord>> ListAsync(
        string instanceId,
        int limit,
        CancellationToken ct = default);

    /// <summary>删除修订时间早于 <paramref name="before"/> 的记录，返回删除条数。</summary>
    /// <param name="before">截止时间（UTC）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default);
}

/// <summary>
/// 一次待落库的参数修订（写入侧视图：没有修订号，由存储分配）。
/// </summary>
/// <param name="InstanceId">流程实例 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key（写入时定稿）。</param>
/// <param name="Source">来源 <c>START</c> / <c>SUBMIT</c> / <c>ADMIN</c>。</param>
/// <param name="TaskId">引起修订的任务 id（发起时为空）。</param>
/// <param name="ActivityId">引起修订的环节 id。</param>
/// <param name="Values">修订后的参数值（键 = apiName）。</param>
/// <param name="UpdatedAt">修订时间（UTC）。</param>
/// <param name="UpdatedBy">修订人（令牌 subject）。</param>
public sealed record ParameterRevision(
    string InstanceId,
    string TenantId,
    string? ProcessKey,
    string Source,
    string? TaskId,
    string? ActivityId,
    IReadOnlyDictionary<string, string?> Values,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy)
{
    /// <summary>修订号（仅供 <see cref="ParameterSnapshotRecord"/> 复用；写入时忽略）。</summary>
    public long Revision { get; init; }
}

/// <summary>已落库的一版参数修订。</summary>
/// <param name="InstanceId">流程实例 id。</param>
/// <param name="Revision">实例内单调修订号（从 1 开始）。</param>
/// <param name="TenantId">租户。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="Source">来源 <c>START</c> / <c>SUBMIT</c> / <c>ADMIN</c>。</param>
/// <param name="TaskId">引起修订的任务 id。</param>
/// <param name="ActivityId">引起修订的环节 id。</param>
/// <param name="Values">修订后的参数值（键 = apiName）。</param>
/// <param name="UpdatedAt">修订时间（UTC）。</param>
/// <param name="UpdatedBy">修订人。</param>
public sealed record ParameterSnapshotRecord(
    string InstanceId,
    long Revision,
    string TenantId,
    string? ProcessKey,
    string Source,
    string? TaskId,
    string? ActivityId,
    IReadOnlyDictionary<string, string?> Values,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>修订来源：谁引起的这一版（审计与展示都要能区分）。</summary>
public static class ParameterRevisionSources
{
    /// <summary>发起实例（参数第一次落地）。</summary>
    public const string Start = "START";

    /// <summary>审批提交携带的参数变更。</summary>
    public const string Submit = "SUBMIT";

    /// <summary>管理面直写变量（<c>/api/admin</c> 的变量接口）。</summary>
    public const string Admin = "ADMIN";
}

/// <summary>修订记录里 JSON 列的口径。</summary>
internal static class ParameterSnapshotJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal static string Write(IReadOnlyDictionary<string, string?> values) =>
        JsonSerializer.Serialize(values, Options);

    internal static Dictionary<string, string?> Read(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>数据库版参数修订历史。</summary>
public sealed class DapperParameterSnapshotStore : IParameterSnapshotStore
{
    private const string Columns =
        "process_instance_id as InstanceId, revision as Revision, tenant_id as TenantId, "
        + "process_key as ProcessKey, source as Source, task_id as TaskId, activity_id as ActivityId, "
        + "values_json as ValuesJson, updated_at as UpdatedAt, updated_by as UpdatedBy";

    /// <summary>主键冲突时的重试次数（并发提交同一实例）。</summary>
    private const int MaxAppendAttempts = 5;

    private const string MaxRevisionSql =
        "select coalesce(max(revision), 0) from nb_parameter_snapshot where process_instance_id = @instanceId";

    private readonly IDbConnectionFactory _connections;

    /// <summary>构造。</summary>
    /// <param name="connections">连接工厂。</param>
    public DapperParameterSnapshotStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask<ParameterSnapshotRecord> AppendAsync(
        ParameterRevision revision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.InstanceId);

        await using var connection = _connections.CreateConnection();

        for (var attempt = 1; ; attempt++)
        {
            var next = await connection
                .ExecuteScalarAsync<long>(new CommandDefinition(
                    MaxRevisionSql,
                    new { instanceId = revision.InstanceId },
                    cancellationToken: ct))
                .ConfigureAwait(false) + 1;

            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "insert into nb_parameter_snapshot "
                    + "(process_instance_id, revision, tenant_id, process_key, source, task_id, "
                    + "activity_id, values_json, updated_at, updated_by) "
                    + "values (@instanceId, @revision, @tenantId, @processKey, @source, @taskId, "
                    + "@activityId, @values, @updatedAt, @updatedBy)",
                    new
                    {
                        instanceId = revision.InstanceId,
                        revision = next,
                        tenantId = string.IsNullOrWhiteSpace(revision.TenantId) ? "default" : revision.TenantId,
                        processKey = revision.ProcessKey,
                        source = string.IsNullOrWhiteSpace(revision.Source)
                            ? ParameterRevisionSources.Submit
                            : revision.Source,
                        taskId = revision.TaskId,
                        activityId = revision.ActivityId,
                        values = ParameterSnapshotJson.Write(revision.Values),
                        updatedAt = revision.UpdatedAt.UtcDateTime,
                        updatedBy = revision.UpdatedBy,
                    },
                    cancellationToken: ct)).ConfigureAwait(false);

                return new ParameterSnapshotRecord(
                    revision.InstanceId,
                    next,
                    string.IsNullOrWhiteSpace(revision.TenantId) ? "default" : revision.TenantId,
                    revision.ProcessKey,
                    revision.Source,
                    revision.TaskId,
                    revision.ActivityId,
                    revision.Values,
                    revision.UpdatedAt,
                    revision.UpdatedBy);
            }
            catch (Exception exception) when (attempt < MaxAppendAttempts && DatabaseErrors.IsUniqueViolation(exception))
            {
                // 另一个写入者抢先占了这个修订号：重读 max 再来一次
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ParameterSnapshotRecord?> LatestAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await using var connection = _connections.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("instanceId", instanceId);

        // 方言分页：MySQL 认 limit，SQL Server 只认 offset/fetch（见 SqlPaging）
        var paging = SqlPaging.Build(_connections.DatabaseType, 0, 1, parameters);

        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"select {Columns} from nb_parameter_snapshot "
            + $"where process_instance_id = @instanceId order by revision desc{paging}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : ToRecord(row);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ParameterSnapshotRecord>> ListAsync(
        string instanceId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await using var connection = _connections.CreateConnection();

        var parameters = new DynamicParameters();
        parameters.Add("instanceId", instanceId);

        var paging = SqlPaging.Build(_connections.DatabaseType, 0, Math.Clamp(limit, 1, 200), parameters);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"select {Columns} from nb_parameter_snapshot "
            + $"where process_instance_id = @instanceId order by revision desc{paging}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(ToRecord)];
    }

    /// <inheritdoc />
    public async ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_parameter_snapshot where updated_at < @before",
            new { before },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static ParameterSnapshotRecord ToRecord(Row row) =>
        new(
            InstanceId: row.InstanceId,
            Revision: row.Revision,
            TenantId: row.TenantId,
            ProcessKey: row.ProcessKey,
            Source: row.Source,
            TaskId: row.TaskId,
            ActivityId: row.ActivityId,
            Values: ParameterSnapshotJson.Read(row.ValuesJson),
            UpdatedAt: PlatformTimestamps.ToOffset(row.UpdatedAt),
            UpdatedBy: row.UpdatedBy);

    /// <summary>Dapper 行（属性名与 SQL 别名一致）。</summary>
    private sealed class Row
    {
        public string InstanceId { get; set; } = string.Empty;

        public long Revision { get; set; }

        public string TenantId { get; set; } = "default";

        public string? ProcessKey { get; set; }

        public string Source { get; set; } = ParameterRevisionSources.Submit;

        public string? TaskId { get; set; }

        public string? ActivityId { get; set; }

        public string? ValuesJson { get; set; }

        public DateTime UpdatedAt { get; set; }

        public string? UpdatedBy { get; set; }
    }
}

/// <summary>
/// 内存版参数修订历史（无库宿主 / 单元测试）。
/// </summary>
public sealed class InMemoryParameterSnapshotStore : IParameterSnapshotStore
{
    private readonly ConcurrentDictionary<string, List<ParameterSnapshotRecord>> _byInstance =
        new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask<ParameterSnapshotRecord> AppendAsync(
        ParameterRevision revision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.InstanceId);

        lock (_gate)
        {
            var list = _byInstance.GetOrAdd(revision.InstanceId, static _ => []);

            var record = new ParameterSnapshotRecord(
                revision.InstanceId,
                list.Count + 1L,
                string.IsNullOrWhiteSpace(revision.TenantId) ? "default" : revision.TenantId,
                revision.ProcessKey,
                string.IsNullOrWhiteSpace(revision.Source) ? ParameterRevisionSources.Submit : revision.Source,
                revision.TaskId,
                revision.ActivityId,
                revision.Values,
                revision.UpdatedAt,
                revision.UpdatedBy);

            list.Add(record);

            return ValueTask.FromResult(record);
        }
    }

    /// <inheritdoc />
    public ValueTask<ParameterSnapshotRecord?> LatestAsync(string instanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        lock (_gate)
        {
            return ValueTask.FromResult(
                _byInstance.TryGetValue(instanceId, out var list) && list.Count > 0
                    ? list[^1]
                    : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ParameterSnapshotRecord>> ListAsync(
        string instanceId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        lock (_gate)
        {
            IReadOnlyList<ParameterSnapshotRecord> result =
                _byInstance.TryGetValue(instanceId, out var list)
                    ? [.. list.OrderByDescending(item => item.Revision).Take(Math.Clamp(limit, 1, 200))]
                    : [];

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneAsync(DateTime before, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var removed = 0;

            foreach (var (instanceId, list) in _byInstance)
            {
                removed += list.RemoveAll(item => item.UpdatedAt.UtcDateTime < before);

                if (list.Count == 0)
                {
                    _byInstance.TryRemove(instanceId, out _);
                }
            }

            return ValueTask.FromResult(removed);
        }
    }
}
