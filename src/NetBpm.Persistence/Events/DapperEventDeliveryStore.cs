using Dapper;
using NetBpm.Persistence.Database;
using NetBpm.Engine.Events;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Events;

/// <summary>
/// 事件投递存储（<c>nb_event_delivery</c>），粒度是「一条事件 × 一个订阅」。
/// <para>
/// 复合主键 <c>(event_id, subscription_id)</c> 让「取（没有就建）」可以用一条
/// <c>insert ignore</c> 完成：两个宿主同时遇到同一条事件时，只有一个真正插入成功，
/// 另一个读到已存在的行 —— 不需要额外的锁，也不会建出重复行。
/// </para>
/// </summary>
public sealed class DapperEventDeliveryStore : IEventDeliveryStore
{
    private const string Columns =
        "event_id, subscription_id, tenant_id, status, attempts, last_error, next_attempt_at,"
        + " created_at, updated_at, delivered_at";

    private readonly IDbSessionProvider _sessions;

    public DapperEventDeliveryStore(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask<EventDelivery?> FindAsync(
        string eventId,
        string subscriptionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        using var lease = _sessions.Acquire();

        var row = await lease.Session.Connection.QueryFirstOrDefaultAsync(new CommandDefinition(
            $"select {Columns} from nb_event_delivery where event_id = @eventId and subscription_id = @subscriptionId",
            new { eventId, subscriptionId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map((IDictionary<string, object?>)row);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EventDelivery>> FindByEventAsync(
        string eventId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.QueryAsync(new CommandDefinition(
            $"select {Columns} from nb_event_delivery where event_id = @eventId order by subscription_id",
            new { eventId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(row => Map((IDictionary<string, object?>)row)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<EventDelivery> GetOrCreateAsync(
        string eventId,
        string subscriptionId,
        string tenantId,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var now = DateTime.UtcNow;

        var sql = session.DatabaseType switch
        {
            // MySQL：insert ignore 撞主键即忽略，随后读回已存在的那行
            DatabaseType.MySql => $"""
                insert ignore into nb_event_delivery
                    (event_id, subscription_id, tenant_id, status, attempts, created_at, updated_at)
                values
                    (@eventId, @subscriptionId, @tenantId, '{EventDeliveryStatus.Pending}', 0, @now, @now)
                """,

            // SQL Server：先按主键条件插入（exists 判据在同一语句里），撞键则 0 行
            DatabaseType.SqlServer => $"""
                insert into nb_event_delivery
                    (event_id, subscription_id, tenant_id, status, attempts, created_at, updated_at)
                select @eventId, @subscriptionId, @tenantId, '{EventDeliveryStatus.Pending}', 0, @now, @now
                 where not exists (
                    select 1 from nb_event_delivery
                     where event_id = @eventId and subscription_id = @subscriptionId)
                """,

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        await session.Connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { eventId, subscriptionId, tenantId, now },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return await FindAsync(eventId, subscriptionId, ct).ConfigureAwait(false)
            ?? new EventDelivery
            {
                EventId = eventId,
                SubscriptionId = subscriptionId,
                TenantId = tenantId,
                CreatedAt = now,
                UpdatedAt = now,
            };
    }

    /// <inheritdoc />
    public async ValueTask MarkDeliveredAsync(
        string eventId,
        string subscriptionId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
            update nb_event_delivery
               set status = '{EventDeliveryStatus.Delivered}',
                   last_error = null,
                   next_attempt_at = null,
                   delivered_at = @now,
                   updated_at = @now
             where event_id = @eventId and subscription_id = @subscriptionId
            """,
            new { eventId, subscriptionId, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailureAsync(
        string eventId,
        string subscriptionId,
        string reason,
        DateTime nowUtc,
        DateTime? nextAttemptUtc,
        int maxAttempts,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        // attempts + 1 与状态判定放在同一条 update 里：先读再写会与并发的另一个宿主互相覆盖
        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
            update nb_event_delivery
               set attempts = attempts + 1,
                   last_error = @error,
                   next_attempt_at = @nextAttempt,
                   status = case when attempts + 1 >= @maxAttempts then '{EventDeliveryStatus.DeadLetter}'
                                 else '{EventDeliveryStatus.Failed}' end,
                   updated_at = @now
             where event_id = @eventId and subscription_id = @subscriptionId
            """,
            new
            {
                eventId,
                subscriptionId,
                error = reason.Length > 1000 ? reason[..1000] : reason,
                nextAttempt = nextAttemptUtc,
                maxAttempts = Math.Max(1, maxAttempts),
                now = nowUtc,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkDeferredAsync(
        string eventId,
        string subscriptionId,
        DateTime nextAttemptUtc,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        // 只改 next_attempt_at / updated_at：attempts 不动（被限速不算一次尝试），
        // status 不动（这条还没投成，只是现在轮不到它），last_error 也不动（别把上次真实原因冲掉）
        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            """
            update nb_event_delivery
               set next_attempt_at = @nextAttempt,
                   updated_at = @now
             where event_id = @eventId and subscription_id = @subscriptionId
            """,
            new { eventId, subscriptionId, nextAttempt = nextAttemptUtc, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryAsync(
        string eventId,
        string subscriptionId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
            update nb_event_delivery
               set status = '{EventDeliveryStatus.Pending}',
                   attempts = 0,
                   last_error = null,
                   next_attempt_at = @now,
                   updated_at = @now
             where event_id = @eventId and subscription_id = @subscriptionId
               and status = '{EventDeliveryStatus.DeadLetter}'
            """,
            new { eventId, subscriptionId, now = nowUtc },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteByEventAsync(string eventId, CancellationToken ct = default)
    {        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_event_delivery where event_id = @eventId",
            new { eventId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<EventDeadLetterPage> ListDeadLettersAsync(
        EventDeadLetterQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();

        var parameters = new DynamicParameters();
        var conditions = new List<string>(3) { $"status = '{EventDeliveryStatus.DeadLetter}'" };

        if (!string.IsNullOrEmpty(query.TenantId))
        {
            conditions.Add("tenant_id = @tenantId");
            parameters.Add("tenantId", query.TenantId);
        }

        if (!string.IsNullOrEmpty(query.SubscriptionId))
        {
            conditions.Add("subscription_id = @subscriptionId");
            parameters.Add("subscriptionId", query.SubscriptionId);
        }

        var where = "where " + string.Join(" and ", conditions);

        var total = await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from nb_event_delivery {where}",
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        if (total == 0)
        {
            return new EventDeadLetterPage([], 0);
        }

        var paging = SqlPaging.Build(
            lease.Session.DatabaseType,
            query.FirstResult,
            query.MaxResults,
            parameters);

        var rows = await lease.Session.Connection.QueryAsync(new CommandDefinition(
            $"select {Columns} from nb_event_delivery {where} "
            + $"order by updated_at desc, event_id, subscription_id{paging}",
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return new EventDeadLetterPage([.. rows.Select(row => Map((IDictionary<string, object?>)row))], total);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EventDeliveryStats>> GetStatsAsync(
        EventDeliveryStatsQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();

        var parameters = new DynamicParameters();
        var conditions = new List<string>(3);

        if (!string.IsNullOrEmpty(query.TenantId))
        {
            conditions.Add("tenant_id = @tenantId");
            parameters.Add("tenantId", query.TenantId);
        }

        if (!string.IsNullOrEmpty(query.SubscriptionId))
        {
            conditions.Add("subscription_id = @subscriptionId");
            parameters.Add("subscriptionId", query.SubscriptionId);
        }

        if (query.SinceUtc is { } since)
        {
            conditions.Add("created_at >= @since");
            parameters.Add("since", since);
        }

        var where = conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : string.Empty;

        // 聚合交给数据库：投递表是按天长的，全量取回再算会随运行时间慢慢变成一次事故。
        // sum(case when ...) 两个方言都支持（不像 bool 聚合函数那样有方言差异）。
        var rows = await lease.Session.Connection.QueryAsync(new CommandDefinition(
            $"""
             select subscription_id,
                    max(tenant_id) as tenant_id,
                    count(*) as total_count,
                    sum(case when status = '{EventDeliveryStatus.Pending}' then 1 else 0 end) as pending_count,
                    sum(case when status = '{EventDeliveryStatus.Delivered}' then 1 else 0 end) as delivered_count,
                    sum(case when status = '{EventDeliveryStatus.Failed}' then 1 else 0 end) as failed_count,
                    sum(case when status = '{EventDeliveryStatus.DeadLetter}' then 1 else 0 end) as dead_letter_count,
                    max(delivered_at) as last_delivered_at,
                    max(case when status in ('{EventDeliveryStatus.Failed}', '{EventDeliveryStatus.DeadLetter}')
                             then updated_at else null end) as last_failure_at
               from nb_event_delivery{where}
              group by subscription_id
              order by subscription_id
             """,
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return
        [
            .. rows.Select(row =>
            {
                var values = (IDictionary<string, object?>)row;

                return new EventDeliveryStats(
                    Text(values, "subscription_id") ?? string.Empty,
                    Text(values, "tenant_id") ?? "default",
                    Number(values, "total_count") ?? 0,
                    Number(values, "pending_count") ?? 0,
                    Number(values, "delivered_count") ?? 0,
                    Number(values, "failed_count") ?? 0,
                    Number(values, "dead_letter_count") ?? 0,
                    AsOffset(NullableDate(values, "last_delivered_at")),
                    AsOffset(NullableDate(values, "last_failure_at")));
            }),
        ];
    }

    private static DateTimeOffset? AsOffset(DateTime? value) =>
        value is { } actual ? new DateTimeOffset(DateTime.SpecifyKind(actual, DateTimeKind.Utc)) : null;

    private static EventDelivery Map(IDictionary<string, object?> row) => new()
    {
        EventId = Text(row, "event_id") ?? string.Empty,
        SubscriptionId = Text(row, "subscription_id") ?? string.Empty,
        TenantId = Text(row, "tenant_id") ?? "default",
        Status = Text(row, "status") ?? EventDeliveryStatus.Pending,
        Attempts = Number(row, "attempts") ?? 0,
        LastError = Text(row, "last_error"),
        NextAttemptAt = NullableDate(row, "next_attempt_at"),
        CreatedAt = Date(row, "created_at"),
        UpdatedAt = Date(row, "updated_at"),
        DeliveredAt = NullableDate(row, "delivered_at"),
    };
}
