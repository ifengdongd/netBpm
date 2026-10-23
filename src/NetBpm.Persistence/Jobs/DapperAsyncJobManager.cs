using Dapper;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Database;
using static NetBpm.Persistence.RowValues;

namespace NetBpm.Persistence.Jobs;

/// <summary>
/// 数据库异步作业存储。表结构见 <c>db/{dialect}/netbpm_init.sql</c>。
/// <para>
/// 到期查询在 SQL 里完成过滤与分页，并用 left join 排除挂起实例的作业；
/// 执行时以 <c>delete ... where ID_ = @id</c> 的影响行数做抢占（与定时作业同构）。
/// </para>
/// </summary>
public sealed class DapperAsyncJobManager : DapperJobStoreBase, IAsyncJobManager
{
    public DapperAsyncJobManager(IDbSessionProvider sessions)
        : base(sessions)
    {
    }

    /// <inheritdoc />
    protected override string Table => "ACT_RU_JOB";

    /// <inheritdoc />
    protected override int DefaultMaxRetries => AsyncJobRetry.DefaultMaxRetries;

    /// <inheritdoc />
    public async ValueTask InsertAsync(AsyncJobEntity job, CancellationToken ct = default)
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

        using var lease = Sessions.Acquire();

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
        using var lease = Sessions.Acquire();
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

        using var lease = Sessions.Acquire();
        var session = lease.Session;
        var (where, parameters) = BuildWhere(query);

        var paging = SqlPaging.Build(session.DatabaseType, query.FirstResult, query.MaxResults, parameters);
        var sql = $"select {Columns} from ACT_RU_JOB{where} order by CREATE_TIME_, ID_{paging}";

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

        using var lease = Sessions.Acquire();
        var (where, parameters) = BuildWhere(query);

        return await lease.Session.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_JOB{where}",
            parameters,
            lease.Session.Transaction,
            cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken ct = default) => CountCoreAsync(ct);

    /// <inheritdoc />
    public ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default) =>
        TryDeleteCoreAsync(jobId, ct);

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
    public ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default) =>
        DeleteByExecutionCoreAsync(executionId, ct);

    /// <inheritdoc />
    public ValueTask<int> DeleteByProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        DeleteByProcessInstanceCoreAsync(processInstanceId, ct);

    /// <inheritdoc />
    public ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default) =>
        MarkFailureCoreAsync(jobId, reason, nowUtc, nextDueUtc, ct);

    /// <inheritdoc />
    public ValueTask<bool> RetryDeadLetterAsync(
        string jobId,
        DateTime nowUtc,
        CancellationToken ct = default) =>
        RetryDeadLetterCoreAsync(jobId, nowUtc, ct);

    /// <inheritdoc />
    public ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default) =>
        DeleteDeadLetterCoreAsync(jobId, ct);

    /// <inheritdoc />
    public ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default) =>
        RescheduleCoreAsync(jobId, dueDate, ct);

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

    private const string Columns = """
        ID_, REV_, EXECUTION_ID_, PROCESS_INSTANCE_ID_, PROC_DEF_ID_, ACTIVITY_ID_, PHASE_,
        DUEDATE_, TENANT_ID_, CREATE_TIME_, RETRIES_, DEAD_LETTER_, EXCEPTION_MSG_, LAST_FAILURE_,
        LOCK_OWNER_, LOCK_EXP_TIME_
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

}
