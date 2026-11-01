using System.Security.Cryptography;

namespace NetBpm.Engine.Events;

/// <summary>
/// 事件订阅：一条「谁关心哪类事件、往哪儿送」的记录（<c>nb_event_subscription</c>）。
/// <para>
/// 订阅是<b>租户级</b>的：A 租户的流程事件不该送到 B 租户的回调地址上，
/// 因此租户是订阅的必填列，匹配时也必须相等（不是「为空就放行」）。
/// </para>
/// </summary>
public sealed class EventSubscription
{
    /// <summary>订阅 id。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>租户（默认 <c>default</c>）。事件与该值不同则不投递。</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>订阅名（给人看，便于在管理端区分「ERP 回调」「归档服务」）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 关心的引擎事件类型（<see cref="NetBpm.Engine.Services.EngineEvents"/> 的取值）。
    /// <b>空集合 = 全部</b>；非空时按大小写不敏感精确匹配。
    /// </summary>
    public IReadOnlyList<string> EventTypes { get; set; } = [];

    /// <summary>
    /// 只关心某个流程定义 id 的事件；<see langword="null"/> 表示不限。
    /// <para>
    /// 这里是<b>定义 id 而不是 key</b>：outbox 事件只带 <c>PROC_DEF_ID_</c>，
    /// 按 key 过滤要为此往事件里补一列（老库还补不上）。想按流程族订阅就留空，
    /// 再用信封里的 <c>netbpmprocessdefinitionid</c> 在订阅方自己分流。
    /// </para>
    /// </summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary>回调地址（HTTP/HTTPS）。</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>
    /// 签名密钥（HMAC-SHA256）。<b>明文存</b>才能验签，回显时只给后 4 位
    /// （见 <see cref="SecretPreview"/>）—— 否则管理端列表页等于把密钥摊开给人看。
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>附加静态请求头（网关鉴权 / 路由标记之类）。</summary>
    public IDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>是否启用。停用的订阅不参与投递（保留记录便于恢复）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>单条订阅的最大投递尝试次数（超出进死信，默认 5）。</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// 单条订阅每分钟最多投递次数；<c>0</c>（默认）= 不限。
    /// <para>
    /// <b>超限是推迟，不是失败</b>：被限速挡下的投递只把下次尝试时间往后挪，
    /// 不消耗 <see cref="MaxAttempts"/> 的额度，也不改投递状态。
    /// 否则订阅方只是回得慢一点、被限速几次，就会被推进死信。
    /// </para>
    /// </summary>
    public int MaxPerMinute { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>更新时间（UTC）。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>密钥后 4 位（回显用）。</summary>
    public string SecretPreview => Secret.Length <= 4 ? "****" : $"****{Secret[^4..]}";

    /// <summary>
    /// 该事件是否命中本订阅。
    /// </summary>
    /// <remarks>
    /// 三条判据必须<b>全部</b>成立：租户相等、事件类型在白名单里（或白名单为空）、
    /// 流程 key 相符（或未限定）。
    /// </remarks>
    public bool Matches(OutboxEvent outboxEvent)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);

        if (!string.Equals(TenantId, outboxEvent.TenantId ?? "default", StringComparison.Ordinal))
        {
            return false;
        }

        if (EventTypes is { Count: > 0 } types
            && !types.Contains(outboxEvent.EventType, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return ProcessDefinitionId is null
            || string.Equals(ProcessDefinitionId, outboxEvent.ProcessDefinitionId, StringComparison.Ordinal);
    }
}

/// <summary>生成订阅密钥（32 字节 → Base64Url，够长且不含 <c>+</c>/<c>/</c>，贴进配置不转义）。</summary>
public static class EventSubscriptionSecret
{
    /// <summary>生成一个新的随机密钥。</summary>
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}

/// <summary>订阅查询条件。</summary>
/// <param name="TenantId">租户过滤；<see langword="null"/> 不限。</param>
/// <param name="Enabled">启用状态过滤；<see langword="null"/> 不限。</param>
/// <param name="MaxResults">返回条数上限。</param>
public sealed record EventSubscriptionQuery(string? TenantId = null, bool? Enabled = null, int MaxResults = 100);

/// <summary>
/// 事件订阅存储。
/// </summary>
public interface IEventSubscriptionStore
{
    /// <summary>新增订阅（id 为空时由存储层分配；密钥为空时自动生成）。</summary>
    ValueTask<EventSubscription> CreateAsync(EventSubscription subscription, CancellationToken ct = default);

    /// <summary>按 id 查；不存在返回 <see langword="null"/>。</summary>
    ValueTask<EventSubscription?> FindByIdAsync(string id, CancellationToken ct = default);

    /// <summary>按条件查。</summary>
    ValueTask<IReadOnlyList<EventSubscription>> FindAsync(
        EventSubscriptionQuery query,
        CancellationToken ct = default);

    /// <summary>整体更新（按 id）；不存在返回 <see langword="false"/>。</summary>
    ValueTask<bool> UpdateAsync(EventSubscription subscription, CancellationToken ct = default);

    /// <summary>删除；不存在返回 <see langword="false"/>。</summary>
    ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>单独切启用状态（停用保留记录，便于恢复）。</summary>
    ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
}

/// <summary>单进程订阅存储（内存宿主 / 单元测试）。</summary>
public sealed class InMemoryEventSubscriptionStore : IEventSubscriptionStore
{
    private readonly Dictionary<string, EventSubscription> _subscriptions = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<EventSubscription> CreateAsync(EventSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (string.IsNullOrWhiteSpace(subscription.Id))
        {
            subscription.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(subscription.Secret))
        {
            subscription.Secret = EventSubscriptionSecret.New();
        }

        subscription.CreatedAt = subscription.CreatedAt == default ? DateTime.UtcNow : subscription.CreatedAt;
        subscription.UpdatedAt = DateTime.UtcNow;

        _subscriptions[subscription.Id] = subscription;

        return ValueTask.FromResult(subscription);
    }

    /// <inheritdoc />
    public ValueTask<EventSubscription?> FindByIdAsync(string id, CancellationToken ct = default) =>
        ValueTask.FromResult(_subscriptions.TryGetValue(id, out var item) ? item : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<EventSubscription>> FindAsync(
        EventSubscriptionQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<EventSubscription> page = _subscriptions.Values
            .Where(item => query.TenantId is null
                || string.Equals(item.TenantId, query.TenantId, StringComparison.Ordinal))
            .Where(item => query.Enabled is null || item.Enabled == query.Enabled)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, query.MaxResults))
            .ToList();

        return ValueTask.FromResult(page);
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(EventSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (!_subscriptions.ContainsKey(subscription.Id))
        {
            return ValueTask.FromResult(false);
        }

        subscription.UpdatedAt = DateTime.UtcNow;
        _subscriptions[subscription.Id] = subscription;

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        ValueTask.FromResult(_subscriptions.Remove(id));

    /// <inheritdoc />
    public ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        if (!_subscriptions.TryGetValue(id, out var item))
        {
            return ValueTask.FromResult(false);
        }

        item.Enabled = enabled;
        item.UpdatedAt = DateTime.UtcNow;

        return ValueTask.FromResult(true);
    }
}
