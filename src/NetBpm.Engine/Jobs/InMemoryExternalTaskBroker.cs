namespace NetBpm.Engine.Jobs;

/// <summary>
/// 外部任务的进程内实现（嵌入式宿主 / 单元测试）。
/// <para>
/// 语义必须与 <c>Dapper</c> 版<b>逐条一致</b>，否则「内存宿主跑通、换 MySQL 行为变了」——
/// 这类差异最难查，因为现象是偶发不是必现。两处共享的判定都在
/// <see cref="ExternalTask"/>（<c>IsAvailable</c> / <c>IsLockExpired</c>）与
/// <see cref="ExternalTaskRetryPolicy"/> 里，避免各写一遍。
/// </para>
/// </summary>
public sealed class InMemoryExternalTaskBroker(ExternalTaskOptions? options = null) : IExternalTaskBroker
{
    private readonly ExternalTaskOptions _options = options ?? new();
    private readonly object _gate = new();

    private readonly Dictionary<string, ExternalTask> _tasks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ExternalTask> CreateAsync(ExternalTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var stored = Clone(task);

        if (string.IsNullOrWhiteSpace(stored.Id))
        {
            stored.Id = Guid.NewGuid().ToString("N");
        }

        lock (_gate)
        {
            _tasks[stored.Id] = stored;
        }

        return ValueTask.FromResult(Clone(stored));
    }

