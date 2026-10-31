using System.Text.Json;
using Dapper;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 外部任务的数据库存储（表 <c>nb_external_task</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="IDbSessionProvider"/> 而不是连接工厂：登记发生在<b>命令内</b>（执行到节点时），
/// 必须复用命令事务 —— 否则「流程推进了但任务没登记」或反之，都会留下对不上的半截状态。
/// </para>
/// <para>
/// 🔑 <b>领取靠 SQL 抢占，不靠「先查后写」</b>：<c>update ... where 可领取条件</c> 的影响行数
/// 就是仲裁结果。多个 worker 同时拉同一个 topic 时，先查后写必然重复领取；
/// 数据库的行锁是这里唯一可靠的仲裁者。
/// </para>
/// <para>
/// 与 <see cref="InMemoryExternalTaskBroker"/> 的语义必须逐条一致（可领取判定、
/// 退避、锁校验都在 <see cref="ExternalTask"/> 与 <see cref="ExternalTaskRetryPolicy"/> 里共享），
/// 否则「内存宿主跑通、换 MySQL 行为变了」—— 这类差异偶发而非必现，最难查。
/// </para>
/// </remarks>
public sealed class DapperExternalTaskBroker : IExternalTaskBroker
{
    private const string Table = "nb_external_task";

    /// <summary>列名下划线、属性驼峰，Dapper 不自动映射，SQL 里逐列 <c>as</c> 别名（本仓规矩）。</summary>
    private const string Columns =
        "id as Id, process_instance_id as ProcessInstanceId, execution_id as ExecutionId, "
        + "process_definition_id as ProcessDefinitionId, process_definition_key as ProcessDefinitionKey, "
        + "activity_id as ActivityId, activity_name as ActivityName, topic as Topic, "
        + "tenant_id as TenantId, business_key as BusinessKey, payload_json as PayloadJson, "
        + "priority as Priority, retries as Retries, status as Status, worker_id as WorkerId, "
        + "lock_expires_at as LockExpiresAt, next_due_at as NextDueAt, "
        + "error_message as ErrorMessage, error_details as ErrorDetails, "
        + "created_at as CreatedAt, completed_at as CompletedAt";

    /// <summary>
    /// 「可领取」的行判定：待领取且退避期已过，或已被锁但锁已过期（worker 崩溃后自动回收）。
    /// 与 <see cref="ExternalTask.IsAvailable"/> 同义，写在 SQL 里才能靠行锁抢占。
    /// </summary>
    private const string AvailablePredicate =
        "((status = 'PENDING' and (next_due_at is null or next_due_at <= @now)) "
        + "or (status = 'LOCKED' and lock_expires_at is not null and lock_expires_at <= @now))";

    /// <summary>锁校验：必须是当前持锁者且锁未过期（与内存版 <c>IsHeldBy</c> 同义）。</summary>
    private const string HeldByPredicate =
        "status = 'LOCKED' and worker_id = @workerId "
        + "and (lock_expires_at is null or lock_expires_at > @now)";

    private readonly IDbSessionProvider _sessions;
    private readonly ExternalTaskOptions _options;

    /// <summary>构造。</summary>
    /// <param name="sessions">会话提供者。</param>
    /// <param name="options">锁时长 / 默认重试次数。</param>
    public DapperExternalTaskBroker(IDbSessionProvider sessions, ExternalTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
        _options = options ?? new();
    }

