namespace NetBpm.Engine.Events;

/// <summary>
/// 单条投递的状态（<c>nb_event_delivery</c>）。
/// <para>
/// 粒度是「<b>一条事件 × 一个订阅</b>」而不是「一条事件」：三个订阅里挂了一个，
/// 不能因为重投就把另外两个已经收过的再喂一遍。订阅方虽然有 <c>id</c> 做幂等，
/// 但那只是兜底，不是让人随意浪费对方配额的理由。
/// </para>
/// </summary>
public static class EventDeliveryStatus
{
    /// <summary>尚未投递成功（还没试过 / 正在退避）。</summary>
    public const string Pending = "PENDING";

    /// <summary>已收到 2xx。</summary>
    public const string Delivered = "DELIVERED";

    /// <summary>尝试过但失败，还在重试额度内。</summary>
    public const string Failed = "FAILED";

    /// <summary>超过订阅的 <c>MaxAttempts</c>，停投等人工干预（可重投）。</summary>
    public const string DeadLetter = "DEAD_LETTER";
}

/// <summary>一条投递记录。</summary>
public sealed class EventDelivery
{
    /// <summary>outbox 事件 id。</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>订阅 id。</summary>
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>租户（冗余存一份，便于按租户统计与清理）。</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>状态（见 <see cref="EventDeliveryStatus"/>）。</summary>
    public string Status { get; set; } = EventDeliveryStatus.Pending;

    /// <summary>已尝试次数。</summary>
    public int Attempts { get; set; }

    /// <summary>最近一次失败原因。</summary>
    public string? LastError { get; set; }

    /// <summary>下次可再投时间（UTC）；<see langword="null"/> 表示立即可投。</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>首次创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>投递成功时间（UTC）。</summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>是否已终结（成功或死信）：终态不再重试。</summary>
    public bool IsSettled =>
        Status is EventDeliveryStatus.Delivered or EventDeliveryStatus.DeadLetter;
}

/// <summary>
/// 死信投递的查询条件（管理面 <c>GET /admin/v1/events/deadletters</c>）。
/// </summary>
/// <remarks>
/// 只能按租户 / 订阅两个维度过滤：死信的用法是「谁挂了我去看看」，
/// 而不是按事件时间做分析（那件事该在日志里做，死信表只保留到重投或清理）。
/// </remarks>
/// <param name="TenantId">租户过滤；<see langword="null"/> 表示跨租户（仅管理面）。</param>
/// <param name="SubscriptionId">订阅过滤。</param>
/// <param name="FirstResult">跳过条数。</param>
/// <param name="MaxResults">页大小（调用方负责夹取上限）。</param>
public sealed record EventDeadLetterQuery(
    string? TenantId = null,
    string? SubscriptionId = null,
    int FirstResult = 0,
    int MaxResults = 20);

/// <summary>死信分页结果。</summary>
/// <param name="Items">当前页（按更新时间倒序）。</param>
/// <param name="Total">满足条件的总数。</param>
public sealed record EventDeadLetterPage(IReadOnlyList<EventDelivery> Items, int Total);

/// <summary>
/// 投递统计的查询条件（管理面 <c>GET /admin/v1/events/stats</c>）。
/// </summary>
/// <param name="TenantId">租户过滤；<see langword="null"/> 表示跨租户（仅管理面）。</param>
/// <param name="SubscriptionId">订阅过滤；为空表示全部订阅。</param>
/// <param name="SinceUtc">
/// 只统计这段时间之后<b>创建</b>的投递记录；为空表示全量。
/// 看板要的是「最近一小时有没有异常」，不是「开天辟地以来的成功率」——后者被历史稀释得没有意义。
/// </param>
public sealed record EventDeliveryStatsQuery(
    string? TenantId = null,
    string? SubscriptionId = null,
    DateTime? SinceUtc = null);

