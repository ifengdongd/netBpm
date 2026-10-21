using Dapper;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 数据库异步作业存储。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// <para>
/// 到期查询在 SQL 里完成过滤与分页，并用 left join 排除挂起实例的作业；
/// 执行时以 <c>delete ... where ID_ = @id</c> 的影响行数做抢占（与定时作业同构）。
/// </para>
/// </summary>
public sealed class DapperAsyncJobManager : IAsyncJobManager
{
    private readonly IDbSessionProvider _sessions;

    public DapperAsyncJobManager(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask InsertAsync(AsyncJobEntity job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            InsertSql,
            new
            {
                id = job.Id,
                revision = job.Revision,
                executionId = job.ExecutionId,
                processInstanceId = job.ProcessInstanceId,
                processDefinitionId = job.ProcessDefinitionId,
                activityId = job.ActivityId,
                phase = job.PhaseValue,
                dueDate = job.DueDate,
                tenantId = job.TenantId ?? string.Empty,
                createTime = job.CreateTime,
                retries = job.Retries,
                deadLetter = job.DeadLetter ? 1 : 0,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AsyncJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.QueryAsync(new CommandDefinition(
            SelectByIdSql,
            new { id = jobId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        foreach (IDictionary<string, object?> row in rows)
        {
            return Map(row);
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AsyncJobEntity>> FindDueAsync(
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
                select j.*
                from ACT_RU_JOB j
                left join ACT_RU_EXECUTION e on e.ID_ = j.EXECUTION_ID_
                where j.DUEDATE_ <= @utcNow
                  and j.DEAD_LETTER_ = 0
                  and (e.ID_ is null or e.SUSPENSION_STATE_ = 1)
                order by j.DUEDATE_, j.ID_
                limit @take
                """,

            DatabaseType.SqlServer => $"""
                select top (@take) j.*
                from ACT_RU_JOB j
                left join ACT_RU_EXECUTION e on e.ID_ = j.EXECUTION_ID_
                where j.DUEDATE_ <= @utcNow
                  and j.DEAD_LETTER_ = 0
                  and (e.ID_ is null or e.SUSPENSION_STATE_ = 1)
                order by j.DUEDATE_, j.ID_
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
    public async ValueTask<IReadOnlyList<AsyncJobEntity>> FindAsync(
        AsyncJobQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var (where, parameters) = BuildWhere(query);

        parameters.Add("skip", Math.Max(0, query.FirstResult));
        parameters.Add("take", Math.Max(1, query.MaxResults));

        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql =>
                $"select {Columns} from ACT_RU_JOB{where} order by CREATE_TIME_, ID_ limit @take offset @skip",
            DatabaseType.SqlServer =>
                $"select {Columns} from ACT_RU_JOB{where} order by CREATE_TIME_, ID_ offset @skip rows fetch next @take rows only",
            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        var rows = await session.Connection.QueryAsync(new CommandDefinition(
            sql,
            parameters,
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return MapAll(rows);
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(AsyncJobQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var lease = _sessions.Acquire();
        var (where, parameters) = BuildWhere(query);

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_JOB{where}",
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountSql,
            transaction: lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByIdSql,
            new { id = jobId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByExecutionSql,
            new { executionId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteByProcessInstanceSql,
            new { processInstanceId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateFailureSql,
            new
            {
                id = jobId,
                error = reason.Length > 1000 ? reason[..1000] : reason,
                now = nowUtc,
                nextDue = nextDueUtc,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryDeadLetterAsync(string jobId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            RetryDeadLetterSql,
            new { id = jobId, now = nowUtc, retries = AsyncJobRetry.DefaultMaxRetries },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            DeleteDeadLetterSql,
            new { id = jobId },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows == 1;
    }

    /// <inheritdoc />
    public async ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            UpdateDueDateSql,
            new { id = jobId, dueDate },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static (string Where, DynamicParameters Parameters) BuildWhere(AsyncJobQuery query)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.ProcessInstanceId))
        {
            clauses.Add("PROCESS_INSTANCE_ID_ = @processInstanceId");
            parameters.Add("processInstanceId", query.ProcessInstanceId.Trim());
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

    private static List<AsyncJobEntity> MapAll(IEnumerable<dynamic> rows)
    {
        var jobs = new List<AsyncJobEntity>();

        foreach (IDictionary<string, object?> row in rows)
        {
            jobs.Add(Map(row));
        }

        return jobs;
    }

    private static AsyncJobEntity Map(IDictionary<string, object?> row) =>
        new()
        {
            Id = Text(row, "ID_") ?? string.Empty,
            Revision = Number(row, "REV_") ?? 0,
            ExecutionId = Text(row, "EXECUTION_ID_") ?? string.Empty,
            ProcessInstanceId = Text(row, "PROCESS_INSTANCE_ID_") ?? string.Empty,
            ProcessDefinitionId = Text(row, "PROC_DEF_ID_"),
            ActivityId = Text(row, "ACTIVITY_ID_") ?? string.Empty,
            Phase = string.Equals(Text(row, "PHASE_"), "after", StringComparison.OrdinalIgnoreCase)
                ? AsyncJobPhase.After
                : AsyncJobPhase.Before,
            DueDate = Date(row, "DUEDATE_"),
            TenantId = Text(row, "TENANT_ID_"),
            CreateTime = Date(row, "CREATE_TIME_"),
            Retries = Number(row, "RETRIES_") ?? AsyncJobRetry.DefaultMaxRetries,
            DeadLetter = Number(row, "DEAD_LETTER_") is > 0,
            ExceptionMessage = Text(row, "EXCEPTION_MSG_"),
            LastFailureTime = NullableDate(row, "LAST_FAILURE_"),
        };

    private static string? Text(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull ? value as string : null;

    private static int? Number(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private static DateTime Date(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? DateTime.SpecifyKind(
                Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeKind.Utc)
            : DateTime.MinValue;

    private static DateTime? NullableDate(IDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not DBNull and not null
            ? DateTime.SpecifyKind(
                Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeKind.Utc)
            : null;

    private const string Columns = """
        ID_, REV_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, ACTIVITY_ID_, PHASE_,
        DUEDATE_, TENANT_ID_, CREATE_TIME_, RETRIES_, DEAD_LETTER_, EXCEPTION_MSG_, LAST_FAILURE_
        """;

    private const string SelectByIdSql = $"""
        select {Columns}
        from ACT_RU_JOB
        where ID_ = @id
        """;

    private const string InsertSql = """
        insert into ACT_RU_JOB
            (ID_, REV_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, ACTIVITY_ID_, PHASE_,
             DUEDATE_, TENANT_ID_, CREATE_TIME_, RETRIES_, DEAD_LETTER_)
        values (@id, @revision, @executionId, @processInstanceId, @processDefinitionId, @activityId, @phase,
                @dueDate, @tenantId, @createTime, @retries, @deadLetter)
        """;

    private const string UpdateDueDateSql =
        "update ACT_RU_JOB set DUEDATE_ = @dueDate where ID_ = @id";

    private const string UpdateFailureSql = """
        update ACT_RU_JOB
        set RETRIES_ = case when coalesce(RETRIES_, 3) > 0 then coalesce(RETRIES_, 3) - 1 else 0 end,
            DEAD_LETTER_ = case when coalesce(RETRIES_, 3) > 1 then 0 else 1 end,
            EXCEPTION_MSG_ = @error,
            LAST_FAILURE_ = @now,
            DUEDATE_ = @nextDue
        where ID_ = @id
        """;

    private const string RetryDeadLetterSql = """
        update ACT_RU_JOB
        set RETRIES_ = @retries, DEAD_LETTER_ = 0, EXCEPTION_MSG_ = null, DUEDATE_ = @now
        where ID_ = @id and DEAD_LETTER_ = 1
        """;

    private const string DeleteDeadLetterSql =
        "delete from ACT_RU_JOB where ID_ = @id and DEAD_LETTER_ = 1";

    private const string DeleteByIdSql = "delete from ACT_RU_JOB where ID_ = @id";

    private const string DeleteByExecutionSql = "delete from ACT_RU_JOB where EXECUTION_ID_ = @executionId";

    private const string DeleteByProcessInstanceSql =
        "delete from ACT_RU_JOB where PROCESS_INSTANCE_ID_ = @processInstanceId";

    private const string CountSql = "select count(*) from ACT_RU_JOB";
}
