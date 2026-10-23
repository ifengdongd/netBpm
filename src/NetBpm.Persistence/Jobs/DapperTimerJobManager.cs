using Dapper;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 数据库定时作业存储。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// <para>
/// 与其它仓储一致：语句全部带 <c>session.Transaction</c>（命令内与命令同事务；命令外由会话自己提交）。
/// 到点查询不做方言分页：<c>where DUEDATE_ &lt;= @utcNow order by DUEDATE_</c>，
/// 取前 N 条在内存完成（到期作业通常只有几条）。
/// </para>
/// </summary>
public sealed class DapperTimerJobManager : DapperJobStoreBase, ITimerJobManager
{
    public DapperTimerJobManager(IDbSessionProvider sessions)
        : base(sessions)
    {
    }

    /// <inheritdoc />
    protected override string Table => "ACT_RU_TIMER_JOB";

    /// <inheritdoc />
    protected override int DefaultMaxRetries => TimerJobRetry.DefaultMaxRetries;

    /// <inheritdoc />
    public async ValueTask InsertAsync(TimerJobEntity job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);

        using var lease = Sessions.Acquire();

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

        using var lease = Sessions.Acquire();

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
        using var lease = Sessions.Acquire();
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
    public ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default) =>
        TryDeleteCoreAsync(jobId, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default) =>
        DeleteByExecutionCoreAsync(executionId, ct);

    /// <inheritdoc />
    public ValueTask<bool> TryAcquireAsync(
        string jobId,
        string owner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken ct = default) =>
        TryAcquireCoreAsync(jobId, owner, nowUtc, leaseDuration, ct);

    /// <inheritdoc />
    public ValueTask<bool> RenewAsync(
        string jobId,
        string owner,
        DateTime nowUtc,
        TimeSpan leaseDuration,
        CancellationToken ct = default) =>
        RenewCoreAsync(jobId, owner, nowUtc, leaseDuration, ct);

    /// <inheritdoc />
    public ValueTask<bool> ReleaseAsync(string jobId, string owner, CancellationToken ct = default) =>
        ReleaseCoreAsync(jobId, owner, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        DeleteByProcessInstanceCoreAsync(processInstanceId, ct);

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken ct = default) => CountCoreAsync(ct);

    /// <inheritdoc />
    public ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default) =>
        RescheduleCoreAsync(jobId, dueDate, ct);

    /// <inheritdoc />
    public ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default) =>
        MarkFailureCoreAsync(jobId, reason, nowUtc, nextDueUtc, ct);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TimerJobEntity>> FindDeadLettersAsync(
        int maxCount,
        CancellationToken ct = default)
    {
        using var lease = Sessions.Acquire();
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
    public ValueTask<bool> RetryDeadLetterAsync(
        string jobId,
        DateTime nowUtc,
        CancellationToken ct = default) =>
        RetryDeadLetterCoreAsync(jobId, nowUtc, ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default) =>
        DeleteDeadLetterCoreAsync(jobId, ct);

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

    private const string Columns = """
        ID_, REV_, TYPE_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_,
        ACTIVITY_ID_, BOUNDARY_EVENT_ID_, DUEDATE_, TENANT_ID_, CREATE_TIME_,
        RETRIES_, DEAD_LETTER_, EXCEPTION_MSG_, LAST_FAILURE_,
        LOCK_OWNER_, LOCK_EXP_TIME_
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

}
