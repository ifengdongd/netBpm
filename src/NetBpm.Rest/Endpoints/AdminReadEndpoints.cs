using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Engine.Events;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.RateLimiting;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 管理面：<b>只读观测</b>（<c>/admin/v1</c> 的字典、审计、订阅、死信、配额）。
/// </summary>
/// <remarks>
/// <para>
/// 这五个面的共同点是「只能看，不能改」，因此共用一套注册入口：
/// 各自的写路径要么已经在别处（订阅在 <c>/api/admin/event-subscriptions</c>，死信重投同理），
/// 要么这一版就不打算开（字典的写路径要连带 <c>IApprovalVocabulary</c> 的三层合并一起再想一遍）。
/// 分五个文件注册只会复制五份 group + 鉴权 + 错误体。
/// </para>
/// <para>
/// <b>租户口径分两类</b>，不是随手写的：
/// <list type="bullet">
///   <item><description><b>按内容</b>（字典）：跟当前管理身份所属租户——字典是「这个租户的审批口径」，
///     跨租户并列没有意义；</description></item>
///   <item><description><b>按运营</b>（审计 / 订阅 / 死信 / 配额）：跨租户，<c>?tenantId=</c> 作为可选过滤——
///     平台运维要能在一屏里看到「哪个租户在狂发事件」，而不是先切租户再逐个看。</description></item>
/// </list>
/// </para>
/// <para>
/// 🔑 <b>读端点不写审计</b>。审计是「谁改了东西」的流水，把读操作也记进去，
/// 真正的干预动作会被翻页噪声淹掉——想看读行为应该去访问日志，那里有 traceId。
/// </para>
/// </remarks>
public static class AdminReadEndpoints
{
    private const string DefaultTenantId = "default";

    /// <summary>审计页大小上限。</summary>
    private const int MaxAuditPageSize = 200;

    /// <summary>死信页大小上限。</summary>
    private const int MaxDeadLetterPageSize = 200;

    /// <summary>订阅列表上限（订阅是配置数据，几百条已经很夸张）。</summary>
    private const int MaxSubscriptionCount = 500;

    /// <summary>注册端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapAdminReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/admin/v1").WithTags("platform-admin-read");

        // 字典：读流程的审批口径（BPMN 里只有 activiti:value，语义在这张表里）
        group.MapGet("/dictionaries", ListDictionariesAsync)
            .Produces<IReadOnlyList<ApprovalDictionaryEntryView>>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 审计：管理动作的完整流水（分页靠 offset，因为「一共多少条」对合规核对有用）
        group.MapGet("/audit", QueryAuditAsync)
            .Produces<AdminAuditPageView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 审计筛选项 + 动作分布：与分页分开，避免每翻一页重复传几十个字符串
        group.MapGet("/audit/facets", GetAuditFacetsAsync)
            .Produces<AdminAuditFacetsView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 订阅：只读投影（写路径在 /api/admin/event-subscriptions，密钥只在创建时出现一次）
        group.MapGet("/subscriptions", ListSubscriptionsAsync)
            .Produces<IReadOnlyList<EventSubscriptionView>>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 死信：事件投递失败到上限的全部记录（重投仍走 /api/admin/events/.../retry）
        group.MapGet("/events/deadletters", ListDeadLettersAsync)
            .Produces<AdminDeadLetterPageView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 看板：outbox 积压 + 每订阅投递结果分布（聚合在库里 group by，不取回全表再算）
        group.MapGet("/events/stats", GetEventStatsAsync)
            .Produces<AdminEventStatsView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 配额：租户默认值 + 客户端实际值，一次取回两层
        group.MapGet("/quotas", GetQuotasAsync)
            .Produces<QuotaOverviewView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    // ─────────────────────────── 审批语义字典 ───────────────────────────

