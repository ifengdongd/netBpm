using System.Text;
using Dapper;

namespace NetBpm.Persistence.Admin;

/// <summary>
/// 一条管理动作审计记录。
/// <para>
/// 只记录「谁在什么时候对什么做了什么、结果如何」，<b>不记录变量值</b>
/// （变量可能是敏感业务数据，且会让审计表膨胀）。
/// </para>
/// </summary>
/// <param name="Id">审计记录 id。</param>
/// <param name="TenantId">租户（与引擎 <c>TENANT_ID_</c> 同口径，可为空）。</param>
/// <param name="Actor">操作人（JWT 的 <c>sub</c> 或 API Key 模式下的 <c>X-User-Id</c>）。</param>
/// <param name="AuthMode">鉴权方式（<c>none</c> / <c>api_key</c> / <c>jwt</c>）。</param>
/// <param name="Action">动作标识（如 <c>task.reassign</c> / <c>instance.jump</c> / <c>variable.write</c>）。</param>
/// <param name="TargetType">目标类型（<c>task</c> / <c>process_instance</c> / <c>variable</c>）。</param>
/// <param name="TargetId">目标 id。</param>
/// <param name="ProcessInstanceId">关联的流程实例 id（便于按实例追溯）。</param>
/// <param name="Detail">动作明细（JSON；不含变量值）。</param>
/// <param name="Success">动作是否成功。</param>
/// <param name="Error">失败原因（成功时为 <see langword="null"/>）。</param>
/// <param name="CreatedAt">写入时间（UTC）。</param>
public sealed record AdminAuditEntry(
    string Id,
    string? TenantId,
    string? Actor,
    string AuthMode,
    string Action,
    string TargetType,
    string TargetId,
    string? ProcessInstanceId,
    string? Detail,
    bool Success,
    string? Error,
    DateTime CreatedAt);

/// <summary>审计查询条件（分页）。</summary>
/// <param name="Actor">操作人（精确匹配）。</param>
/// <param name="Action">动作（精确匹配）。</param>
/// <param name="TargetType">目标类型。</param>
/// <param name="TargetId">目标 id。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="TenantId">租户（受限身份由调用方强制注入）。</param>
/// <param name="Success">只看成功 / 失败。</param>
/// <param name="Since">写入时间下界（含）。</param>
/// <param name="Until">写入时间上界（不含）。</param>
/// <param name="FirstResult">跳过条数。</param>
/// <param name="MaxResults">页大小（调用方负责夹取上限）。</param>
public sealed record AdminAuditQuery(
    string? Actor = null,
    string? Action = null,
    string? TargetType = null,
    string? TargetId = null,
    string? ProcessInstanceId = null,
    string? TenantId = null,
    bool? Success = null,
    DateTime? Since = null,
    DateTime? Until = null,
    int FirstResult = 0,
    int MaxResults = 20);

/// <summary>审计分页结果。</summary>
/// <param name="Items">当前页。</param>
/// <param name="Total">满足条件的总数。</param>
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditEntry> Items, int Total);

/// <summary>按动作聚合的审计计数（指标用）。</summary>
/// <param name="Action">动作标识。</param>
/// <param name="Count">条数。</param>
public sealed record AdminAuditSummary(string Action, int Count);

/// <summary>
/// 管理动作审计存储。宿主可选内存实现（单进程 / 开发）或数据库实现（多实例 / 可追溯）。
/// </summary>
public interface IAdminAuditStore
{
    /// <summary>追加一条审计记录。</summary>
    ValueTask RecordAsync(AdminAuditEntry entry, CancellationToken ct = default);

    /// <summary>分页查询审计记录（按时间倒序）。</summary>
    ValueTask<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken ct = default);

    /// <summary>按动作聚合指定时间之后的审计条数。</summary>
    ValueTask<IReadOnlyList<AdminAuditSummary>> SummarizeAsync(
        string? tenantId,
        DateTime since,
        CancellationToken ct = default);

    /// <summary>
    /// 删除写入时间早于 <paramref name="before"/> 的审计记录（保留策略）。
    /// <para>
    /// 清理本身是管理动作，调用方应在清理后写一条 <c>audit.purge</c>；
    /// 只要 <paramref name="before"/> 不晚于当前时间，这条新记录不会被同一次清理删掉。
    /// </para>
    /// </summary>
    /// <param name="tenantId">只清理该租户（<see langword="null"/> 表示全部租户，供系统级保留策略使用）。</param>
    /// <param name="before">写入时间上界（严格小于）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>删除条数。</returns>
    ValueTask<int> PurgeAsync(string? tenantId, DateTime before, CancellationToken ct = default);
}

