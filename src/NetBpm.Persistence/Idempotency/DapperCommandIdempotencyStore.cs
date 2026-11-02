using Dapper;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Idempotency;

/// <summary>
/// 命令级幂等的数据库实现（<c>ACT_CMD_IDEMPOTENCY</c>；多实例部署下跨进程生效）。
/// </summary>
/// <remarks>
/// <para>
/// 与 HTTP 幂等表（<c>ACT_IDEMPOTENCY</c>）分开建表的原因：
/// 两者的键空间、生命周期与清理策略都不同 —— HTTP 键由客户端给、按响应体回放；
/// 命令键由调用方给、只需记录「执行过」。混在一张表里会让清理与审计互相干扰。
/// </para>
/// <para>
/// <b>同事务保证</b>：所有语句都走 <see cref="IDbSessionProvider.Acquire"/>，
/// 命令内拿到的是命令自己的会话，因此占位行与业务写入一起提交 / 一起回滚。
/// </para>
/// </remarks>
public sealed class DapperCommandIdempotencyStore : ICommandIdempotencyStore
{
    private readonly IDbSessionProvider _sessions;
    private readonly IIdGenerator _idGenerator;

    public DapperCommandIdempotencyStore(IDbSessionProvider sessions, IIdGenerator idGenerator)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(idGenerator);

        _sessions = sessions;
        _idGenerator = idGenerator;
    }

    /// <inheritdoc />
    public async ValueTask<CommandExecutionRecord?> FindAsync(string key, CancellationToken ct = default)
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
    public async ValueTask<bool> TryRecordAsync(CommandExecutionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var lease = _sessions.Acquire();

        try
        {
            await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
                InsertSql,
                new
                {
                    id = _idGenerator.NextId(),
                    key = record.Key,
                    commandName = record.CommandName,
                    resultPayload = record.ResultPayload,
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

            // 与 HTTP 幂等同样的兜底：拿不到 SQLSTATE 时以「键已存在」判定，
            // 避免把一次并发重复误报成 500。
            var existing = await FindAsync(record.Key, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                return false;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(string key, string resultPayload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        using var lease = _sessions.Acquire();

        // 同一个会话 = 同一个事务：快照与业务写入一起提交 / 一起回滚
        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateSql,
            new { key, resultPayload = resultPayload ?? string.Empty },
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

    private static CommandExecutionRecord Map(IDictionary<string, object?> row) =>
        new(
            Text(row, "COMMAND_KEY_")!,
            Text(row, "COMMAND_NAME_")!,
            Text(row, "RESULT_PAYLOAD_") ?? string.Empty,
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
        select COMMAND_KEY_, COMMAND_NAME_, RESULT_PAYLOAD_, CREATED_AT_, EXPIRES_AT_
        from ACT_CMD_IDEMPOTENCY where COMMAND_KEY_ = @key
        """;

    private const string InsertSql = """
        insert into ACT_CMD_IDEMPOTENCY
            (ID_, COMMAND_KEY_, COMMAND_NAME_, RESULT_PAYLOAD_, CREATED_AT_, EXPIRES_AT_)
        values (@id, @key, @commandName, @resultPayload, @createdAt, @expiresAt)
        """;

    private const string UpdateSql = """
        update ACT_CMD_IDEMPOTENCY set RESULT_PAYLOAD_ = @resultPayload where COMMAND_KEY_ = @key
        """;

    private const string DeleteSql = "delete from ACT_CMD_IDEMPOTENCY where COMMAND_KEY_ = @key";
}
