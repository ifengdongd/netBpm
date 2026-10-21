using Dapper;
using NetBpm.Persistence;

namespace NetBpm.Admin.Storage;

/// <summary>
/// 定时作业（<c>ACT_RU_TIMER_JOB</c>）的只读查询与改期。
/// </summary>
/// <remarks>
/// <para>
/// 作业本身由引擎维护（插入 / 条件删除抢占），这里只做运维诊断：
/// 「哪些实例在等定时器、什么时候到期」，以及必要时把某条作业改期。
/// </para>
/// <para>改期只更新 <c>DUEDATE_</c>，不改 <c>REV_</c> / 状态；触发仍由宿主的轮询服务负责。</para>
/// </remarks>
public sealed class TimerJobStore
{
    private readonly IDbConnectionFactory _connections;

    public TimerJobStore(IDbConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
    }

    private const string Columns = """
        ID_                 as Id,
        PROCESS_INSTANCE_ID_ as ProcessInstanceId,
        PROC_DEF_ID_         as ProcessDefinitionId,
        ACTIVITY_ID_         as ActivityId,
        BOUNDARY_EVENT_ID_   as BoundaryEventId,
        DUEDATE_             as DueDate,
        TENANT_ID_           as TenantId,
        CREATE_TIME_         as CreateTime,
        RETRIES_             as Retries,
        DEAD_LETTER_         as DeadLetter,
        EXCEPTION_MSG_       as ExceptionMessage,
        LAST_FAILURE_        as LastFailureTime
        """;

    /// <summary>按条件分页查作业（到期时间升序）。</summary>
    public async Task<TimerJobPage> FindAsync(
        TimerJobFilter filter,
        int firstResult,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter);