/// <summary>
/// 单进程审计存储（开发 / 单实例部署）。
/// <para>只保留最近 <see cref="Capacity"/> 条：内存实现不承担长期审计职责，
/// 生产环境应使用数据库实现（<see cref="DapperAdminAuditStore"/>）。</para>
/// </summary>
public sealed class InMemoryAdminAuditStore : IAdminAuditStore
{
    /// <summary>内存中保留的最大条数。</summary>
    public const int Capacity = 10_000;

    private readonly Queue<AdminAuditEntry> _entries = new();
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public ValueTask RecordAsync(AdminAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _entries.Enqueue(entry);

            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        List<AdminAuditEntry> matched;

        lock (_gate)
        {
            matched = [.. _entries.Where(entry => Matches(entry, query))];
        }

        matched.Reverse();

        var total = matched.Count;

        var page = matched
            .Skip(Math.Max(0, query.FirstResult))
            .Take(Math.Max(0, query.MaxResults))
            .ToArray();

        return ValueTask.FromResult(new AdminAuditPage(page, total));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AdminAuditSummary>> SummarizeAsync(
        string? tenantId,
        DateTime since,
        CancellationToken ct = default)
    {
        List<AdminAuditEntry> matched;

        lock (_gate)
        {
            matched = [.. _entries.Where(entry =>
                entry.CreatedAt >= since && TenantMatches(entry.TenantId, tenantId))];
        }

        var summary = matched
            .GroupBy(entry => entry.Action, StringComparer.Ordinal)
            .Select(group => new AdminAuditSummary(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Action, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<AdminAuditSummary>>(summary);
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeAsync(string? tenantId, DateTime before, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var kept = new Queue<AdminAuditEntry>(_entries.Count);
            var removed = 0;

            foreach (var entry in _entries)
            {
                if (entry.CreatedAt < before && TenantMatches(entry.TenantId, tenantId))
                {
                    removed++;
                    continue;
                }

                kept.Enqueue(entry);
            }

            if (removed == 0)
            {
                return ValueTask.FromResult(0);
            }

            _entries.Clear();

            foreach (var entry in kept)
            {
                _entries.Enqueue(entry);
            }

            return ValueTask.FromResult(removed);
        }
    }

    private static bool Matches(AdminAuditEntry entry, AdminAuditQuery query)
    {
        if (!TenantMatches(entry.TenantId, query.TenantId))
        {
            return false;
        }

        if (query.Success is { } success && entry.Success != success)
        {
            return false;
        }

        if (query.Since is { } since && entry.CreatedAt < since)
        {
            return false;
        }

        if (query.Until is { } until && entry.CreatedAt >= until)
        {
            return false;
        }

        return TextMatches(entry.Actor, query.Actor)
            && TextMatches(entry.Action, query.Action)
            && TextMatches(entry.TargetType, query.TargetType)
            && TextMatches(entry.TargetId, query.TargetId)
            && TextMatches(entry.ProcessInstanceId, query.ProcessInstanceId);
    }

    private static bool TextMatches(string? value, string? expected) =>
        string.IsNullOrWhiteSpace(expected) || string.Equals(value, expected, StringComparison.Ordinal);

    private static bool TenantMatches(string? value, string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) || string.Equals(value, tenantId, StringComparison.Ordinal);
}

/// <summary>
/// 数据库审计存储（多实例 / 需要长期追溯）。表结构见 <c>db/{dialect}/0009_create_admin_governance.sql</c>。
/// </summary>
public sealed class DapperAdminAuditStore : IAdminAuditStore
{
    private readonly IDbConnectionFactory _connections;

    public DapperAdminAuditStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(AdminAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = _connections.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                entry.Id,
                entry.TenantId,
                entry.Actor,
                entry.AuthMode,
                entry.Action,
                entry.TargetType,
                entry.TargetId,
                entry.ProcessInstanceId,
                entry.Detail,
                Success = entry.Success ? 1 : 0,
                entry.Error,
                entry.CreatedAt,
            },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AdminAuditPage> QueryAsync(AdminAuditQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (where, parameters) = BuildWhere(query);

        await using var connection = _connections.CreateConnection();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from admin_audit_log{where}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        if (total == 0)
        {
            return new AdminAuditPage([], 0);
        }

        parameters.Add("skip", Math.Max(0, query.FirstResult));
        parameters.Add("take", Math.Max(1, query.MaxResults));

        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            SelectSql(where, _connections.DatabaseType),
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return new AdminAuditPage([.. rows.Select(Map)], total);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AdminAuditSummary>> SummarizeAsync(
        string? tenantId,
        DateTime since,
        CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var where = string.IsNullOrWhiteSpace(tenantId)
            ? " where created_at >= @since"
            : " where created_at >= @since and tenant_id = @tenantId";

        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(
            $"select action as Action, count(*) as Count from admin_audit_log{where} group by action order by count(*) desc, action asc",
            new { since, tenantId },
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(row => new AdminAuditSummary(row.Action, row.Count))];
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeAsync(string? tenantId, DateTime before, CancellationToken ct = default)
    {
        await using var connection = _connections.CreateConnection();

        var where = string.IsNullOrWhiteSpace(tenantId)
            ? " where created_at < @before"
            : " where created_at < @before and tenant_id = @tenantId";

        return await connection.ExecuteAsync(new CommandDefinition(
            $"delete from admin_audit_log{where}",
            new { before, tenantId },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static string SelectSql(string where, DatabaseType databaseType) => databaseType switch
    {
        DatabaseType.MySql =>
            $"select {Columns} from admin_audit_log{where} order by created_at desc, id desc limit @take offset @skip",

        DatabaseType.SqlServer =>
            $"select {Columns} from admin_audit_log{where} order by created_at desc, id desc " +
            "offset @skip rows fetch next @take rows only",

        _ => throw new NotSupportedException($"不支持的数据库方言：{databaseType}"),
    };

    private static (string Where, DynamicParameters Parameters) BuildWhere(AdminAuditQuery query)
    {
        var where = new StringBuilder(" where 1 = 1");
        var parameters = new DynamicParameters();

        void Add(string column, string name, object? value)
        {
            if (value is null)
            {
                return;
            }

            where.Append(" and ").Append(column).Append(" = @").Append(name);
            parameters.Add(name, value);
        }

        if (!string.IsNullOrWhiteSpace(query.TenantId))
        {
            Add("tenant_id", "tenantId", query.TenantId);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            Add("actor", "actor", query.Actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            Add("action", "action", query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetType))
        {
            Add("target_type", "targetType", query.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(query.TargetId))
        {
            Add("target_id", "targetId", query.TargetId);
        }

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            Add("process_instance_id", "processInstanceId", query.ProcessInstanceId);
        }

        if (query.Success is { } success)
        {
            Add("success", "success", success ? 1 : 0);
        }

        if (query.Since is { } since)
        {
            where.Append(" and created_at >= @since");
            parameters.Add("since", since);
        }

        if (query.Until is { } until)
        {
            where.Append(" and created_at < @until");
            parameters.Add("until", until);
        }

        return (where.ToString(), parameters);
    }

    private static AdminAuditEntry Map(AuditRow row) =>
        new(
            row.Id,
            row.TenantId,
            row.Actor,
            row.AuthMode,
            row.Action,
            row.TargetType,
            row.TargetId,
            row.ProcessInstanceId,
            row.Detail,
            row.Success != 0,
            row.Error,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));

    // 显式别名：Dapper 默认不做「下划线 → 驼峰」映射，直接选列名会让 TargetId / CreatedAt 等静默为空
    private const string Columns =
        "id as Id, tenant_id as TenantId, actor as Actor, auth_mode as AuthMode, action as Action, " +
        "target_type as TargetType, target_id as TargetId, process_instance_id as ProcessInstanceId, " +
        "detail as Detail, success as Success, error as Error, created_at as CreatedAt";

    private const string InsertSql = """
        insert into admin_audit_log
            (id, tenant_id, actor, auth_mode, action, target_type, target_id, process_instance_id,
             detail, success, error, created_at)
        values
            (@Id, @TenantId, @Actor, @AuthMode, @Action, @TargetType, @TargetId, @ProcessInstanceId,
             @Detail, @Success, @Error, @CreatedAt)
        """;

    /// <summary>Dapper 行映射（<c>success</c> 在库中是 int，映射后转 bool）。</summary>
    private sealed class AuditRow
    {
        public string Id { get; set; } = string.Empty;

        public string? TenantId { get; set; }

        public string? Actor { get; set; }

        public string AuthMode { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string TargetType { get; set; } = string.Empty;

        public string TargetId { get; set; } = string.Empty;

        public string? ProcessInstanceId { get; set; }

        public string? Detail { get; set; }

        public int Success { get; set; }

        public string? Error { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>聚合行的 Dapper 映射类型。</summary>
    private sealed class SummaryRow
    {
        public string Action { get; set; } = string.Empty;

        public int Count { get; set; }
    }
}

/// <summary>审计动作标识（写入与查询共用，避免字符串漂移）。</summary>
public static class AdminAuditActions
{
    /// <summary>转办 / 改派待办。</summary>
    public const string TaskReassign = "task.reassign";

    /// <summary>批量转办。</summary>
    public const string TaskBatchReassign = "task.batch_reassign";

    /// <summary>修改任务字段。</summary>
    public const string TaskUpdate = "task.update";

    /// <summary>增加任务候选（用户 / 组）。</summary>
    public const string TaskCandidateAdd = "task.candidate_add";

    /// <summary>移除任务候选（用户 / 组）。</summary>
    public const string TaskCandidateRemove = "task.candidate_remove";

    /// <summary>节点跳转。</summary>
    public const string InstanceJump = "instance.jump";

    /// <summary>流程实例迁移到其它定义。</summary>
    public const string InstanceMigrate = "instance.migrate";

    /// <summary>删除（撤销）流程实例。</summary>
    public const string InstanceDelete = "instance.delete";

    /// <summary>批量删除流程实例（逐项留痕）。</summary>
    public const string InstanceBatchDelete = "instance.batch_delete";

    /// <summary>挂起流程实例（暂停所有人的办理）。</summary>
    public const string InstanceSuspend = "instance.suspend";

    /// <summary>激活流程实例。</summary>
    public const string InstanceActivate = "instance.activate";

    /// <summary>写流程实例变量（单条 / 批量）。</summary>
    public const string VariableWrite = "variable.write";

    /// <summary>删除流程实例变量。</summary>
    public const string VariableDelete = "variable.delete";

    /// <summary>写任务局部变量。</summary>
    public const string TaskVariableWrite = "task_variable.write";

    /// <summary>删除任务局部变量。</summary>
    public const string TaskVariableDelete = "task_variable.delete";

    /// <summary>清理模型历史（按保留版本数删除）。</summary>
    public const string ModelHistoryPrune = "model.history_prune";

    /// <summary>部署（发布）流程定义。</summary>
    public const string DeploymentCreate = "deployment.create";

    /// <summary>删除部署（可能级联删除运行实例）。</summary>
    public const string DeploymentDelete = "deployment.delete";

    /// <summary>挂起流程定义（之后不能启动新实例）。</summary>
    public const string DefinitionSuspend = "definition.suspend";

    /// <summary>激活流程定义。</summary>
    public const string DefinitionActivate = "definition.activate";

    /// <summary>历史归档。</summary>
    public const string HistoryArchive = "history.archive";

    /// <summary>清理审计记录（按保留期删除）。</summary>
    public const string AuditPurge = "audit.purge";
}

/// <summary>审计目标类型常量。</summary>
public static class AdminAuditTargets
{
    /// <summary>任务。</summary>
    public const string Task = "task";

    /// <summary>流程实例。</summary>
    public const string ProcessInstance = "process_instance";

    /// <summary>变量。</summary>
    public const string Variable = "variable";

    /// <summary>历史数据。</summary>
    public const string History = "history";

    /// <summary>部署。</summary>
    public const string Deployment = "deployment";

    /// <summary>流程模型（bpmn_model 编辑源及其版本历史）。</summary>
    public const string Model = "model";

    /// <summary>流程定义。</summary>
    public const string ProcessDefinition = "process_definition";

    /// <summary>审计记录本身（保留策略清理）。</summary>
    public const string Audit = "audit";
}
