using System.Text.Json;
using Dapper;
using NetBpm.Engine.Events;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Events;

/// <summary>
/// 事件订阅存储（<c>nb_event_subscription</c>）。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// <para>
/// 与 outbox 刻意<b>不共用事务</b>：订阅是配置数据，改它的请求与流程命令不在同一个事务里，
/// 也不需要（订阅改错可以再改，事件写错会丢通知）。
/// </para>
/// </summary>
public sealed class DapperEventSubscriptionStore : IEventSubscriptionStore
{
    private const string Columns =
        "id, tenant_id, name, event_types, process_definition_id, endpoint_url, secret, headers_json,"
        + " enabled, max_attempts, max_per_minute, created_at, updated_at";

    private readonly IDbSessionProvider _sessions;

    public DapperEventSubscriptionStore(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask<EventSubscription> CreateAsync(
        EventSubscription subscription,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (string.IsNullOrWhiteSpace(subscription.Id))
        {
            subscription.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(subscription.Secret))
        {
            subscription.Secret = EventSubscriptionSecret.New();
        }

        var now = DateTime.UtcNow;
        subscription.CreatedAt = subscription.CreatedAt == default ? now : subscription.CreatedAt;
        subscription.UpdatedAt = now;

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            $"""
             insert into nb_event_subscription
                 ({Columns})
             values
                 (@id, @tenantId, @name, @eventTypes, @processDefinitionId, @endpointUrl, @secret, @headers,
                  @enabled, @maxAttempts, @maxPerMinute, @createdAt, @updatedAt)
             """,
            ToParameters(subscription),
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return subscription;
    }

    /// <inheritdoc />
    public async ValueTask<EventSubscription?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var lease = _sessions.Acquire();

        var row = await lease.Session.Connection.QueryFirstOrDefaultAsync(new CommandDefinition(
            $"select {Columns} from nb_event_subscription where id = @id",
            new { id },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : Map((IDictionary<string, object?>)row);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<EventSubscription>> FindAsync(
        EventSubscriptionQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();
        var session = lease.Session;

        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (query.TenantId is { Length: > 0 } tenantId)
        {
            conditions.Add("tenant_id = @tenantId");
            parameters.Add("tenantId", tenantId);
        }

        if (query.Enabled is { } enabled)
        {
            conditions.Add("enabled = @enabled");
            parameters.Add("enabled", enabled ? 1 : 0);
        }

        var where = conditions.Count > 0 ? " where " + string.Join(" and ", conditions) : string.Empty;
        var paging = SqlPaging.Build(session.DatabaseType, 0, Math.Max(1, query.MaxResults), parameters);

        var rows = await session.Connection.QueryAsync(new CommandDefinition(
            $"select {Columns} from nb_event_subscription{where} order by tenant_id, name, id{paging}",
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(row => Map((IDictionary<string, object?>)row)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<bool> UpdateAsync(EventSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription.Id);

        subscription.UpdatedAt = DateTime.UtcNow;

        if (subscription.CreatedAt == default)
        {
            // 调用方从列表页拿到的对象可能没有带出创建时间：以库里的为准，别把它写成 0001-01-01
            var existing = await FindByIdAsync(subscription.Id, ct).ConfigureAwait(false);

            if (existing is null)
            {
                return false;
            }

            subscription.CreatedAt = existing.CreatedAt;
        }

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            """
            update nb_event_subscription
               set tenant_id = @tenantId,
                   name = @name,
                   event_types = @eventTypes,
                   process_definition_id = @processDefinitionId,
                   endpoint_url = @endpointUrl,
                   secret = @secret,
                   headers_json = @headers,
                   enabled = @enabled,
                   max_attempts = @maxAttempts,
                   max_per_minute = @maxPerMinute,
                   created_at = @createdAt,
                   updated_at = @updatedAt
             where id = @id
            """,
            ToParameters(subscription),
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_event_subscription where id = @id",
            new { id },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            "update nb_event_subscription set enabled = @enabled, updated_at = @now where id = @id",
            new { id, enabled = enabled ? 1 : 0, now = DateTime.UtcNow },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    private static object ToParameters(EventSubscription subscription) => new
    {
        id = subscription.Id,
        tenantId = string.IsNullOrWhiteSpace(subscription.TenantId) ? "default" : subscription.TenantId,
        name = subscription.Name,
        eventTypes = string.Join(',', subscription.EventTypes ?? []),
        processDefinitionId = subscription.ProcessDefinitionId,
        endpointUrl = subscription.EndpointUrl,
        secret = subscription.Secret,
        headers = subscription.Headers is { Count: > 0 } headers
            ? JsonSerializer.Serialize(headers)
            : null,
        enabled = subscription.Enabled ? 1 : 0,
        maxAttempts = subscription.MaxAttempts > 0 ? subscription.MaxAttempts : 5,
        maxPerMinute = subscription.MaxPerMinute > 0 ? subscription.MaxPerMinute : 0,
        createdAt = subscription.CreatedAt,
        updatedAt = subscription.UpdatedAt,
    };

    private static EventSubscription Map(IDictionary<string, object?> row) => new()
    {
        Id = Text(row, "id") ?? string.Empty,
        TenantId = Text(row, "tenant_id") ?? "default",
        Name = Text(row, "name") ?? string.Empty,
        EventTypes = SplitTypes(Text(row, "event_types")),
        ProcessDefinitionId = Text(row, "process_definition_id"),
        EndpointUrl = Text(row, "endpoint_url") ?? string.Empty,
        Secret = Text(row, "secret") ?? string.Empty,
        Headers = ParseHeaders(Text(row, "headers_json")),
        Enabled = (Number(row, "enabled") ?? 0) != 0,
        MaxAttempts = Number(row, "max_attempts") ?? 5,
        MaxPerMinute = Number(row, "max_per_minute") ?? 0,
        CreatedAt = Date(row, "created_at"),
        UpdatedAt = Date(row, "updated_at"),
    };

    private static string[] SplitTypes(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> ParseHeaders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