/// <summary>
/// 一个订阅的投递统计。
/// </summary>
/// <remarks>
/// <para>
/// 计数按「投递记录」而不是「事件」：同一条事件对三个订阅就是三条记录，
/// 这正是运维要看的粒度（「ERP 那个订阅挂得多，归档那个一直好」）。
/// </para>
/// <para>
/// <see cref="Failed"/> 是<b>还在重试额度内</b>的失败，<see cref="DeadLetter"/> 是已经停投的——
/// 两个数分开，否则「失败了 20 次」与「有 20 条永不重试」看起来一样。
/// </para>
/// </remarks>
/// <param name="SubscriptionId">订阅 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="Total">投递记录总数。</param>
/// <param name="Pending">尚未投递成功（没试过 / 退避中）。</param>
/// <param name="Delivered">已成功。</param>
/// <param name="Failed">失败但还有重试额度。</param>
/// <param name="DeadLetter">已进死信。</param>
/// <param name="LastDeliveredAt">最近一次成功投递时间。</param>
/// <param name="LastFailureAt">最近一次失败时间（含死信）。</param>
public sealed record EventDeliveryStats(
    string SubscriptionId,
    string TenantId,
    int Total,
    int Pending,
    int Delivered,
    int Failed,
    int DeadLetter,
    DateTimeOffset? LastDeliveredAt,
    DateTimeOffset? LastFailureAt);

/// <summary>
/// 投递存储。
/// </summary>
public interface IEventDeliveryStore
{
    /// <summary>按 事件 id + 订阅 id 查；不存在返回 <see langword="null"/>。</summary>
    ValueTask<EventDelivery?> FindAsync(string eventId, string subscriptionId, CancellationToken ct = default);

    /// <summary>按事件查全部订阅的投递情况（运维看「这条事件谁没收到」）。</summary>
    ValueTask<IReadOnlyList<EventDelivery>> FindByEventAsync(string eventId, CancellationToken ct = default);

    /// <summary>取（没有就建）一条投递记录。</summary>
    ValueTask<EventDelivery> GetOrCreateAsync(
        string eventId,
        string subscriptionId,
        string tenantId,
        CancellationToken ct = default);

    /// <summary>标记投递成功。</summary>
    ValueTask MarkDeliveredAsync(string eventId, string subscriptionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// 记录失败：尝试次数 +1；达到 <paramref name="maxAttempts"/> 置死信并停投。
    /// </summary>
    ValueTask MarkFailureAsync(
        string eventId,
        string subscriptionId,
        string reason,
        DateTime nowUtc,
        DateTime? nextAttemptUtc,
        int maxAttempts,
        CancellationToken ct = default);

    /// <summary>
    /// 推迟本次投递：只把下次尝试时间往后挪，<b>不动</b>尝试次数、<b>不改</b>状态、不留失败原因。
    /// </summary>
    /// <remarks>
    /// 存在的理由只有一个：<b>被限速挡下不是失败</b>。若走 <see cref="MarkFailureAsync"/>，
    /// 订阅方只是回得慢一点、被限速几次，就会把 <c>MaxAttempts</c> 用完推进死信 ——
    /// 而它一次都没真的失败过。状态保持原样（<see cref="EventDeliveryStatus.Pending"/> 或
    /// <see cref="EventDeliveryStatus.Failed"/>），语义是「这条还没投成，只是现在轮不到它」。
    /// </remarks>
    ValueTask MarkDeferredAsync(
        string eventId,
        string subscriptionId,
        DateTime nextAttemptUtc,
        DateTime nowUtc,
        CancellationToken ct = default);

    /// <summary>人工重投死信投递（复位尝试次数与状态）。</summary>
    ValueTask<bool> RetryAsync(string eventId, string subscriptionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>事件被删除时一并清理投递记录。</summary>
    ValueTask<int> DeleteByEventAsync(string eventId, CancellationToken ct = default);

    /// <summary>
    /// 列死信（只返回 <see cref="EventDeliveryStatus.DeadLetter"/>，按更新时间倒序）。
    /// </summary>
    /// <remarks>
    /// 单独一个方法而不是「<c>FindByEvent</c> 加个状态参数」：死信的读法是「翻一遍所有挂掉的」，
    /// 没有具体事件 id 可给；而按事件查的场景要看到「这条事件三个订阅各自的进展」，
    /// 包含未终结的那些。两个查询的过滤条件与排序都不同，硬合成一个只会让两边都别扭。
    /// </remarks>
    /// <param name="query">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<EventDeadLetterPage> ListDeadLettersAsync(
        EventDeadLetterQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// 按订阅统计投递结果（看板）。
    /// </summary>
    /// <remarks>
    /// 聚合放在存储层（<c>group by subscription_id</c>）而不是取回全表再算：
    /// 投递表是按天增长的，全量取回会随运行时间慢慢变成一次事故。
    /// </remarks>
    /// <param name="query">查询条件。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask<IReadOnlyList<EventDeliveryStats>> GetStatsAsync(
        EventDeliveryStatsQuery query,
        CancellationToken ct = default);
}

/// <summary>单进程投递存储（内存宿主 / 单元测试）。</summary>
public sealed class InMemoryEventDeliveryStore : IEventDeliveryStore
{
    private readonly Dictionary<string, EventDelivery> _deliveries = new(StringComparer.Ordinal);

