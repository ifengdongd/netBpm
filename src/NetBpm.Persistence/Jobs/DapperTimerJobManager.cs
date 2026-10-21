using Dapper;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Database;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 数据库定时作业存储。表结构见 <c>db/{dialect}/0006_create_timer_job.sql</c>。
/// <para>
/// 与其它仓储一致：语句全部带 <c>session.Transaction</c>（命令内与命令同事务；命令外由会话自己提交）。
/// 到点查询不做方言分页：<c>where DUEDATE_ &lt;= @utcNow order by DUEDATE_</c>，
/// 取前 N 条在内存完成（到期作业通常只有几条）。
/// </para>
/// </summary>
public sealed class DapperTimerJobManager : ITimerJobManager
{
    private readonly IDbSessionProvider _sessions;

    public DapperTimerJobManager(IDbSessionProvider sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public async ValueTask InsertAsync(TimerJobEntity job, CancellationToken ct = default)
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
                type = job.Type,
                executionId = job.ExecutionId,
                processInstanceId = job.ProcessInstanceId,
                processDefinitionId = job.ProcessDefinitionId,
                activityId = job.ActivityId,
                boundaryEventId = job.BoundaryEventId,
                dueDate = job.DueDate,
                tenantId = job.TenantId ?? string.Empty,
                createTime = job.CreateTime,
            },
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<TimerJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection
            .QueryAsync(new CommandDefinition(
                SelectByIdSql,
                new { id = jobId },
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
    public async ValueTask<IReadOnlyList<TimerJobEntity>> FindDueAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();
        var session = lease.Session;
        var take = Math.Max(1, maxCount);

        // 到期作业在 SQL 里分页：到期风暴时不再把全部行拉进内存。
        // 挂起的实例由 left join 过滤（执行行缺失的孤儿作业仍返回，由命令负责清理）。
        var sql = session.DatabaseType switch
        {
            DatabaseType.MySql => $"""
                select j.*
                from ACT_RU_TIMER_JOB j
                left join ACT_RU_EXECUTION e on e.ID_ = j.EXECUTION_ID_
                where j.DUEDATE_ <= @utcNow
                  and j.DEAD_LETTER_ = 0
                  and (e.ID_ is null or e.SUSPENSION_STATE_ = 1)
                order by j.DUEDATE_, j.ID_
                limit @take
                """,

            DatabaseType.SqlServer => $"""
                select top (@take) j.*
                from ACT_RU_TIMER_JOB j
                left join ACT_RU_EXECUTION e on e.ID_ = j.EXECUTION_ID_
                where j.DUEDATE_ <= @utcNow
                  and j.DEAD_LETTER_ = 0
                  and (e.ID_ is null or e.SUSPENSION_STATE_ = 1)
                order by j.DUEDATE_, j.ID_
                """,

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        var rows = await session.Connection
            .QueryAsync(new CommandDefinition(
                sql,
                new { utcNow, take },
                session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var jobs = new List<TimerJobEntity>();

        foreach (IDictionary<string, object?> row in rows)
        {
            jobs.Add(Map(row));
        }

        return jobs;
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
    public async ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        using var lease = _sessions.Acquire();

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountSql,
            transaction: lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
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

        // 单条 UPDATE 完成「递减 + 死信判定」：先按当前重试次数算出新值，避免依赖列赋值顺序。
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
    public async ValueTask<IReadOnlyList<TimerJobEntity>> FindDeadLettersAsync(
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
                from ACT_RU_TIMER_JOB
                where DEAD_LETTER_ = 1
                order by LAST_FAILURE_ desc, ID_
                limit @take
                """,

            DatabaseType.SqlServer => $"""
                select top (@take) {Columns}
                from ACT_RU_TIMER_JOB
                where DEAD_LETTER_ = 1
                order by LAST_FAILURE_ desc, ID_
                """,

            _ => throw new NotSupportedException($"不支持的数据库方言：{session.DatabaseType}"),
        };

        var rows = await session.Connection.QueryAsync(new CommandDefinition(
            sql,
            new { take },
            session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var result = new List<TimerJobEntity>();

        foreach (IDictionary<string, object?> row in rows)
        {
            result.Add(Map(row));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetryDeadLetterAsync(string jobId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var lease = _sessions.Acquire();

        var rows = await lease.Session.Connection.ExecuteAsync(new CommandDefinition(
            RetryDeadLetterSql,
            new { id = jobId, now = nowUtc, retries = TimerJobRetry.DefaultMaxRetries },
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

    private static TimerJobEntity Map(IDictionary<string, object?> row) =>
        new()
        {
            Id = Text(row, "ID_") ?? string.Empty,
            Revision = Number(row, "REV_") ?? 0,
            Type = Text(row, "TYPE_") ?? "timer",
            ExecutionId = Text(row, "EXECUTION_ID_") ?? string.Empty,
            ProcessInstanceId = Text(row, "PROCESS_INSTANCE_ID_") ?? string.Empty,
            ProcessDefinitionId = Text(row, "PROC_DEF_ID_"),
            ActivityId = Text(row, "ACTIVITY_ID_") ?? string.Empty,
            BoundaryEventId = Text(row, "BOUNDARY_EVENT_ID_") ?? string.Empty,
            DueDate = Date(row, "DUEDATE_"),
            TenantId = Text(row, "TENANT_ID_"),
            CreateTime = Date(row, "CREATE_TIME_"),
            Retries = Number(row, "RETRIES_") ?? TimerJobRetry.DefaultMaxRetries,
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
        ID_, REV_, TYPE_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_,
        ACTIVITY_ID_, BOUNDARY_EVENT_ID_, DUEDATE_, TENANT_ID_, CREATE_TIME_,
        RETRIES_, DEAD_LETTER_, EXCEPTION_MSG_, LAST_FAILURE_
        """;

    private const string SelectByIdSql = $"""
        select {Columns}
        from ACT_RU_TIMER_JOB
        where ID_ = @id
        """;

    private const string InsertSql = """
        insert into ACT_RU_TIMER_JOB
            (ID_, REV_, TYPE_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_,
             ACTIVITY_ID_, BOUNDARY_EVENT_ID_, DUEDATE_, TENANT_ID_, CREATE_TIME_)
        values (@id, @revision, @type, @executionId, @processInstanceId, @processDefinitionId,
                @activityId, @boundaryEventId, @dueDate, @tenantId, @createTime)
        """;

    private const string UpdateDueDateSql =
        "update ACT_RU_TIMER_JOB set DUEDATE_ = @dueDate where ID_ = @id";

    private const string UpdateFailureSql = """
        update ACT_RU_TIMER_JOB
        set RETRIES_ = case when coalesce(RETRIES_, 3) > 0 then coalesce(RETRIES_, 3) - 1 else 0 end,
            DEAD_LETTER_ = case when coalesce(RETRIES_, 3) > 1 then 0 else 1 end,
            EXCEPTION_MSG_ = @error,
            LAST_FAILURE_ = @now,
            DUEDATE_ = @nextDue
        where ID_ = @id
        """;

    private const string RetryDeadLetterSql = """
        update ACT_RU_TIMER_JOB
        set RETRIES_ = @retries, DEAD_LETTER_ = 0, EXCEPTION_MSG_ = null, DUEDATE_ = @now
        where ID_ = @id and DEAD_LETTER_ = 1
        """;

    private const string DeleteDeadLetterSql =
        "delete from ACT_RU_TIMER_JOB where ID_ = @id and DEAD_LETTER_ = 1";

    private const string DeleteByIdSql = "delete from ACT_RU_TIMER_JOB where ID_ = @id";

    private const string DeleteByExecutionSql = "delete from ACT_RU_TIMER_JOB where EXECUTION_ID_ = @executionId";

    private const string DeleteByProcessInstanceSql =
        "delete from ACT_RU_TIMER_JOB where PROCESS_INSTANCE_ID_ = @processInstanceId";

    private const string CountSql = "select count(*) from ACT_RU_TIMER_JOB";
}
