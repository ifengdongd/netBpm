using System.Collections.Concurrent;

namespace NetBpm.Engine.Events;

/// <summary>
/// 事务性 outbox 事件（<c>ACT_EVT_OUTBOX</c> 一行）。
/// <para>
/// 事件与业务变更在<b>同一个命令事务</b>里写入：命令回滚则事件一起回滚，
/// 命令提交则事件一定可见 —— 解决「流程结束了、通知没发出去」的双写问题。
/// 后台发布器负责把事件投递给订阅者（至少一次），发布成功才打标。
/// </para>
/// </summary>
public sealed class OutboxEvent
{
    /// <summary>事件 id（<c>ID_</c>，全局唯一；订阅方可用它做幂等键）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>事件类型（<c>EVENT_TYPE_</c>，见 <c>EngineEvents</c>）。</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>实体 id（<c>ENTITY_ID_</c>）：任务 id / 活动 id / 实例 id，视事件而定。</summary>
    public string? EntityId { get; set; }

    /// <summary>流程实例 id（<c>PROCESS_INSTANCE_ID_</c>）。</summary>
    public string? ProcessInstanceId { get; set; }

    /// <summary>流程定义 id（<c>PROC_DEF_ID_</c>）。</summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary>执行 id（<c>EXECUTION_ID_</c>）。</summary>
    public string? ExecutionId { get; set; }

    /// <summary>活动 id（<c>ACTIVITY_ID_</c>）。</summary>
    public string? ActivityId { get; set; }

    /// <summary>租户（<c>TENANT_ID_</c>）。</summary>
    public string? TenantId { get; set; }

    /// <summary>事件载荷（<c>PAYLOAD_</c>，JSON）：订阅方不必再回查即可拿到关键 id。</summary>
    public string? Payload { get; set; }

    /// <summary>创建时间（UTC，<c>CREATE_TIME_</c>）。</summary>
    public DateTime CreateTime { get; set; }

    /// <summary>是否已发布（<c>PUBLISHED_</c>）。</summary>
    public bool Published { get; set; }

    /// <summary>发布尝试次数（<c>PUBLISH_ATTEMPTS_</c>）。</summary>
    public int PublishAttempts { get; set; }

    /// <summary>是否已进死信（<c>DEAD_LETTER_</c>）：超过最大尝试次数，需人工重试。</summary>
    public bool DeadLetter { get; set; }

    /// <summary>最后一次发布失败原因（<c>LAST_ERROR_</c>）。</summary>
    public string? LastError { get; set; }

    /// <summary>下次可发布时间（UTC，<c>NEXT_ATTEMPT_</c>）。发布失败后按退避顺延。</summary>
    public DateTime? NextAttemptTime { get; set; }

    /// <summary>发布时间（UTC，<c>PUBLISHED_TIME_</c>）。</summary>
    public DateTime? PublishedTime { get; set; }
}

/// <summary>outbox 重试策略。</summary>
public static class OutboxRetry
{
    /// <summary>默认最大发布尝试次数。</summary>
    public const int DefaultMaxAttempts = 5;

    /// <summary>第 <paramref name="failedAttempts"/> 次失败后的退避：5、10、20… 秒，上限 5 分钟。</summary>
    public static TimeSpan Backoff(int failedAttempts)
    {
        var seconds = Math.Min(300, 5 * Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 6)));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// 事件写入端口：由引擎在命令事务内调用。
/// <para>实现必须使用<b>当前命令的会话/事务</b>，不能另开连接，否则失去事务性。</para>
/// </summary>
public interface IEventOutbox
{
    /// <summary>写入一条待发布事件（幂等：同 <see cref="OutboxEvent.Id"/> 重复写入应忽略）。</summary>
    ValueTask EnqueueAsync(OutboxEvent outboxEvent, CancellationToken ct = default);
}

/// <summary>外部事件订阅者（由宿主注册，可注册多个）。</summary>
/// <remarks>
/// <b>至少一次</b>：发布失败会重试，订阅方必须以 <see cref="OutboxEvent.Id"/> 做幂等。
/// </remarks>
public interface IOutboxEventHandler
{
    /// <summary>处理一条事件；抛异常表示本次发布失败，由发布器安排重试。</summary>
    ValueTask HandleAsync(OutboxEvent outboxEvent, CancellationToken ct = default);
}

/// <summary>outbox 查询条件（运维诊断）。</summary>
/// <param name="ProcessInstanceId">按实例过滤。</param>
/// <param name="Published">按已发布状态过滤；<see langword="null"/> 不限制。</param>
/// <param name="DeadLetter">按死信状态过滤；<see langword="null"/> 不限制。</param>
/// <param name="FirstResult">跳过条数。</param>
/// <param name="MaxResults">最多返回条数。</param>
public sealed record OutboxQuery(
    string? ProcessInstanceId = null,
    bool? Published = null,
    bool? DeadLetter = null,
    int FirstResult = 0,
    int MaxResults = 50,
    string? TenantId = null);