    /// <inheritdoc />
    public async ValueTask<ExternalTask> CreateAsync(ExternalTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(task.Id);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             insert into {Table}
             (id, process_instance_id, execution_id, process_definition_id, process_definition_key,
              activity_id, activity_name, topic, tenant_id, business_key, payload_json,
              priority, retries, status, worker_id, lock_expires_at, next_due_at,
              error_message, error_details, created_at, completed_at)
             values
             (@id, @processInstanceId, @executionId, @processDefinitionId, @processDefinitionKey,
              @activityId, @activityName, @topic, @tenantId, @businessKey, @payloadJson,
              @priority, @retries, @status, @workerId, @lockExpiresAt, @nextDueAt,
              @errorMessage, @errorDetails, @createdAt, @completedAt)
             """,
            new
            {
                id = task.Id,
                processInstanceId = task.ProcessInstanceId,
                executionId = task.ExecutionId,
                processDefinitionId = task.ProcessDefinitionId,
                processDefinitionKey = task.ProcessDefinitionKey,
                activityId = task.ActivityId,
                activityName = task.ActivityName,
                topic = task.Topic,
                tenantId = string.IsNullOrWhiteSpace(task.TenantId) ? "default" : task.TenantId,
                businessKey = task.BusinessKey,
                payloadJson = ExternalTaskJson.Write(task.Payload),
                priority = task.Priority,
                retries = task.Retries,
                status = task.Status,
                workerId = task.WorkerId,
                lockExpiresAt = task.LockExpiresAt,
                nextDueAt = task.NextDueAt,
                errorMessage = Trim(task.ErrorMessage, 1000),
                errorDetails = task.ErrorDetails,
                createdAt = task.CreatedAt,
                completedAt = task.CompletedAt,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return task;
    }

    /// <inheritdoc />
    public async ValueTask<ExternalTask?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var lease = _sessions.Acquire();

        return await QueryOneAsync(lease, id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExternalTask>> FetchAndLockAsync(
        ExternalTaskFetchRequest request,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        var topics = request.Topics
            .Select(topic => topic.Topic)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (topics.Length == 0)
        {
            return [];
        }

        var maxTasks = request.MaxTasks <= 0 ? 10 : request.MaxTasks;

        using var lease = _sessions.Acquire();

        var parameters = new DynamicParameters();
        parameters.Add("now", nowUtc);
        parameters.Add("topics", topics);

        // 租户必须收窄：不传就放行等于让 A 租户的 worker 领走 B 租户的活
        var tenantPredicate = request.TenantId is { Length: > 0 }
            ? " and tenant_id = @tenantId"
            : string.Empty;

        if (request.TenantId is { Length: > 0 } tenantId)
        {
            parameters.Add("tenantId", tenantId);
        }

        var paging = SqlPaging.Build(lease.Session.DatabaseType, 0, maxTasks, parameters);

        // ① 先取候选（只取 id 与 topic：锁时长按主题可能不同，payload 后面再读）
        var candidates = await lease.Session.Connection.QueryAsync(new CommandDefinition(
            $"""
             select id as Id, topic as Topic
             from {Table}
             where {AvailablePredicate} and topic in @topics{tenantPredicate}
             order by priority desc, created_at asc, id asc
             {paging}
             """,
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        // ② 逐条抢占：影响行数即仲裁结果（并发下别的 worker 可能先抢走）
        var locked = new List<ExternalTask>();

        foreach (var candidate in candidates)
        {
            var id = (string)candidate.Id;
            var topic = (string)candidate.Topic;
            var duration = ResolveLockDuration(request, topic);

            var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
                $"""
                 update {Table}
                 set status = 'LOCKED', worker_id = @workerId, lock_expires_at = @lockUntil
                 where id = @id and {AvailablePredicate}
                 """,
                new { id, workerId = request.WorkerId, lockUntil = nowUtc + duration, now = nowUtc },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            if (rows == 0)
            {
                continue;
            }

            var task = await QueryOneAsync(lease, id, ct).ConfigureAwait(false);

            if (task is not null)
            {
                locked.Add(task);
            }
        }

        return locked;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompleteAsync(
        string id,
        string workerId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             update {Table}
             set status = 'COMPLETED', completed_at = @now, lock_expires_at = null,
                 error_message = null, error_details = null
             where id = @id and {HeldByPredicate}
             """,
            new { id, workerId, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> FailureAsync(
        string id,
        string workerId,
        ExternalTaskFailure failure,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(failure);

        using var lease = _sessions.Acquire();

        // 剩余次数要么 worker 指定，要么当前值减一 —— 需要在同一会话里读当前值，
        // 否则算出来的退避对不上（读与写跨会话还会被别的 worker 插一脚）
        var current = await QueryOneAsync(lease, id, ct).ConfigureAwait(false);

        var remaining = failure.Retries ?? (current?.Retries ?? 0) - 1;

        if (remaining <= 0)
        {
            var dead = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
                $"""
                 update {Table}
                 set status = 'DEAD_LETTER', retries = 0, next_due_at = null,
                     worker_id = null, lock_expires_at = null,
                     error_message = @errorMessage, error_details = @errorDetails
                 where id = @id and {HeldByPredicate}
                 """,
                new
                {
                    id,
                    workerId,
                    now = nowUtc,
                    errorMessage = Trim(failure.ErrorMessage, 1000),
                    errorDetails = failure.ErrorDetails,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            return dead > 0;
        }

        var nextDue = failure.RetryAfter
            ?? nowUtc + ExternalTaskRetryPolicy.Backoff(ExternalTaskRetryPolicy.AttemptOf(remaining));

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             update {Table}
             set status = 'PENDING', retries = @retries, next_due_at = @nextDue,
                 worker_id = null, lock_expires_at = null,
                 error_message = @errorMessage, error_details = @errorDetails
             where id = @id and {HeldByPredicate}
             """,
            new
            {
                id,
                workerId,
                now = nowUtc,
                retries = remaining,
                nextDue,
                errorMessage = Trim(failure.ErrorMessage, 1000),
                errorDetails = failure.ErrorDetails,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExtendLockAsync(
        string id,
        string workerId,
        TimeSpan lockDuration,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"update {Table} set lock_expires_at = @lockUntil where id = @id and {HeldByPredicate}",
            new
            {
                id,
                workerId,
                now = nowUtc,
                lockUntil = nowUtc + (lockDuration > TimeSpan.Zero ? lockDuration : _options.DefaultLockDuration),
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> UnlockAsync(string id, string workerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        using var lease = _sessions.Acquire();

        // 不检查锁是否过期：锁已过期时它早就不持有这把锁了，让它「释放成功」
        // 会掩盖「其实已经被别人领走」这件事
        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             update {Table}
             set status = 'PENDING', worker_id = null, lock_expires_at = null
             where id = @id and status = 'LOCKED' and worker_id = @workerId
             """,
            new { id, workerId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ExternalTask>> FindAsync(
        ExternalTaskQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();

        var parameters = new DynamicParameters();
        var where = new List<string> { "1 = 1" };

        if (query.TenantId is { Length: > 0 } tenantId)
        {
            where.Add("tenant_id = @tenantId");
            parameters.Add("tenantId", tenantId);
        }

        if (query.Topic is { Length: > 0 } topic)
        {
            where.Add("topic = @topic");
            parameters.Add("topic", topic);
        }

        if (query.ProcessInstanceId is { Length: > 0 } instanceId)
        {
            where.Add("process_instance_id = @instanceId");
            parameters.Add("instanceId", instanceId);
        }

        if (query.Status is { Length: > 0 } status)
        {
            where.Add("status = @status");
            parameters.Add("status", status);
        }

        if (query.WorkerId is { Length: > 0 } workerId)
        {
            where.Add("worker_id = @workerId");
            parameters.Add("workerId", workerId);
        }

        if (query.OnlyDue)
        {
            where.Add(AvailablePredicate);
            parameters.Add("now", DateTime.UtcNow);
        }

        var paging = SqlPaging.Build(
            lease.Session.DatabaseType, 0, query.MaxResults <= 0 ? 100 : query.MaxResults, parameters);

        var rows = await lease.Session.Connection.QueryAsync<Row>(new CommandDefinition(
            $"""
             select {Columns}
             from {Table}
             where {string.Join(" and ", where)}
             order by created_at asc, id asc
             {paging}
             """,
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return [.. rows.Select(ToEntity)];
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryDeadLetterAsync(string id, int retries, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             update {Table}
             set status = 'PENDING', retries = @retries, next_due_at = null,
                 worker_id = null, lock_expires_at = null,
                 error_message = null, error_details = null
             where id = @id and status = 'DEAD_LETTER'
             """,
            new { id, retries = retries > 0 ? retries : _options.DefaultRetries },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"delete from {Table} where process_instance_id = @processInstanceId",
            new { processInstanceId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    // ─────────────────────────── 内部 ───────────────────────────

    private static async Task<ExternalTask?> QueryOneAsync(
        DbSessionLease lease,
        string id,
        CancellationToken ct)
    {
        var row = await lease.Session.Connection
            .QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
                $"select {Columns} from {Table} where id = @id",
                new { id },
                lease.Session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        return row is null ? null : ToEntity(row);
    }

    private TimeSpan ResolveLockDuration(ExternalTaskFetchRequest request, string topic)
    {
        var byTopic = request.Topics
            .FirstOrDefault(item => string.Equals(item.Topic, topic, StringComparison.Ordinal))
            ?.LockDuration;

        return byTopic ?? request.LockDuration ?? _options.DefaultLockDuration;
    }

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } text && text.Length > max ? text[..max] : value;

    private static ExternalTask ToEntity(Row row) => new()
    {
        Id = row.Id,
        ProcessInstanceId = row.ProcessInstanceId,
        ExecutionId = row.ExecutionId,
        ProcessDefinitionId = row.ProcessDefinitionId,
        ProcessDefinitionKey = row.ProcessDefinitionKey,
        ActivityId = row.ActivityId,
        ActivityName = row.ActivityName,
        Topic = row.Topic,
        TenantId = row.TenantId,
        BusinessKey = row.BusinessKey,
        Payload = ExternalTaskJson.Read(row.PayloadJson),
        Priority = row.Priority ?? 0,
        Retries = row.Retries ?? 0,
        Status = row.Status,
        WorkerId = row.WorkerId,
        LockExpiresAt = row.LockExpiresAt,
        NextDueAt = row.NextDueAt,
        ErrorMessage = row.ErrorMessage,
        ErrorDetails = row.ErrorDetails,
        CreatedAt = row.CreatedAt,
        CompletedAt = row.CompletedAt,
    };

    /// <summary>行映射目标（Dapper 直接绑定，不做下划线→驼峰转换）。</summary>
    private sealed class Row
    {
        public string Id { get; set; } = string.Empty;

        public string ProcessInstanceId { get; set; } = string.Empty;

        public string ExecutionId { get; set; } = string.Empty;

        public string? ProcessDefinitionId { get; set; }

        public string? ProcessDefinitionKey { get; set; }

        public string ActivityId { get; set; } = string.Empty;

        public string? ActivityName { get; set; }

        public string Topic { get; set; } = string.Empty;

        public string? TenantId { get; set; }

        public string? BusinessKey { get; set; }

        public string? PayloadJson { get; set; }

        public int? Priority { get; set; }

        public int? Retries { get; set; }

        public string Status { get; set; } = ExternalTaskStatus.Pending;

        public string? WorkerId { get; set; }

        public DateTime? LockExpiresAt { get; set; }

        public DateTime? NextDueAt { get; set; }

        public string? ErrorMessage { get; set; }

        public string? ErrorDetails { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? CompletedAt { get; set; }
    }
}

/// <summary>
/// payload 的 JSON 口径。
/// <para>
/// 变量快照里可能含 <c>long</c> / <c>double</c> / <c>bool</c> / <c>DateTime</c> / <c>string</c>，
/// 直接 <c>Deserialize&lt;Dictionary&lt;string, object?&gt;&gt;</c> 拿到的是 <c>JsonElement</c>，
/// 引擎侧再取变量就会得到「不是 string 也不是 long」的第三类东西 —— 所以这里显式还原成原始类型。
/// </para>
/// </summary>
internal static class ExternalTaskJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    internal static string? Write(IDictionary<string, object?>? payload) =>
        payload is null or { Count: 0 } ? null : JsonSerializer.Serialize(payload, Options);

    internal static IDictionary<string, object?> Read(string? json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options);

        if (parsed is null)
        {
            return result;
        }

        foreach (var (key, element) in parsed)
        {
            result[key] = ToValue(element);
        }

        return result;
    }

    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? integer
            : element.TryGetDouble(out var number)
                ? number
                : element.GetRawText(),
        _ => element.GetRawText(),
    };
}
