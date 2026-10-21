using System.Collections.Concurrent;

namespace NetBpm.Engine.Jobs;

/// <summary>异步作业的执行阶段（对应 <c>ACT_RU_JOB.PHASE_</c>）。</summary>
public enum AsyncJobPhase
{
    /// <summary>活动开始前（<c>activiti:asyncBefore</c>）：作业执行时记录活动开始并运行业务行为。</summary>
    Before = 0,

    /// <summary>活动执行完成后（<c>activiti:asyncAfter</c>）：作业执行时只负责离开节点。</summary>
    After = 1,
}

/// <summary>
/// 异步作业（<c>ACT_RU_JOB</c> 一行）。对应 Activiti 的 <c>JobEntity</c> 中
/// <c>HANDLER_TYPE_ = 'async-continuation'</c> 的子集。
/// <para>
/// 一条作业只推进<b>一个执行的一步</b>：<c>asyncBefore</c> 把「进入活动」推迟到作业里，
/// <c>asyncAfter</c> 把「离开活动」推迟到作业里。作业与业务变更在同一事务里创建，
/// 因此不会出现「流程推进了、作业没落库」。
/// </para>
/// </summary>
public sealed class AsyncJobEntity
{
    /// <summary>作业 id（<c>ID_</c>，由 <c>IIdGenerator</c> 分配）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>乐观锁版本（<c>REV_</c>）。</summary>
    public int Revision { get; set; }

    /// <summary>执行 id（<c>EXECUTION_ID_</c>）。</summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>流程实例 id（<c>PROCESS_INSTANCE_ID_</c>）。</summary>
    public string ProcessInstanceId { get; set; } = string.Empty;

    /// <summary>流程定义 id（<c>PROC_DEF_ID_</c>）。</summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary>活动 id（<c>ACTIVITY_ID_</c>）。</summary>
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>阶段（<c>PHASE_</c>：before / after）。</summary>
    public AsyncJobPhase Phase { get; set; }

    /// <summary>到期时间（UTC，<c>DUEDATE_</c>）。</summary>
    public DateTime DueDate { get; set; }

    /// <summary>租户（<c>TENANT_ID_</c>）。</summary>
    public string? TenantId { get; set; }

    /// <summary>创建时间（UTC，<c>CREATE_TIME_</c>）。</summary>
    public DateTime CreateTime { get; set; }

    /// <summary>剩余重试次数（<c>RETRIES_</c>）；0 表示已进死信。</summary>
    public int Retries { get; set; } = AsyncJobRetry.DefaultMaxRetries;

    /// <summary>是否已进死信（<c>DEAD_LETTER_</c>）：不再参与轮询，需人工重试。</summary>
    public bool DeadLetter { get; set; }

    /// <summary>最后一次失败原因（<c>EXCEPTION_MSG_</c>）。</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>最后一次失败时间（UTC，<c>LAST_FAILURE_</c>）。</summary>
    public DateTime? LastFailureTime { get; set; }

    /// <summary><c>PHASE_</c> 的持久化值。</summary>
    public string PhaseValue => Phase == AsyncJobPhase.Before ? "before" : "after";
}

/// <summary>异步作业重试策略。</summary>
public static class AsyncJobRetry
{
    /// <summary>默认最大重试次数。</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// 第 <paramref name="failedAttempts"/> 次失败后的退避：5、10、20、40… 秒，上限 5 分钟。
    /// <para>比定时作业快：异步作业通常卡在外部系统抖动，尽快重试更有价值。</para>
    /// </summary>
    public static TimeSpan Backoff(int failedAttempts)
    {
        var seconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 6)));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>异步作业筛选（运维查询）。</summary>
/// <param name="ProcessInstanceId">按实例过滤。</param>
/// <param name="DeadLetter">按死信状态过滤；<see langword="null"/> 不限制。</param>
/// <param name="FirstResult">跳过条数。</param>
/// <param name="MaxResults">最多返回条数。</param>
public sealed record AsyncJobQuery(
    string? ProcessInstanceId = null,
    bool? DeadLetter = null,
    int FirstResult = 0,
    int MaxResults = 50,
    string? TenantId = null);

/// <summary>
/// 异步作业存储端口。
/// <para>
/// 与定时作业一致：<see cref="TryDeleteAsync"/> 必须是<b>条件删除</b>（返回是否删到行），
/// 多宿主同时轮询时只有一个能真正执行。
/// </para>
/// </summary>
public interface IAsyncJobManager
{
    /// <summary>插入作业（<see cref="AsyncJobEntity.Id"/> 必须已分配）。</summary>
    ValueTask InsertAsync(AsyncJobEntity job, CancellationToken ct = default);

