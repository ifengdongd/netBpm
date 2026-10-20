using System.Globalization;
using System.Text.Json;
using Dapper;
using NetBpm.Engine.Migration;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Migration;

/// <summary>
/// <see cref="IMigrationLog"/> 的 Dapper 实现（<c>ACT_HI_MIGRATION</c>）。
/// <para>
/// 活动映射以 JSON 存储（源节点 id → 目标节点 id）：列宽 4000，普通审批流程的映射远小于上限；
/// 查询时按实例倒序返回，用于「这个实例被迁过几次、从哪版到哪版」的追溯。
/// </para>
/// </summary>
public sealed class DapperMigrationLog : IMigrationLog
{
    private const string InsertSql = """
        insert into ACT_HI_MIGRATION
            (ID_, PROC_INST_ID_, SOURCE_PROC_DEF_ID_, TARGET_PROC_DEF_ID_,
             ACTIVITY_MAPPINGS_, REASON_, USER_ID_, MIGRATED_AT_)
        values
            (@id, @processInstanceId, @sourceProcessDefinitionId, @targetProcessDefinitionId,
             @activityMappings, @reason, @userId, @migratedAt)
        """;

    private const string SelectByProcessInstanceSql = """
        select ID_, PROC_INST_ID_, SOURCE_PROC_DEF_ID_, TARGET_PROC_DEF_ID_,
               ACTIVITY_MAPPINGS_, REASON_, USER_ID_, MIGRATED_AT_
        from ACT_HI_MIGRATION
        where PROC_INST_ID_ = @processInstanceId
        order by MIGRATED_AT_ desc, ID_ desc
        """;

    private readonly IDbSessionProvider _sessions;

    public DapperMigrationLog(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask RecordAsync(MigrationRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Id);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = record.Id,
                processInstanceId = record.ProcessInstanceId,
                sourceProcessDefinitionId = record.SourceProcessDefinitionId,
                targetProcessDefinitionId = record.TargetProcessDefinitionId,
                activityMappings = JsonSerializer.Serialize(record.ActivityMappings),
                reason = Truncate(record.Reason, 400),
                userId = Truncate(record.UserId, 255),
                migratedAt = record.MigratedAt,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MigrationRecord>> FindByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection
            .QueryAsync(new CommandDefinition(
                SelectByProcessInstanceSql,
                new { processInstanceId },
                lease.Session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var records = new List<MigrationRecord>();

        foreach (IDictionary<string, object?> row in rows)
        {
            records.Add(Map(row));
        }

        return records;
    }

    private static MigrationRecord Map(IDictionary<string, object?> row)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = row["ACTIVITY_MAPPINGS_"] as string;

        if (!string.IsNullOrWhiteSpace(json))
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (parsed is not null)
            {
                foreach (var (from, to) in parsed)
                {
                    mappings[from] = to;
                }
            }
        }

        return new MigrationRecord(
            Convert.ToString(row["ID_"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row["PROC_INST_ID_"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row["SOURCE_PROC_DEF_ID_"], CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(row["TARGET_PROC_DEF_ID_"], CultureInfo.InvariantCulture) ?? string.Empty,
            mappings,
            row["REASON_"] as string,
            row["USER_ID_"] as string,
            ToUtc(row["MIGRATED_AT_"]));
    }

    private static DateTime ToUtc(object? value) => value switch
    {
        null => DateTime.UtcNow,
        DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
        DateTimeOffset offset => offset.UtcDateTime,
        _ => DateTime.SpecifyKind(
            Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            DateTimeKind.Utc),
    };

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
