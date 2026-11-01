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
