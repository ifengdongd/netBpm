using System.Collections.Concurrent;

namespace NetBpm.Engine.Jobs;

/// <summary>
/// 定时作业（边界定时器）。对应 Activiti 的 <c>JobEntity</c> 中 <c>TYPE_ = 'timer'</c> 的子集。
/// <para>
/// 一次用户任务创建 = 每个挂在其上的定时边界事件一条作业。到点后由宿主轮询触发：
/// 删除作业 → 取消任务 → 沿边界事件出流继续。
/// </para>
/// </summary>
public sealed class TimerJobEntity
{
    /// <summary>作业 id（<c>ACT_RU_TIMER_JOB.ID_</c>，由 <c>IIdGenerator</c> 分配）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>乐观锁版本（<c>REV_</c>，本子集只插入/删除，不更新）。</summary>
    public int Revision { get; set; }

    /// <summary>作业类型（<c>TYPE_</c>，固定 <c>timer</c>）。</summary>
    public string Type { get; set; } = "timer";

    /// <summary>持有任务的执行 id（<c>EXECUTION_ID_</c>）。</summary>
    public string ExecutionId { get; set; } = string.Empty;

    /// <summary>流程实例 id（<c>PROCESS_INSTANCE_ID_</c>）。</summary>
    public string ProcessInstanceId { get; set; } = string.Empty;

    /// <summary>流程定义 id（<c>PROC_DEF_ID_</c>）。</summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary>挂靠的用户任务节点 id（<c>ACTIVITY_ID_</c>）。</summary>
    public string ActivityId { get; set; } = string.Empty;

    /// <summary>边界事件 id（<c>BOUNDARY_EVENT_ID_</c>）。</summary>
    public string BoundaryEventId { get; set; } = string.Empty;

    /// <summary>到期时间（UTC，<c>DUEDATE_</c>）。</summary>
    public DateTime DueDate { get; set; }

    /// <summary>租户（<c>TENANT_ID_</c>）。</summary>
    public string? TenantId { get; set; }

    /// <summary>创建时间（UTC，<c>CREATE_TIME_</c>）。</summary>
    public DateTime CreateTime { get; set; }

    /// <summary>剩余重试次数（<c>RETRIES_</c>）；0 表示已进死信。</summary>
    public int Retries { get; set; } = TimerJobRetry.DefaultMaxRetries;

    /// <summary>是否已进死信（<c>DEAD_LETTER_</c>）：不再参与到期轮询，需人工重试。</summary>
    public bool DeadLetter { get; set; }

    /// <summary>最后一次失败原因（<c>EXCEPTION_MSG_</c>）。</summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>最后一次失败时间（UTC，<c>LAST_FAILURE_</c>）。</summary>
    public DateTime? LastFailureTime { get; set; }
}

/// <summary>定时作业重试策略的公共常量。</summary>
public static class TimerJobRetry
{
    /// <summary>默认最大重试次数。</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// 第 <paramref name="failedAttempts"/> 次失败后的退避时长：1、2、4… 分钟，上限 30 分钟。
    /// </summary>
    public static TimeSpan Backoff(int failedAttempts)
    {
        var minutes = Math.Min(30, Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 5)));
        return TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>
/// 定时作业存储端口。
/// <para>
/// 删除必须是<b>条件删除</b>（<see cref="TryDeleteAsync"/> 返回是否删到行），
/// 多宿主同时轮询时只有一个能真正触发作业 —— 这与 Activiti 的
/// <c>deleteJob</c> + 影响行数判定同构。
/// </para>
/// </summary>
public interface ITimerJobManager
{
    /// <summary>插入作业（<see cref="TimerJobEntity.Id"/> 必须已由 <c>IIdGenerator</c> 分配）。</summary>
    ValueTask InsertAsync(TimerJobEntity job, CancellationToken ct = default);