    private static string Key(string eventId, string subscriptionId) => $"{eventId}\u001f{subscriptionId}";

    /// <inheritdoc />
    public ValueTask<EventDelivery?> FindAsync(string eventId, string subscriptionId, CancellationToken ct = default) =>
        ValueTask.FromResult(
            _deliveries.TryGetValue(Key(eventId, subscriptionId), out var item) ? item : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EventDelivery>> FindByEventAsync(string eventId, CancellationToken ct = default)
    {
        IReadOnlyList<EventDelivery> items = _deliveries.Values
            .Where(item => string.Equals(item.EventId, eventId, StringComparison.Ordinal))
            .OrderBy(item => item.SubscriptionId, StringComparer.Ordinal)
            .ToList();

        return ValueTask.FromResult(items);
    }

    /// <inheritdoc />
    public ValueTask<EventDelivery> GetOrCreateAsync(
        string eventId,
        string subscriptionId,
        string tenantId,
        CancellationToken ct = default)
    {
        var key = Key(eventId, subscriptionId);

        if (!_deliveries.TryGetValue(key, out var item))
        {
            item = new EventDelivery
            {
                EventId = eventId,
                SubscriptionId = subscriptionId,
                TenantId = tenantId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            _deliveries[key] = item;
        }

        return ValueTask.FromResult(item);
    }

    /// <inheritdoc />
    public ValueTask MarkDeliveredAsync(
        string eventId,
        string subscriptionId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (_deliveries.TryGetValue(Key(eventId, subscriptionId), out var item))
        {
            item.Status = EventDeliveryStatus.Delivered;
            item.DeliveredAt = nowUtc;
            item.LastError = null;
            item.NextAttemptAt = null;
            item.UpdatedAt = nowUtc;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkFailureAsync(
        string eventId,
        string subscriptionId,
        string reason,
        DateTime nowUtc,
        DateTime? nextAttemptUtc,
        int maxAttempts,
        CancellationToken ct = default)
    {
        if (!_deliveries.TryGetValue(Key(eventId, subscriptionId), out var item))
        {
            return ValueTask.CompletedTask;
        }

        item.Attempts++;
        item.LastError = reason;
        item.UpdatedAt = nowUtc;

        if (item.Attempts >= Math.Max(1, maxAttempts))
        {
            item.Status = EventDeliveryStatus.DeadLetter;
            item.NextAttemptAt = null;
        }
        else
        {
            item.Status = EventDeliveryStatus.Failed;
            item.NextAttemptAt = nextAttemptUtc;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkDeferredAsync(
        string eventId,
        string subscriptionId,
        DateTime nextAttemptUtc,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (_deliveries.TryGetValue(Key(eventId, subscriptionId), out var item))
        {
            // 刻意不动 Status / Attempts / LastError：被限速不是失败，也不该把上次真实失败的原因冲掉
            item.NextAttemptAt = nextAttemptUtc;
            item.UpdatedAt = nowUtc;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RetryAsync(string eventId, string subscriptionId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!_deliveries.TryGetValue(Key(eventId, subscriptionId), out var item)
            || !string.Equals(item.Status, EventDeliveryStatus.DeadLetter, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }

        item.Status = EventDeliveryStatus.Pending;
        item.Attempts = 0;
        item.LastError = null;
        item.NextAttemptAt = nowUtc;
        item.UpdatedAt = nowUtc;

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<int> DeleteByEventAsync(string eventId, CancellationToken ct = default)
    {
        var keys = _deliveries
            .Where(pair => string.Equals(pair.Value.EventId, eventId, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in keys)
        {
            _deliveries.Remove(key);
        }

        return ValueTask.FromResult(keys.Count);
    }

    /// <inheritdoc />
    public ValueTask<EventDeadLetterPage> ListDeadLettersAsync(
        EventDeadLetterQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var matched = _deliveries.Values
            .Where(item => string.Equals(item.Status, EventDeliveryStatus.DeadLetter, StringComparison.Ordinal))
            .Where(item => query.TenantId is null
                || string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal))
            .Where(item => query.SubscriptionId is null
                || string.Equals(item.SubscriptionId, query.SubscriptionId, StringComparison.Ordinal))
            .ToList();

        var items = matched
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.EventId, StringComparer.Ordinal)
            .ThenBy(item => item.SubscriptionId, StringComparer.Ordinal)
            .Skip(Math.Max(0, query.FirstResult))
            .Take(Math.Max(0, query.MaxResults))
            .ToList();

        return ValueTask.FromResult(new EventDeadLetterPage(items, matched.Count));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EventDeliveryStats>> GetStatsAsync(
        EventDeliveryStatsQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<EventDeliveryStats> stats =
        [
            .. _deliveries.Values
                .Where(item => query.TenantId is null
                    || string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal))
                .Where(item => query.SubscriptionId is null
                    || string.Equals(item.SubscriptionId, query.SubscriptionId, StringComparison.Ordinal))
                .Where(item => query.SinceUtc is not { } since || item.CreatedAt >= since)
                .GroupBy(item => item.SubscriptionId, StringComparer.Ordinal)
                .Select(group => new EventDeliveryStats(
                    group.Key,
                    group.Select(item => item.TenantId).FirstOrDefault() ?? "default",
                    group.Count(),
                    group.Count(item => string.Equals(item.Status, EventDeliveryStatus.Pending, StringComparison.Ordinal)),
                    group.Count(item => string.Equals(item.Status, EventDeliveryStatus.Delivered, StringComparison.Ordinal)),
                    group.Count(item => string.Equals(item.Status, EventDeliveryStatus.Failed, StringComparison.Ordinal)),
                    group.Count(item => string.Equals(item.Status, EventDeliveryStatus.DeadLetter, StringComparison.Ordinal)),
                    AsOffset(group.Max(item => item.DeliveredAt)),
                    AsOffset(group
                        .Where(item => string.Equals(item.Status, EventDeliveryStatus.Failed, StringComparison.Ordinal)
                            || string.Equals(item.Status, EventDeliveryStatus.DeadLetter, StringComparison.Ordinal))
                        .Max(item => (DateTime?)item.UpdatedAt))))
                .OrderBy(item => item.SubscriptionId, StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(stats);
    }

    private static DateTimeOffset? AsOffset(DateTime? value) =>
        value is { } actual ? new DateTimeOffset(DateTime.SpecifyKind(actual, DateTimeKind.Utc)) : null;
}