    /// <summary>按 id 查作业。</summary>
    ValueTask<AsyncJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default);

    /// <summary>取到期作业（排除死信与挂起实例，按到期时间升序，最多 <paramref name="maxCount"/> 条）。</summary>
    ValueTask<IReadOnlyList<AsyncJobEntity>> FindDueAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>按条件分页查作业（运维诊断）。</summary>
    ValueTask<IReadOnlyList<AsyncJobEntity>> FindAsync(AsyncJobQuery query, CancellationToken ct = default);

    /// <summary>按条件数作业。</summary>
    ValueTask<int> CountAsync(AsyncJobQuery query, CancellationToken ct = default);

    /// <summary>作业总数（运行中 + 死信）。</summary>
    ValueTask<int> CountAsync(CancellationToken ct = default);

    /// <summary>删除作业并返回是否删到（<b>抢占</b>语义）。</summary>
    ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>删除某执行上的全部作业。</summary>
    ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default);

    /// <summary>删除某流程实例的全部作业。</summary>
    ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>记录失败：递减重试次数、写原因、按退避设置下次到期；用尽时置死信。</summary>
    ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default);

    /// <summary>把死信作业重置为可执行。</summary>
    ValueTask<bool> RetryDeadLetterAsync(string jobId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>删除死信作业（人工确认不再重试）。</summary>
    ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default);

    /// <summary>改期（挂起实例的作业让位给正常作业）。</summary>
    ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default);
}

/// <summary>单进程异步作业存储（内存宿主 / 单元测试）。</summary>
public sealed class InMemoryAsyncJobManager : IAsyncJobManager
{
    private readonly ConcurrentDictionary<string, AsyncJobEntity> _jobs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask InsertAsync(AsyncJobEntity job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);

        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException($"异步作业 '{job.Id}' 已存在。");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AsyncJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return ValueTask.FromResult(_jobs.GetValueOrDefault(jobId));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AsyncJobEntity>> FindDueAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default)
    {
        IReadOnlyList<AsyncJobEntity> due = _jobs.Values
            .Where(job => !job.DeadLetter && job.DueDate <= utcNow)
            .OrderBy(job => job.DueDate)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCount))
            .ToList();

        return ValueTask.FromResult(due);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AsyncJobEntity>> FindAsync(
        AsyncJobQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<AsyncJobEntity> page = _jobs.Values
            .Where(job => query.ProcessInstanceId is null
                || string.Equals(job.ProcessInstanceId, query.ProcessInstanceId, StringComparison.Ordinal))
            .Where(job => query.DeadLetter is null || job.DeadLetter == query.DeadLetter)
            .Where(job => query.TenantId is null
                || string.Equals(job.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
            .OrderBy(job => job.CreateTime)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, query.FirstResult))
            .Take(Math.Max(1, query.MaxResults))
            .ToList();

        return ValueTask.FromResult(page);
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(AsyncJobQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var count = _jobs.Values
            .Where(job => query.ProcessInstanceId is null
                || string.Equals(job.ProcessInstanceId, query.ProcessInstanceId, StringComparison.Ordinal))
            .Where(job => query.DeadLetter is null || job.DeadLetter == query.DeadLetter)
            .Where(job => query.TenantId is null
                || string.Equals(job.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal))
            .Count();

        return ValueTask.FromResult(count);
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(_jobs.Count);

    /// <inheritdoc />
    public ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return ValueTask.FromResult(_jobs.TryRemove(jobId, out _));
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        return ValueTask.FromResult(RemoveAll(pair =>
            string.Equals(pair.Value.ExecutionId, executionId, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        return ValueTask.FromResult(RemoveAll(pair =>
            string.Equals(pair.Value.ProcessInstanceId, processInstanceId, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (_jobs.TryGetValue(jobId, out var job))
        {
            lock (job)
            {
                job.Retries = Math.Max(0, job.Retries - 1);
                job.ExceptionMessage = reason;
                job.LastFailureTime = nowUtc;
                job.DueDate = nextDueUtc;
                job.DeadLetter = job.Retries <= 0;

                if (job.DeadLetter)
                {
                    job.DueDate = nowUtc;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryDeadLetterAsync(string jobId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (!_jobs.TryGetValue(jobId, out var job) || !job.DeadLetter)
        {
            return ValueTask.FromResult(false);
        }

        lock (job)
        {
            job.DeadLetter = false;
            job.Retries = AsyncJobRetry.DefaultMaxRetries;
            job.ExceptionMessage = null;
            job.DueDate = nowUtc;
        }

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (!_jobs.TryGetValue(jobId, out var job) || !job.DeadLetter)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(_jobs.TryRemove(jobId, out _));
    }

    /// <inheritdoc />
    public ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (_jobs.TryGetValue(jobId, out var job))
        {
            lock (job)
            {
                job.DueDate = dueDate;
            }
        }

        return ValueTask.CompletedTask;
    }

    private int RemoveAll(Func<KeyValuePair<string, AsyncJobEntity>, bool> predicate)
    {
        var removed = 0;

        foreach (var pair in _jobs.Where(predicate).ToList())
        {
            if (_jobs.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