    /// <inheritdoc />
    public ValueTask<ExternalTask?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_gate)
        {
            return ValueTask.FromResult(
                _tasks.TryGetValue(id, out var task) ? Clone(task) : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ExternalTask>> FetchAndLockAsync(
        ExternalTaskFetchRequest request,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        if (request.Topics is not { Count: > 0 })
        {
            return ValueTask.FromResult<IReadOnlyList<ExternalTask>>([]);
        }

        var maxTasks = request.MaxTasks <= 0 ? 10 : request.MaxTasks;
        var topics = request.Topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic.Topic))
            .ToArray();

        var locked = new List<ExternalTask>();

        lock (_gate)
        {
            var candidates = _tasks.Values
                .Where(task => task.IsAvailable(nowUtc))
                .Where(task => topics.Any(topic => string.Equals(topic.Topic, task.Topic, StringComparison.Ordinal)))
                .Where(task => MatchesTenant(task.TenantId, request.TenantId))
                .OrderByDescending(task => task.Priority)
                .ThenBy(task => task.CreatedAt)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Take(maxTasks)
                .ToArray();

            foreach (var task in candidates)
            {
                var duration = ResolveLockDuration(request, task.Topic);
                task.Status = ExternalTaskStatus.Locked;
                task.WorkerId = request.WorkerId;
                task.LockExpiresAt = nowUtc + duration;

                locked.Add(Clone(task));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ExternalTask>>(locked);
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAsync(string id, string workerId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || !IsHeldBy(task, workerId, nowUtc))
            {
                return ValueTask.FromResult(false);
            }

            task.Status = ExternalTaskStatus.Completed;
            task.CompletedAt = nowUtc;
            task.WorkerId = workerId;
            task.LockExpiresAt = null;
            task.ErrorMessage = null;
            task.ErrorDetails = null;

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> FailureAsync(
        string id,
        string workerId,
        ExternalTaskFailure failure,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(failure);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || !IsHeldBy(task, workerId, nowUtc))
            {
                return ValueTask.FromResult(false);
            }

            var remaining = failure.Retries ?? task.Retries - 1;

            task.ErrorMessage = Trim(failure.ErrorMessage);
            task.ErrorDetails = Trim(failure.ErrorDetails);
            task.WorkerId = null;
            task.LockExpiresAt = null;

            if (remaining <= 0)
            {
                // 重试耗尽：进死信，停在原地等管理干预 —— 不能悄悄跳过节点，
                // 那样流程会「看起来办完了，其实那一步从没执行」
                task.Status = ExternalTaskStatus.DeadLetter;
                task.Retries = 0;
                task.NextDueAt = null;
            }
            else
            {
                task.Status = ExternalTaskStatus.Pending;
                task.Retries = remaining;
                task.NextDueAt = failure.RetryAfter ?? nowUtc + ExternalTaskRetryPolicy.Backoff(
                    ExternalTaskRetryPolicy.AttemptOf(remaining));
            }

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> ExtendLockAsync(
        string id,
        string workerId,
        TimeSpan lockDuration,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || !IsHeldBy(task, workerId, nowUtc))
            {
                return ValueTask.FromResult(false);
            }

            task.LockExpiresAt = nowUtc + (lockDuration > TimeSpan.Zero ? lockDuration : _options.DefaultLockDuration);

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> UnlockAsync(string id, string workerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task))
            {
                return ValueTask.FromResult(false);
            }

            // 释放锁只认持有者，但<b>不检查锁是否过期</b>：锁已过期时它早就不持有这把锁了，
            // 让它「释放成功」会掩盖「其实已经被别人领走」这件事
            if (!string.Equals(task.WorkerId, workerId, StringComparison.Ordinal)
                || task.Status != ExternalTaskStatus.Locked)
            {
                return ValueTask.FromResult(false);
            }

            task.Status = ExternalTaskStatus.Pending;
            task.WorkerId = null;
            task.LockExpiresAt = null;

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ExternalTask>> FindAsync(ExternalTaskQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var max = query.MaxResults <= 0 ? 100 : query.MaxResults;
        var nowUtc = DateTime.UtcNow;

            lock (_gate)
            {
                IEnumerable<ExternalTask> rows = _tasks.Values;

                if (query.TenantId is { Length: > 0 } tenantId)
                {
                    rows = rows.Where(task => MatchesTenant(task.TenantId, tenantId));
                }

            if (query.Topic is { Length: > 0 } topic)
            {
                rows = rows.Where(task => string.Equals(task.Topic, topic, StringComparison.Ordinal));
            }

            if (query.ProcessInstanceId is { Length: > 0 } instanceId)
            {
                rows = rows.Where(task => string.Equals(task.ProcessInstanceId, instanceId, StringComparison.Ordinal));
            }

            if (query.Status is { Length: > 0 } status)
            {
                rows = rows.Where(task => string.Equals(task.Status, status, StringComparison.Ordinal));
            }

            if (query.WorkerId is { Length: > 0 } workerId)
            {
                rows = rows.Where(task => string.Equals(task.WorkerId, workerId, StringComparison.Ordinal));
            }

            if (query.OnlyDue)
            {
                rows = rows.Where(task => task.IsAvailable(nowUtc));
            }

            IReadOnlyList<ExternalTask> result = rows
                .OrderBy(task => task.CreatedAt)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Take(max)
                .Select(Clone)
                .ToArray();

            return ValueTask.FromResult(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryDeadLetterAsync(string id, int retries, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || task.Status != ExternalTaskStatus.DeadLetter)
            {
                return ValueTask.FromResult(false);
            }

            task.Status = ExternalTaskStatus.Pending;
            task.Retries = retries > 0 ? retries : _options.DefaultRetries;
            task.NextDueAt = null;
            task.WorkerId = null;
            task.LockExpiresAt = null;
            task.ErrorMessage = null;
            task.ErrorDetails = null;

            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteByProcessInstanceAsync(string processInstanceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        lock (_gate)
        {
            var ids = _tasks.Values
                .Where(task => string.Equals(task.ProcessInstanceId, processInstanceId, StringComparison.Ordinal))
                .Select(task => task.Id)
                .ToArray();

            foreach (var id in ids)
            {
                _tasks.Remove(id);
            }

            return ValueTask.FromResult(ids.Length);
        }
    }

    // ─────────────────────────── 内部 ───────────────────────────

    /// <summary>锁是否仍被 <paramref name="workerId"/> 持有（已过期即视为不持有）。</summary>
    private static bool IsHeldBy(ExternalTask task, string workerId, DateTime nowUtc) =>
        task.Status == ExternalTaskStatus.Locked
        && string.Equals(task.WorkerId, workerId, StringComparison.Ordinal)
        && !task.IsLockExpired(nowUtc);

    private TimeSpan ResolveLockDuration(ExternalTaskFetchRequest request, string topic)
    {
        var byTopic = request.Topics
            .FirstOrDefault(item => string.Equals(item.Topic, topic, StringComparison.Ordinal))
            ?.LockDuration;

        return byTopic ?? request.LockDuration ?? _options.DefaultLockDuration;
    }

    /// <summary>租户匹配：未指定租户（单租户部署 / 诊断）时不收窄。</summary>
    private static bool MatchesTenant(string? taskTenant, string? requested) =>
        requested is not { Length: > 0 } || string.Equals(taskTenant ?? "default", requested, StringComparison.Ordinal);

    private static string? Trim(string? value) =>
        value is { Length: > 2000 } longText ? longText[..2000] : value;

    /// <summary>对外一律给副本，避免调用方改到存储里的实体（内存实现尤其容易踩）。</summary>
    private static ExternalTask Clone(ExternalTask task) => new()
    {
        Id = task.Id,
        ProcessInstanceId = task.ProcessInstanceId,
        ExecutionId = task.ExecutionId,
        ProcessDefinitionId = task.ProcessDefinitionId,
        ProcessDefinitionKey = task.ProcessDefinitionKey,
        ActivityId = task.ActivityId,
        ActivityName = task.ActivityName,
        Topic = task.Topic,
        TenantId = task.TenantId,
        BusinessKey = task.BusinessKey,
        Payload = new Dictionary<string, object?>(task.Payload, StringComparer.Ordinal),
        Priority = task.Priority,
        Retries = task.Retries,
        Status = task.Status,
        WorkerId = task.WorkerId,
        LockExpiresAt = task.LockExpiresAt,
        CreatedAt = task.CreatedAt,
        CompletedAt = task.CompletedAt,
        NextDueAt = task.NextDueAt,
        ErrorMessage = task.ErrorMessage,
        ErrorDetails = task.ErrorDetails,
    };
}