    /// <summary>按 id 查作业；不存在返回 <see langword="null"/>。</summary>
    ValueTask<TimerJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default);

    /// <summary>取到期的作业（按到期时间升序，最多 <paramref name="maxCount"/> 条）。</summary>
    ValueTask<IReadOnlyList<TimerJobEntity>> FindDueAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>删除作业并返回是否删到（<b>抢占</b>语义：false 表示已被别的宿主处理）。</summary>
    ValueTask<bool> TryDeleteAsync(string jobId, CancellationToken ct = default);

    /// <summary>删除某执行上的全部作业（任务完成 / 跳转时调用）。</summary>
    ValueTask<int> DeleteByExecutionAsync(string executionId, CancellationToken ct = default);

    /// <summary>删除某流程实例的全部作业（实例结束 / 终止 / 删除时调用）。</summary>
    ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>作业总数（诊断与测试用）。</summary>
    ValueTask<int> CountAsync(CancellationToken ct = default);

    /// <summary>把作业的到期时间改到指定时刻（挂起实例的作业让位给正常作业）。</summary>
    ValueTask RescheduleAsync(string jobId, DateTime dueDate, CancellationToken ct = default);

    /// <summary>
    /// 记录一次执行失败：递减重试次数、写入失败原因、按退避设置下次到期时间；
    /// 重试次数用尽时置死信（<c>DEAD_LETTER_ = 1</c>）。
    /// </summary>
    ValueTask MarkFailureAsync(
        string jobId,
        string reason,
        DateTime nowUtc,
        DateTime nextDueUtc,
        CancellationToken ct = default);

    /// <summary>查死信作业（按失败时间倒序，最多 <paramref name="maxCount"/> 条）。</summary>
    ValueTask<IReadOnlyList<TimerJobEntity>> FindDeadLettersAsync(
        int maxCount,
        CancellationToken ct = default);

    /// <summary>把死信作业重置为可执行（重试次数复位、到期时间置为当前），成功返回 true。</summary>
    ValueTask<bool> RetryDeadLetterAsync(string jobId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>删除死信作业（人工确认不再重试），返回是否删到。</summary>
    ValueTask<bool> DeleteDeadLetterAsync(string jobId, CancellationToken ct = default);
}

/// <summary>单进程定时作业存储（内存宿主 / 单元测试）。</summary>
public sealed class InMemoryTimerJobManager : ITimerJobManager
{
    private readonly ConcurrentDictionary<string, TimerJobEntity> _jobs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask InsertAsync(TimerJobEntity job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Id);

        if (!_jobs.TryAdd(job.Id, job))
        {
            throw new InvalidOperationException($"定时作业 '{job.Id}' 已存在。");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<TimerJobEntity?> FindByIdAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return ValueTask.FromResult(_jobs.GetValueOrDefault(jobId));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<TimerJobEntity>> FindDueAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default)
    {
        IReadOnlyList<TimerJobEntity> due = _jobs.Values
            .Where(job => !job.DeadLetter && job.DueDate <= utcNow)
            .OrderBy(job => job.DueDate)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCount))
            .ToList();

        return ValueTask.FromResult(due);
    }

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

        var keys = _jobs
            .Where(pair => string.Equals(pair.Value.ExecutionId, executionId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        return ValueTask.FromResult(RemoveAll(keys));
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        var keys = _jobs
            .Where(pair => string.Equals(pair.Value.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        return ValueTask.FromResult(RemoveAll(keys));
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(_jobs.Count);

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
    public ValueTask<IReadOnlyList<TimerJobEntity>> FindDeadLettersAsync(
        int maxCount,
        CancellationToken ct = default)
    {
        IReadOnlyList<TimerJobEntity> dead = _jobs.Values
            .Where(job => job.DeadLetter)
            .OrderByDescending(job => job.LastFailureTime ?? job.DueDate)
            .ThenBy(job => job.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCount))
            .ToList();

        return ValueTask.FromResult(dead);
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
            job.Retries = TimerJobRetry.DefaultMaxRetries;
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

    private int RemoveAll(IEnumerable<string> keys)
    {
        var removed = 0;

        foreach (var key in keys)
        {
            if (_jobs.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
