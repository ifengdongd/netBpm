using Dapper;
using NetBpm.Engine.Events;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Events;

/// <summary>
/// 数据库事务性 outbox。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// <para>
/// <see cref="EnqueueAsync"/> 使用当前命令的会话与事务（由 <see cref="IDbSessionProvider"/> 提供），
/// 因此事件与业务变更同生共死。
/// </para>
/// </summary>
public sealed class DapperEventOutbox : IEventOutboxStore
{
    private readonly IDbSessionProvider _sessions;

    public DapperEventOutbox(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(OutboxEvent outboxEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxEvent.Id);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql => InsertIgnoreSql,
            DatabaseType.SqlServer => InsertIfAbsentSql,
            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        await session.Connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                id = outboxEvent.Id,
                eventType = outboxEvent.EventType,
                entityId = outboxEvent.EntityId,
                processInstanceId = outboxEvent.ProcessInstanceId,
                processDefinitionId = outboxEvent.ProcessDefinitionId,
                executionId = outboxEvent.ExecutionId,
                activityId = outboxEvent.ActivityId,
                tenantId = outboxEvent.TenantId ?? string.Empty,
                payload = outboxEvent.Payload,
                createTime = outboxEvent.CreateTime,
            },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxEvent>> FindPendingAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var take = Math.Max(1, maxCount);

        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql => $"""
                select {Columns}
                from ACT_EVT_OUTBOX
                where PUBLISHED_ = 0 and DEAD_LETTER_ = 0
                  and (NEXT_ATTEMPT_ is null or NEXT_ATTEMPT_ <= @utcNow)
                order by CREATE_TIME_, ID_
                limit @take
                """,