/// <summary>outbox 存储端口（发布器 + 运维查询）。</summary>
public interface IEventOutboxStore : IEventOutbox
{
    /// <summary>取待发布事件（未发布、未死信、到下次尝试时间），按创建时间升序。</summary>
    ValueTask<IReadOnlyList<OutboxEvent>> FindPendingAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default);

    /// <summary>按条件分页查事件。</summary>
    ValueTask<IReadOnlyList<OutboxEvent>> FindAsync(OutboxQuery query, CancellationToken ct = default);

    /// <summary>按条件数事件。</summary>
    ValueTask<int> CountAsync(OutboxQuery query, CancellationToken ct = default);

    /// <summary>标记发布成功。</summary>
    ValueTask<bool> MarkPublishedAsync(string eventId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>记录发布失败：尝试次数 +1，按退避顺延；超过上限置死信。</summary>
    ValueTask MarkFailureAsync(
        string eventId,
        string reason,
        DateTime nowUtc,
        DateTime nextAttemptUtc,
        CancellationToken ct = default);

    /// <summary>人工重试死信事件（复位尝试次数 / 清除死信）。</summary>
    ValueTask<bool> RetryAsync(string eventId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>删除事件（人工确认不再投递）。</summary>
    ValueTask<bool> DeleteAsync(string eventId, CancellationToken ct = default);
}

/// <summary>单进程 outbox（内存宿主 / 单元测试）。</summary>
public sealed class InMemoryEventOutbox : IEventOutboxStore
{
    private readonly ConcurrentDictionary<string, OutboxEvent> _events = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask EnqueueAsync(OutboxEvent outboxEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxEvent.Id);

        _events.TryAdd(outboxEvent.Id, outboxEvent);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OutboxEvent>> FindPendingAsync(
        DateTime utcNow,
        int maxCount,
        CancellationToken ct = default)
    {
        IReadOnlyList<OutboxEvent> pending = _events.Values
            .Where(item => !item.Published
                && !item.DeadLetter
                && (item.NextAttemptTime is null || item.NextAttemptTime <= utcNow))
            .OrderBy(item => item.CreateTime)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCount))
            .ToList();

        return ValueTask.FromResult(pending);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OutboxEvent>> FindAsync(
        OutboxQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<OutboxEvent> page = Filter(query)
            .OrderBy(item => item.CreateTime)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, query.FirstResult))
            .Take(Math.Max(1, query.MaxResults))
            .ToList();

        return ValueTask.FromResult(page);
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(OutboxQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return ValueTask.FromResult(Filter(query).Count());
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkPublishedAsync(string eventId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        if (!_events.TryGetValue(eventId, out var item))
        {
            return ValueTask.FromResult(false);
        }

        lock (item)
        {
            item.Published = true;
            item.PublishedTime = nowUtc;
            item.LastError = null;
        }

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask MarkFailureAsync(
        string eventId,
        string reason,
        DateTime nowUtc,
        DateTime nextAttemptUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        if (_events.TryGetValue(eventId, out var item))
        {
            lock (item)
            {
                item.PublishAttempts++;
                item.LastError = reason;
                item.NextAttemptTime = nextAttemptUtc;
                item.DeadLetter = item.PublishAttempts >= OutboxRetry.DefaultMaxAttempts;

                if (item.DeadLetter)
                {
                    item.NextAttemptTime = null;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryAsync(string eventId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        if (!_events.TryGetValue(eventId, out var item) || !item.DeadLetter)
        {
            return ValueTask.FromResult(false);
        }

        lock (item)
        {
            item.DeadLetter = false;
            item.PublishAttempts = 0;
            item.LastError = null;
            item.NextAttemptTime = nowUtc;
        }

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string eventId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        return ValueTask.FromResult(_events.TryRemove(eventId, out _));
    }

    private IEnumerable<OutboxEvent> Filter(OutboxQuery query) =>
        _events.Values
            .Where(item => query.ProcessInstanceId is null
                || string.Equals(item.ProcessInstanceId, query.ProcessInstanceId, StringComparison.Ordinal))
            .Where(item => query.Published is null || item.Published == query.Published)
            .Where(item => query.DeadLetter is null || item.DeadLetter == query.DeadLetter)
            .Where(item => query.TenantId is null
                || string.Equals(item.TenantId ?? string.Empty, query.TenantId, StringComparison.Ordinal));
}
