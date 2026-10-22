using NetBpm.Engine.Jobs;

namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// 延迟作业队列的<b>默认实现</b>：进程内优先队列（<see cref="PriorityQueue{TElement, TPriority}"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 事件驱动：入队立即唤醒等待中的消费者；无到期项时精确睡到「下一个到期时刻」，
/// 空闲时不做周期性扫库。
/// </para>
/// <para>
/// 兜底等待 <see cref="MaxWait"/>：即使没有任何提示（例如进程刚重启、内存队列为空
/// 但库里还有到期作业），最多等这么久就返回空，让调用方回扫一次数据库。
/// 这正是「队列只是提示、数据库才是事实来源」的体现。
/// </para>
/// <para>
/// 局限：<b>仅限单进程内</b>，进程重启后未到期提示丢失（由兜底扫库补上）。
/// 多宿主共享请改用 <see cref="RedisJobQueue"/>。
/// </para>
/// </remarks>
public sealed class InMemoryJobQueue : IJobQueue
{
    private readonly PriorityQueue<JobQueueItem, DateTime> _queue = new();
    private readonly SemaphoreSlim _wakeup = new(0);
    private readonly object _gate = new();
    private bool _disposed;

    /// <param name="maxWait">空闲时的兜底等待上限；<see langword="null"/> 用默认值 30s。</param>
    public InMemoryJobQueue(TimeSpan? maxWait = null)
    {
        var candidate = maxWait ?? TimeSpan.FromSeconds(30);

        MaxWait = candidate <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : candidate;
    }

    /// <summary>空闲时的兜底等待上限。</summary>
    public TimeSpan MaxWait { get; }

    /// <inheritdoc />
    public string Name => "memory";

    /// <inheritdoc />
    public ValueTask EnqueueAsync(JobQueueItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.JobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            _queue.Enqueue(item, item.DueAtUtc);

            // 只在确实有等待者时才放行，把信号量计数钉在 0/1，
            // 避免「连续入队 → 计数累积 → 之后空转自旋」。
            if (_wakeup.CurrentCount == 0)
            {
                _wakeup.Release();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JobQueueItem>> TakeDueAsync(
        int maxCount,
        TimeSpan waitTimeout,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 无论调用方传多久，都不超过本实现的兜底上限：
        // 保证「提示丢失」时最多 MaxWait 就能回落到扫库。
        var wait = waitTimeout < MaxWait ? waitTimeout : MaxWait;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var taken = DequeueDue(maxCount);

            if (taken.Count > 0)
            {
                return taken;
            }

            var untilNext = NextDelay(wait);

            if (untilNext <= TimeSpan.Zero)
            {
                return Array.Empty<JobQueueItem>();
            }

            await _wakeup.WaitAsync(untilNext, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // 内存队列取出即出队，无需确认
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 放回队首，下一轮立即重试
        var item = new JobQueueItem(jobId, DateTime.UtcNow);

        lock (_gate)
        {
            _queue.Enqueue(item, item.DueAtUtc);
        }

        SignalWakeup();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _wakeup.Dispose();

        return ValueTask.CompletedTask;
    }

    private List<JobQueueItem> DequeueDue(int maxCount)
    {
        var taken = new List<JobQueueItem>(maxCount);
        var now = DateTime.UtcNow;

        lock (_gate)
        {
            while (taken.Count < maxCount
                   && _queue.TryPeek(out _, out var due)
                   && due <= now)
            {
                taken.Add(_queue.Dequeue());
            }
        }

        return taken;
    }

    private TimeSpan NextDelay(TimeSpan wait)
    {
        lock (_gate)
        {
            if (!_queue.TryPeek(out _, out var next))
            {
                return wait;
            }

            var untilNext = next - DateTime.UtcNow;

            if (untilNext <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return untilNext < wait ? untilNext : wait;
        }
    }

    private void SignalWakeup()
    {
        lock (_gate)
        {
            if (_wakeup.CurrentCount == 0)
            {
                _wakeup.Release();
            }
        }
    }
}