            DatabaseType.SqlServer => $"""
                select top (@take) {Columns}
                from ACT_EVT_OUTBOX
                where PUBLISHED_ = 0 and DEAD_LETTER_ = 0
                  and (NEXT_ATTEMPT_ is null or NEXT_ATTEMPT_ <= @utcNow)
                order by CREATE_TIME_, ID_
                """,

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        var rows = await session.Connection.QueryAsync(new CommandDefinition(
            sql,
            new { utcNow, take },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return MapAll(rows);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryClaimAsync(
        string eventId,
        DateTime utcNow,
        DateTime leaseUntilUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        // 单语句条件更新：读-判断-写在数据库侧原子完成。
        // NEXT_ATTEMPT_ 兼作租约到期时间（FindPendingAsync 已经按它过滤），因此不需要新列，
        // 也不会出现「认领成功但没写租约」的中间态。
        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            ClaimSql,
            new { id = eventId, now = utcNow, leaseUntil = leaseUntilUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxEvent>> FindAsync(
        OutboxQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var (where, parameters) = BuildWhere(query);

        var paging = SqlPaging.Build(session.DatabaseType, query.FirstResult, query.MaxResults, parameters);
        var sql = $"select {Columns} from ACT_EVT_OUTBOX{where} order by CREATE_TIME_, ID_{paging}";

        var rows = await session.Connection.QueryAsync(new CommandDefinition(
            sql,
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return MapAll(rows);
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(OutboxQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();
        var (where, parameters) = BuildWhere(query);

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_EVT_OUTBOX{where}",
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkPublishedAsync(
        string eventId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            MarkPublishedSql,
            new { id = eventId, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask MarkFailureAsync(
        string eventId,
        string reason,
        DateTime nowUtc,
        DateTime nextAttemptUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            MarkFailureSql,
            new
            {
                id = eventId,
                error = reason.Length > 1000 ? reason[..1000] : reason,
                now = nowUtc,
                nextAttempt = nextAttemptUtc,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsync(string eventId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            RetrySql,
            new { id = eventId, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string eventId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteSql,
            new { id = eventId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    private static (string Where, DynamicParameters Parameters) BuildWhere(OutboxQuery query)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            clauses.Add("PROCESS_INSTANCE_ID_ = @processInstanceId");
            parameters.Add("processInstanceId", query.ProcessInstanceId.Trim());
        }

        if (query.Published is { } published)
        {
            clauses.Add("PUBLISHED_ = @published");
            parameters.Add("published", published ? 1 : 0);
        }

        if (query.DeadLetter is { } deadLetter)
        {
            clauses.Add("DEAD_LETTER_ = @deadLetter");
            parameters.Add("deadLetter", deadLetter ? 1 : 0);
        }

        if (query.TenantId is not null)
        {
            clauses.Add("TENANT_ID_ = @tenantId");
            parameters.Add("tenantId", query.TenantId);
        }

        var where = clauses.Count == 0 ? string.Empty : " where " + string.Join(" and ", clauses);

        return (where, parameters);
    }

    private static List<OutboxEvent> MapAll(IEnumerable<dynamic> rows)
    {
        var events = new List<OutboxEvent>();

        foreach (IDictionary<string, object?> row in rows)
        {
            events.Add(Map(row));
        }

        return events;
    }

    private static OutboxEvent Map(IDictionary<string, object?> row) =>
        new()
        {
            Id = Text(row, "ID_") ?? string.Empty,
            EventType = Text(row, "EVENT_TYPE_") ?? string.Empty,
            EntityId = Text(row, "ENTITY_ID_"),
            ProcessInstanceId = Text(row, "PROCESS_INSTANCE_ID_"),
            ProcessDefinitionId = Text(row, "PROC_DEF_ID_"),
            ExecutionId = Text(row, "EXECUTION_ID_"),
            ActivityId = Text(row, "ACTIVITY_ID_"),
            TenantId = Text(row, "TENANT_ID_"),
            Payload = Text(row, "PAYLOAD_"),
            CreateTime = Date(row, "CREATE_TIME_"),
            Published = Number(row, "PUBLISHED_") is > 0,
            PublishAttempts = Number(row, "PUBLISH_ATTEMPTS_") ?? 0,
            DeadLetter = Number(row, "DEAD_LETTER_") is > 0,
            LastError = Text(row, "LAST_ERROR_"),
            NextAttemptTime = NullableDate(row, "NEXT_ATTEMPT_"),
            PublishedTime = NullableDate(row, "PUBLISHED_TIME_"),
        };

    private const string Columns = """
        ID_, EVENT_TYPE_, ENTITY_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, EXECUTION_ID_, ACTIVITY_ID_,
        TENANT_ID_, PAYLOAD_, CREATE_TIME_, PUBLISHED_, PUBLISH_ATTEMPTS_, DEAD_LETTER_, LAST_ERROR_,
        NEXT_ATTEMPT_, PUBLISHED_TIME_
        """;

    private const string InsertIgnoreSql = """
        insert ignore into ACT_EVT_OUTBOX
            (ID_, EVENT_TYPE_, ENTITY_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, EXECUTION_ID_, ACTIVITY_ID_,
             TENANT_ID_, PAYLOAD_, CREATE_TIME_, PUBLISHED_, PUBLISH_ATTEMPTS_, DEAD_LETTER_)
        values (@id, @eventType, @entityId, @processInstanceId, @processDefinitionId, @executionId, @activityId,
                @tenantId, @payload, @createTime, 0, 0, 0)
        """;

    private const string InsertIfAbsentSql = """
        insert into ACT_EVT_OUTBOX
            (ID_, EVENT_TYPE_, ENTITY_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, EXECUTION_ID_, ACTIVITY_ID_,
             TENANT_ID_, PAYLOAD_, CREATE_TIME_, PUBLISHED_, PUBLISH_ATTEMPTS_, DEAD_LETTER_)
        select @id, @eventType, @entityId, @processInstanceId, @processDefinitionId, @executionId, @activityId,
               @tenantId, @payload, @createTime, 0, 0, 0
        where not exists (select 1 from ACT_EVT_OUTBOX where ID_ = @id)
        """;

    private const string ClaimSql = """
        update ACT_EVT_OUTBOX
        set NEXT_ATTEMPT_ = @leaseUntil
        where ID_ = @id
          and PUBLISHED_ = 0
          and DEAD_LETTER_ = 0
          and (NEXT_ATTEMPT_ is null or NEXT_ATTEMPT_ <= @now)
        """;

    private const string MarkPublishedSql = """
        update ACT_EVT_OUTBOX
        set PUBLISHED_ = 1, PUBLISHED_TIME_ = @now, LAST_ERROR_ = null
        where ID_ = @id
        """;

    private const string MarkFailureSql = """
        update ACT_EVT_OUTBOX
        set PUBLISH_ATTEMPTS_ = coalesce(PUBLISH_ATTEMPTS_, 0) + 1,
            DEAD_LETTER_ = case when coalesce(PUBLISH_ATTEMPTS_, 0) + 1 >= 5 then 1 else 0 end,
            LAST_ERROR_ = @error,
            NEXT_ATTEMPT_ = @nextAttempt
        where ID_ = @id
        """;

    private const string RetrySql = """
        update ACT_EVT_OUTBOX
        set DEAD_LETTER_ = 0, PUBLISH_ATTEMPTS_ = 0, LAST_ERROR_ = null, NEXT_ATTEMPT_ = @now
        where ID_ = @id and DEAD_LETTER_ = 1
        """;

    private const string DeleteSql = "delete from ACT_EVT_OUTBOX where ID_ = @id";
}
