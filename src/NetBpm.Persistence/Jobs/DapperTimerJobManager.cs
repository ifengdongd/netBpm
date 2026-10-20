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

        var rows = await lease.Session.Connection
            .QueryAsync(new CommandDefinition(
                SelectDueSql,
                new { utcNow },
                lease.Session.Transaction,
                cancellationToken: ct))
            .ConfigureAwait(false);

        var jobs = new List<TimerJobEntity>();

        foreach (IDictionary<string, object?> row in rows)
        {
            jobs.Add(Map(row));

            if (jobs.Count >= Math.Max(1, maxCount))
            {
                break;
            }
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

    private const string Columns = """
        ID_, REV_, TYPE_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_,
        ACTIVITY_ID_, BOUNDARY_EVENT_ID_, DUEDATE_, TENANT_ID_, CREATE_TIME_
        """;

    private const string SelectByIdSql = $"""
        select {Columns}
        from ACT_RU_TIMER_JOB
        where ID_ = @id
        """;

    private const string SelectDueSql = $"""
        select {Columns}
        from ACT_RU_TIMER_JOB
        where DUEDATE_ <= @utcNow
        order by DUEDATE_
        """;

    private const string InsertSql = """
        insert into ACT_RU_TIMER_JOB
            (ID_, REV_, TYPE_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_,
             ACTIVITY_ID_, BOUNDARY_EVENT_ID_, DUEDATE_, TENANT_ID_, CREATE_TIME_)
        values (@id, @revision, @type, @executionId, @processInstanceId, @processDefinitionId,
                @activityId, @boundaryEventId, @dueDate, @tenantId, @createTime)
        """;

    private const string DeleteByIdSql = "delete from ACT_RU_TIMER_JOB where ID_ = @id";

    private const string DeleteByExecutionSql = "delete from ACT_RU_TIMER_JOB where EXECUTION_ID_ = @executionId";

    private const string DeleteByProcessInstanceSql =
        "delete from ACT_RU_TIMER_JOB where PROCESS_INSTANCE_ID_ = @processInstanceId";

    private const string CountSql = "select count(*) from ACT_RU_TIMER_JOB";
}
