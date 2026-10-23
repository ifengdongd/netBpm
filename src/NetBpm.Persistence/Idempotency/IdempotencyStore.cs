using System.Collections.Concurrent;
using Dapper;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Idempotency;

/// <summary>
/// 幂等记录：一个 <c>Idempotency-Key</c> 对应一次请求的响应快照。
/// <para>
/// <see cref="StatusCode"/> 为 <see langword="null"/> 表示「已占位、仍在处理」——
/// 并发的第二个同 key 请求会拿到 409，而不是重复执行。
/// </para>
/// </summary>
/// <param name="Key">客户端提供的幂等键。</param>
/// <param name="RequestHash">方法 + 路径 + 请求体的 SHA-256（防止同 key 不同请求）。</param>
/// <param name="StatusCode">已缓存的响应状态码；未完成时为 <see langword="null"/>。</param>
/// <param name="ContentType">已缓存的响应 Content-Type。</param>
/// <param name="ResponseBody">已缓存的响应体文本。</param>
/// <param name="CreatedAt">首次占位时间（UTC）。</param>
/// <param name="ExpiresAt">过期时间（UTC）。</param>
public sealed record IdempotencyRecord(
    string Key,
    string RequestHash,
    int? StatusCode,
    string? ContentType,
    string? ResponseBody,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>
/// 幂等存储。HTTP 层只依赖这个端口，宿主可选内存实现（单进程）或数据库实现（多实例）。
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>按 key 查记录；不存在返回 <see langword="null"/>。</summary>
    ValueTask<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// 原子占位：key 不存在则插入并返回 <see langword="true"/>；已存在返回 <see langword="false"/>。
    /// </summary>
    ValueTask<bool> TryBeginAsync(IdempotencyRecord record, CancellationToken ct = default);

    /// <summary>写入最终响应（只对成功占位的 key 生效）。</summary>
    ValueTask CompleteAsync(
        string key,
        int statusCode,
        string? contentType,
        string? responseBody,
        CancellationToken ct = default);

    /// <summary>删除记录（下游抛异常时回滚占位，允许客户端重试）。</summary>
    ValueTask DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>删除过期记录（由后台清理或测试调用）。</summary>
    ValueTask<int> PurgeExpiredAsync(DateTime utcNow, CancellationToken ct = default);
}

/// <summary>单进程幂等存储（开发 / 单实例部署）。</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return ValueTask.FromResult(_records.GetValueOrDefault(key));
    }

    /// <inheritdoc />
    public ValueTask<bool> TryBeginAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return ValueTask.FromResult(_records.TryAdd(record.Key, record));
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(
        string key,
        int statusCode,
        string? contentType,
        string? responseBody,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_records.TryGetValue(key, out var existing))
        {
            var updated = existing with
            {
                StatusCode = statusCode,
                ContentType = contentType,
                ResponseBody = responseBody,
            };

            _records.TryUpdate(key, updated, existing);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _records.TryRemove(key, out _);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeExpiredAsync(DateTime utcNow, CancellationToken ct = default)
    {
        var expired = _records
            .Where(pair => pair.Value.ExpiresAt <= utcNow)
            .Select(pair => pair.Key)
            .ToList();

        var removed = expired.Count(key => _records.TryRemove(key, out _));

        return ValueTask.FromResult(removed);
    }
}

/// <summary>
/// 数据库幂等存储（多实例部署）。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// </summary>
public sealed class DapperIdempotencyStore : IIdempotencyStore
{
    private readonly IDbSessionProvider _sessions;

    public DapperIdempotencyStore(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection
            .QueryAsync(new CommandDefinition(
                SelectSql,
                new { key },
                lease.Session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        foreach (IDictionary<string, object?> row in rows)
        {
            return Map(row);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryBeginAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var lease = _sessions.Acquire();

        try
        {
            await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
                InsertSql,
                new
                {
                    key = record.Key,
                    requestHash = record.RequestHash,
                    createdAt = record.CreatedAt,
                    expiresAt = record.ExpiresAt,
                },
                lease.Session.Transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception)
        {
            if (IsUniqueViolation(exception))
            {
                return false;
            }

            // 部分提供程序不暴露 SqlState 23000：以「键已存在」兜底判定，避免把并发误报成 500
            var existing = await FindAsync(record.Key, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                return false;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(
        string key,
        int statusCode,
        string? contentType,
        string? responseBody,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateSql,
            new { key, statusCode, contentType, responseBody },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteSql,
            new { key },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> PurgeExpiredAsync(DateTime utcNow, CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteExpiredSql,
            new { utcNow },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static IdempotencyRecord Map(IDictionary<string, object?> row) =>
        new(
            Text(row, "IDEMPOTENCY_KEY_")!,
            Text(row, "REQUEST_HASH_")!,
            Number(row, "STATUS_CODE_"),
            Text(row, "CONTENT_TYPE_"),
            Text(row, "RESPONSE_BODY_"),
            Date(row, "CREATED_AT_"),
            Date(row, "EXPIRES_AT_"));

    /// <summary>SQLSTATE 23000 = 完整性约束冲突（MySQL 1062 / SQL Server 2627、2601 均映射到它）。</summary>
    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Data.Common.DbException { SqlState: "23000" })
            {
                return true;
            }
        }

        return false;
    }

    private const string SelectSql = """
        select IDEMPOTENCY_KEY_, REQUEST_HASH_, STATUS_CODE_, CONTENT_TYPE_, RESPONSE_BODY_,
               CREATED_AT_, EXPIRES_AT_
        from ACT_IDEMPOTENCY where IDEMPOTENCY_KEY_ = @key
        """;

    private const string InsertSql = """
        insert into ACT_IDEMPOTENCY
            (IDEMPOTENCY_KEY_, REQUEST_HASH_, STATUS_CODE_, CONTENT_TYPE_, RESPONSE_BODY_, CREATED_AT_, EXPIRES_AT_)
        values (@key, @requestHash, null, null, null, @createdAt, @expiresAt)
        """;

    private const string UpdateSql = """
        update ACT_IDEMPOTENCY
        set STATUS_CODE_ = @statusCode, CONTENT_TYPE_ = @contentType, RESPONSE_BODY_ = @responseBody
        where IDEMPOTENCY_KEY_ = @key
        """;

    private const string DeleteSql = "delete from ACT_IDEMPOTENCY where IDEMPOTENCY_KEY_ = @key";

    private const string DeleteExpiredSql = "delete from ACT_IDEMPOTENCY where EXPIRES_AT_ <= @utcNow";
}