        await using var connection = _connections.CreateConnection();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*) from ACT_RU_TIMER_JOB{where}",
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        if (total == 0)
        {
            return new TimerJobPage([], 0);
        }

        parameters.Add("Skip", Math.Max(0, firstResult));
        parameters.Add("Take", Math.Max(1, maxResults));

        var pageSql = _connections.DatabaseType == DatabaseType.SqlServer
            ? $"select {Columns} from ACT_RU_TIMER_JOB{where} order by DUEDATE_ asc, ID_ asc offset @Skip rows fetch next @Take rows only"
            : $"select {Columns} from ACT_RU_TIMER_JOB{where} order by DUEDATE_ asc, ID_ asc limit @Take offset @Skip";

        var rows = await connection.QueryAsync<TimerJobRow>(new CommandDefinition(
            pageSql,
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return new TimerJobPage(
            [.. rows.Select(row => new TimerJobSummary(
                row.Id,
                row.ProcessInstanceId,
                row.ProcessDefinitionId,
                row.ActivityId,
                row.BoundaryEventId,
                AsUtc(row.DueDate),
                row.TenantId,
                AsUtc(row.CreateTime),
                row.Retries ?? 0,
                row.DeadLetter == 1,
                row.ExceptionMessage,
                row.LastFailureTime is null ? null : AsUtc(row.LastFailureTime.Value)))],
            total);
    }

    /// <summary>按 id 查单条作业；不存在返回 <see langword="null"/>。</summary>
    public async Task<TimerJobSummary?> FindByIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var connection = _connections.CreateConnection();

        var row = await connection.QuerySingleOrDefaultAsync<TimerJobRow>(new CommandDefinition(
            $"select {Columns} from ACT_RU_TIMER_JOB where ID_ = @Id",
            new { Id = jobId },
            cancellationToken: ct)).ConfigureAwait(false);

        return row is null
            ? null
            : new TimerJobSummary(
                row.Id,
                row.ProcessInstanceId,
                row.ProcessDefinitionId,
                row.ActivityId,
                row.BoundaryEventId,
                AsUtc(row.DueDate),
                row.TenantId,
                AsUtc(row.CreateTime),
                row.Retries ?? 0,
                row.DeadLetter == 1,
                row.ExceptionMessage,
                row.LastFailureTime is null ? null : AsUtc(row.LastFailureTime.Value));
    }

    /// <summary>作业统计：总数、已到期、一小时内到期、最近到期时间。</summary>
    public async Task<TimerJobStats> GetStatsAsync(TimerJobFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (where, parameters) = BuildWhere(filter);
        var now = DateTime.UtcNow;

        parameters.Add("Now", now);
        parameters.Add("Soon", now.AddHours(1));

        await using var connection = _connections.CreateConnection();

        var stats = await connection.QuerySingleAsync<TimerJobStatsRow>(new CommandDefinition(
            $"""
             select count(*) as Total,
                    sum(case when DEAD_LETTER_ = 0 and DUEDATE_ <= @Now then 1 else 0 end) as Overdue,
                    sum(case when DEAD_LETTER_ = 0 and DUEDATE_ > @Now and DUEDATE_ <= @Soon then 1 else 0 end) as DueWithinHour,
                    sum(case when DEAD_LETTER_ = 1 then 1 else 0 end) as DeadLetter,
                    min(case when DEAD_LETTER_ = 0 then DUEDATE_ end) as NextDueAt
               from ACT_RU_TIMER_JOB{where}
             """,
            parameters,
            cancellationToken: ct)).ConfigureAwait(false);

        return new TimerJobStats(
            stats.Total,
            stats.Overdue ?? 0,
            stats.DueWithinHour ?? 0,
            stats.NextDueAt is null ? null : AsUtc(stats.NextDueAt.Value),
            stats.DeadLetter ?? 0);
    }

    /// <summary>改期：只更新到期时间；返回是否命中（作业可能已被触发 / 删除）。</summary>
    public async Task<bool> UpdateDueDateAsync(string jobId, DateTime dueDate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "update ACT_RU_TIMER_JOB set DUEDATE_ = @DueDate where ID_ = @Id",
            new { Id = jobId, DueDate = dueDate },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>人工重试死信作业：复位重试次数并立即到期；返回是否命中死信行。</summary>
    public async Task<bool> RetryDeadLetterAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            update ACT_RU_TIMER_JOB
            set RETRIES_ = 3, DEAD_LETTER_ = 0, EXCEPTION_MSG_ = null, DUEDATE_ = @Now
            where ID_ = @Id and DEAD_LETTER_ = 1
            """,
            new { Id = jobId, Now = DateTime.UtcNow },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    /// <summary>删除死信作业（确认不再重试）；普通作业不受影响，返回是否命中。</summary>
    public async Task<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using var connection = _connections.CreateConnection();

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "delete from ACT_RU_TIMER_JOB where ID_ = @Id and DEAD_LETTER_ = 1",
            new { Id = jobId },
            cancellationToken: ct)).ConfigureAwait(false);

        return affected == 1;
    }

    private static (string Where, DynamicParameters Parameters) BuildWhere(TimerJobFilter filter)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(filter.ProcessInstanceId))
        {
            clauses.Add("PROCESS_INSTANCE_ID_ = @ProcessInstanceId");
            parameters.Add("ProcessInstanceId", filter.ProcessInstanceId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.ProcessDefinitionId))
        {
            clauses.Add("PROC_DEF_ID_ = @ProcessDefinitionId");
            parameters.Add("ProcessDefinitionId", filter.ProcessDefinitionId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.TenantId))
        {
            clauses.Add("TENANT_ID_ = @TenantId");
            parameters.Add("TenantId", filter.TenantId.Trim());
        }

        if (filter.DueBefore is { } dueBefore)
        {
            clauses.Add("DUEDATE_ <= @DueBefore");
            parameters.Add("DueBefore", dueBefore);
        }

        if (filter.DeadLetter is { } deadLetter)
        {
            clauses.Add("DEAD_LETTER_ = @DeadLetter");
            parameters.Add("DeadLetter", deadLetter ? 1 : 0);
        }

        var where = clauses.Count == 0 ? string.Empty : " where " + string.Join(" and ", clauses);

        return (where, parameters);
    }

    private static DateTime AsUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class TimerJobRow
    {
        public string Id { get; set; } = string.Empty;

        public string ProcessInstanceId { get; set; } = string.Empty;

        public string? ProcessDefinitionId { get; set; }

        public string ActivityId { get; set; } = string.Empty;

        public string BoundaryEventId { get; set; } = string.Empty;

        public DateTime DueDate { get; set; }

        public string? TenantId { get; set; }

        public DateTime CreateTime { get; set; }

        public int? Retries { get; set; }

        public int? DeadLetter { get; set; }

        public string? ExceptionMessage { get; set; }

        public DateTime? LastFailureTime { get; set; }
    }

    private sealed class TimerJobStatsRow
    {
        public int Total { get; set; }

        public int? Overdue { get; set; }

        public int? DueWithinHour { get; set; }

        public int? DeadLetter { get; set; }

        public DateTime? NextDueAt { get; set; }
    }
}

/// <summary>作业筛选条件。</summary>
/// <param name="ProcessInstanceId">按实例过滤。</param>
/// <param name="ProcessDefinitionId">按定义过滤（精确 id）。</param>
/// <param name="TenantId">租户（空表示不限制，仅无租户限制的身份可用）。</param>
/// <param name="DueBefore">只看到期时间不晚于该时刻的作业。</param>
public sealed record TimerJobFilter(
    string? ProcessInstanceId = null,
    string? ProcessDefinitionId = null,
    string? TenantId = null,
    DateTime? DueBefore = null,
    bool? DeadLetter = null);

/// <summary>定时作业摘要。</summary>
public sealed record TimerJobSummary(
    string Id,
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string ActivityId,
    string BoundaryEventId,
    DateTime DueDate,
    string? TenantId,
    DateTime CreateTime,
    int Retries = 0,
    bool DeadLetter = false,
    string? ExceptionMessage = null,
    DateTime? LastFailureTime = null);

/// <summary>作业分页结果。</summary>
public sealed record TimerJobPage(IReadOnlyList<TimerJobSummary> Items, int Total);

/// <summary>作业统计。</summary>
public sealed record TimerJobStats(
    int Total,
    int Overdue,
    int DueWithinHour,
    DateTime? NextDueAt,
    int DeadLetter = 0);