    private static async Task<IResult> ListDictionariesAsync(
        string? processKey,
        HttpContext http,
        RequestContext context,
        IApprovalDictionaryStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminProcess))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminProcess);
        }

        if (string.IsNullOrWhiteSpace(processKey))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "缺少 processKey",
                StatusCodes.Status400BadRequest,
                "字典的粒度是「某流程的审批口径」，请带 ?processKey=... 查询。"
                + "跨流程总览需要按 tenant_id 单独建索引，等有真实需求再加。");
        }

        var tenantId = string.IsNullOrWhiteSpace(context.EffectiveTenantId)
            ? DefaultTenantId
            : context.EffectiveTenantId;

        var entries = await store.ListAsync(tenantId, processKey.Trim(), ct).ConfigureAwait(false);

        return Results.Ok(entries.Select(ToDictionaryView).ToArray());
    }

    private static ApprovalDictionaryEntryView ToDictionaryView(ApprovalDictionaryEntry entry) => new(
        entry.TenantId,
        entry.ProcessKey,
        entry.NodeId,
        entry.OptionId,
        entry.Semantic,
        entry.OutcomeClass,
        entry.Label,
        entry.RequiresComment,
        entry.RequiresTargetNode,
        entry.AllowedTargetNodes ?? [],
        entry.Attributes ?? new Dictionary<string, string>(StringComparer.Ordinal),
        entry.CreatedAt,
        entry.UpdatedAt);

    // ─────────────────────────── 管理动作审计 ───────────────────────────

    private static async Task<IResult> QueryAuditAsync(
        string? actor,
        string? action,
        string? targetType,
        string? targetId,
        string? processInstanceId,
        string? tenantId,
        bool? success,
        string? since,
        string? until,
        int? first,
        int? max,
        HttpContext http,
        RequestContext context,
        IAdminAuditStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        if (!TryReadInstant(since, "since", out var sinceUtc, out var problem)
            || !TryReadInstant(until, "until", out var untilUtc, out problem))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "时间参数无法解析",
                StatusCodes.Status400BadRequest,
                problem);
        }

        var pageSize = Math.Clamp(max ?? 20, 1, MaxAuditPageSize);
        var skip = Math.Max(0, first ?? 0);

        var page = await store.QueryAsync(
            new AdminAuditQuery(
                Actor: Trimmed(actor),
                Action: Trimmed(action),
                TargetType: Trimmed(targetType),
                TargetId: Trimmed(targetId),
                ProcessInstanceId: Trimmed(processInstanceId),
                TenantId: Trimmed(tenantId),
                Success: success,
                Since: sinceUtc,
                Until: untilUtc,
                FirstResult: skip,
                MaxResults: pageSize),
            ct).ConfigureAwait(false);

        return Results.Ok(new AdminAuditPageView(
            [.. page.Items.Select(ToAuditView)],
            page.Total,
            skip,
            pageSize));
    }

    private static async Task<IResult> GetAuditFacetsAsync(
        int? days,
        string? tenantId,
        HttpContext http,
        RequestContext context,
        IAdminAuditStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        // 窗口只影响「动作分布」这一段：下拉数据源本身是全量的，
        // 否则过完 30 天，下拉里就选不出上季度用过的动作，也就查不到那段时间的记录。
        var window = Math.Clamp(days ?? 30, 1, 365);
        var since = DateTime.UtcNow.AddDays(-window);
        var scopedTenant = Trimmed(tenantId);

        var options = await store.GetOptionsAsync(scopedTenant, ct).ConfigureAwait(false);
        var counts = await store.SummarizeAsync(scopedTenant, since, ct).ConfigureAwait(false);

        return Results.Ok(new AdminAuditFacetsView(
            options.Actions,
            options.TargetTypes,
            options.Actors,
            [.. counts.Select(item => new AdminAuditActionCount(item.Action, item.Count))]));
    }

    private static AdminAuditEntryView ToAuditView(AdminAuditEntry entry) => new(
        entry.Id,
        entry.TenantId,
        entry.Actor,
        entry.AuthMode,
        entry.Action,
        entry.TargetType,
        entry.TargetId,
        entry.ProcessInstanceId,
        entry.Detail,
        entry.Success,
        entry.Error,
        AsUtc(entry.CreatedAt));

    // ─────────────────────────── 事件订阅（只读） ───────────────────────────

    private static async Task<IResult> ListSubscriptionsAsync(
        string? tenantId,
        bool? enabled,
        int? max,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var subscriptions = await store.FindAsync(
            new EventSubscriptionQuery(
                TenantId: Trimmed(tenantId),
                Enabled: enabled,
                MaxResults: Math.Clamp(max ?? 100, 1, MaxSubscriptionCount)),
            ct).ConfigureAwait(false);

        return Results.Ok(subscriptions.Select(EventSubscriptionView.From).ToArray());
    }

    // ─────────────────────────── 事件死信 ───────────────────────────

    private static async Task<IResult> ListDeadLettersAsync(
        string? tenantId,
        string? subscriptionId,
        int? first,
        int? max,
        HttpContext http,
        RequestContext context,
        IEventDeliveryStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var pageSize = Math.Clamp(max ?? 20, 1, MaxDeadLetterPageSize);
        var skip = Math.Max(0, first ?? 0);

        var page = await store.ListDeadLettersAsync(
            new EventDeadLetterQuery(
                TenantId: Trimmed(tenantId),
                SubscriptionId: Trimmed(subscriptionId),
                FirstResult: skip,
                MaxResults: pageSize),
            ct).ConfigureAwait(false);

        return Results.Ok(new AdminDeadLetterPageView(
            [.. page.Items.Select(EventDeliveryView.From)],
            page.Total,
            skip,
            pageSize));
    }

    // ─────────────────────────── 事件看板 ───────────────────────────

    private static async Task<IResult> GetEventStatsAsync(
        string? tenantId,
        string? subscriptionId,
        string? since,
        int? max,
        HttpContext http,
        RequestContext context,
        IEventOutboxStore outbox,
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        if (!TryReadInstant(since, "since", out var sinceUtc, out var problem))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "since 不合法",
                StatusCodes.Status400BadRequest,
                problem);
        }

        var tenant = Trimmed(tenantId);
        var subscription = Trimmed(subscriptionId);
        var cap = Math.Clamp(max ?? 100, 1, MaxSubscriptionCount);

        // 积压看两条「会卡住」的：还没发出去的、已经判死的。
        // 不加 published 的计数 —— 那张表发完即删，统计「历史发了多少」既没意义又全表扫
        var pending = await outbox
            .CountAsync(new OutboxQuery(Published: false, DeadLetter: false, TenantId: tenant), ct)
            .ConfigureAwait(false);

        var deadLetters = await outbox
            .CountAsync(new OutboxQuery(Published: false, DeadLetter: true, TenantId: tenant), ct)
            .ConfigureAwait(false);

        // 最老的一条待发布事件：积压「多少条」看不出「卡了多久」，两个数一起才有判断力
        var oldest = await outbox
            .FindAsync(new OutboxQuery(Published: false, DeadLetter: false, MaxResults: 1, TenantId: tenant), ct)
            .ConfigureAwait(false);

        var stats = await deliveries
            .GetStatsAsync(new EventDeliveryStatsQuery(tenant, subscription, sinceUtc), ct)
            .ConfigureAwait(false);

        var bySubscription = stats.ToDictionary(item => item.SubscriptionId, StringComparer.Ordinal);

        var known = await subscriptions
            .FindAsync(new EventSubscriptionQuery(tenant, null, cap), ct)
            .ConfigureAwait(false);

        var rows = new List<AdminSubscriptionStatsView>();

        foreach (var item in known)
        {
            if (subscription is not null && !string.Equals(item.Id, subscription, StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(BuildSubscriptionStats(
                item.Id,
                item.Name,
                item.TenantId,
                item.MaxPerMinute,
                item.Enabled,
                bySubscription.TryGetValue(item.Id, out var found) ? found : null));
        }

        // 投递记录里的订阅已经不在列表里（被删了，或超出 cap）：仍然要露出来，
        // 否则「有个订阅挂着几十条死信」会因为它的配置被删而从此看不见
        var listed = rows.Select(row => row.SubscriptionId).ToHashSet(StringComparer.Ordinal);

        foreach (var item in stats.Where(item => !listed.Contains(item.SubscriptionId)))
        {
            rows.Add(BuildSubscriptionStats(null, null, item.TenantId, null, null, item));
        }

        return Results.Ok(new AdminEventStatsView(
            sinceUtc is { } instant ? AsUtc(instant) : null,
            new AdminOutboxBacklogView(
                pending,
                deadLetters,
                oldest.Count > 0 && oldest[0].CreateTime != default ? AsUtc(oldest[0].CreateTime) : null),
            [.. rows
                .OrderByDescending(row => row.DeadLetter)
                .ThenByDescending(row => row.Failed)
                .ThenBy(row => row.SubscriptionId, StringComparer.Ordinal)]));
    }

    private static AdminSubscriptionStatsView BuildSubscriptionStats(
        string? subscriptionId,
        string? name,
        string tenantId,
        int? maxPerMinute,
        bool? enabled,
        EventDeliveryStats? stats) =>
        new(
            subscriptionId ?? stats?.SubscriptionId ?? string.Empty,
            name,
            tenantId,
            maxPerMinute,
            enabled,
            stats?.Total ?? 0,
            stats?.Pending ?? 0,
            stats?.Delivered ?? 0,
            stats?.Failed ?? 0,
            stats?.DeadLetter ?? 0,
            stats?.LastDeliveredAt,
            stats?.LastFailureAt);

    // ─────────────────────────── 配额 ───────────────────────────

    private static async Task<IResult> GetQuotasAsync(
        HttpContext http,
        RequestContext context,
        ITenantDirectory tenants,
        IClientDirectory clients,
        ClientRateLimitOptions rateLimit,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var tenantRows = await tenants.ListAsync(ct).ConfigureAwait(false);
        var clientRows = await clients.ListAsync(tenantId: null, ct).ConfigureAwait(false);

        var clientCounts = clientRows
            .GroupBy(client => client.TenantId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return Results.Ok(new QuotaOverviewView(
            [.. tenantRows.Select(tenant => new TenantQuotaView(
                tenant.TenantId,
                tenant.Name,
                tenant.Status == TenantStatus.Suspended ? "SUSPENDED" : "ACTIVE",
                tenant.QuotaPerMinute,
                clientCounts.TryGetValue(tenant.TenantId, out var count) ? count : 0,
                tenant.CreatedAt))],
            [.. clientRows.Select(client => new ClientQuotaView(
                client.TenantId,
                client.ClientId,
                client.Name,
                client.Status.ToString().ToUpperInvariant(),
                client.QuotaPerMinute,
                client.Scopes,
                client.AllowedProcessKeys,
                client.SecretExpiresAt))],
            rateLimit.Enabled,
            rateLimit.ApiPerMinute));
    }

    // ─────────────────────────── 小工具 ───────────────────────────

    /// <summary>空串一律当「没给」，免得 <c>?actor=</c> 变成「查 actor 为空串的记录」。</summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 解析时间参数（ISO 8601）。
    /// </summary>
    /// <remarks>
    /// 🔑 用 <see cref="DateTimeOffset"/> 而不是 <see cref="DateTime"/> 解析：
    /// 不带偏移的 <c>2026-11-01T00:00:00</c> 按 <see cref="DateTimeKind.Unspecified"/> 处理，
    /// 之后和 UTC 的审计时间比大小就成了「按字面比」，服务器时区一变结果就变。
    /// 这里显式 <c>AssumeUniversal</c> + <c>AdjustToUniversal</c>：没有偏移就当 UTC，有偏移就换算成 UTC。
    /// </remarks>
    private static bool TryReadInstant(
        string? text,
        string field,
        out DateTime? value,
        out string problem)
    {
        value = null;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            problem = $"{field}='{text}' 不是合法的 ISO 8601 时间（例：2026-11-01T00:00:00Z）。";
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    /// <summary>库里的时间列是 UTC（Kind 可能丢失），补回 Kind 再转 offset。</summary>
    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

/// <summary>
/// 死信分页结果（<c>GET /admin/v1/events/deadletters</c>）。
/// </summary>
/// <remarks>
/// 元素复用 <see cref="EventDeliveryView"/>，与 <c>/api/admin/event-subscriptions/{id}/deliveries</c>
/// 是同一个形状：同一条投递记录在两个入口不该长得不一样。
/// </remarks>
/// <param name="Items">当前页（按更新时间倒序）。</param>
/// <param name="Total">满足条件的总数。</param>
/// <param name="First">跳过的条数（回显请求值）。</param>
/// <param name="Max">页大小（回显<b>夹取后</b>的值）。</param>
public sealed record AdminDeadLetterPageView(
    IReadOnlyList<EventDeliveryView> Items,
    int Total,
    int First,
    int Max);

/// <summary>
/// 事件看板（<c>GET /admin/v1/events/stats</c>）。
/// </summary>
/// <param name="Since">统计起点（回显请求值；<see langword="null"/> = 全量）。</param>
/// <param name="Outbox">outbox 侧的积压。</param>
/// <param name="Subscriptions">每订阅的投递结果分布（死信多的排前面）。</param>
public sealed record AdminEventStatsView(
    DateTimeOffset? Since,
    AdminOutboxBacklogView Outbox,
    IReadOnlyList<AdminSubscriptionStatsView> Subscriptions);

/// <summary>outbox 积压。</summary>
/// <remarks>
/// 只看「还没发出去」和「已经判死」两类：已发布的行发完即删，
/// 统计「历史上发了多少」既要全表扫又没有判断价值。
/// </remarks>
/// <param name="Pending">待发布（含正在退避的）。</param>
/// <param name="DeadLetter">已判死（需人工重投）。</param>
/// <param name="OldestPendingAt">最老一条待发布事件的创建时间；<see langword="null"/> = 没有积压。</param>
public sealed record AdminOutboxBacklogView(int Pending, int DeadLetter, DateTimeOffset? OldestPendingAt);

/// <summary>单订阅的投递结果分布。</summary>
/// <remarks>
/// <para>
/// 🔑 <see cref="Failed"/> 与 <see cref="DeadLetter"/> 分开：前者还有重试额度（会自愈），
/// 后者已经停投（要人管）。合成一个「失败数」会让看板失去行动指向。
/// </para>
/// <para>
/// <see cref="Name"/> / <see cref="Enabled"/> / <see cref="MaxPerMinute"/> 为
/// <see langword="null"/> 表示订阅配置已经不在了（被删或超出本页上限），
/// 但投递记录还在——这类行恰恰要看，否则「配置删了、死信留一堆」的情况永远发现不了。
/// </para>
/// </remarks>
/// <param name="SubscriptionId">订阅 id。</param>
/// <param name="Name">订阅名（配置已删则为 <see langword="null"/>）。</param>
/// <param name="TenantId">租户。</param>
/// <param name="MaxPerMinute">每分钟上限（0 = 不限）。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="Total">投递记录数（= 事件数 × 命中的订阅数）。</param>
/// <param name="Pending">尚未投成（没试过 / 退避中 / 被限速推迟）。</param>
/// <param name="Delivered">已成功。</param>
/// <param name="Failed">失败但还有重试额度。</param>
/// <param name="DeadLetter">已进死信。</param>
/// <param name="LastDeliveredAt">最近一次成功时间。</param>
/// <param name="LastFailureAt">最近一次失败时间（含死信）。</param>
public sealed record AdminSubscriptionStatsView(
    string SubscriptionId,
    string? Name,
    string TenantId,
    int? MaxPerMinute,
    bool? Enabled,
    int Total,
    int Pending,
    int Delivered,
    int Failed,
    int DeadLetter,
    DateTimeOffset? LastDeliveredAt,
    DateTimeOffset? LastFailureAt);

