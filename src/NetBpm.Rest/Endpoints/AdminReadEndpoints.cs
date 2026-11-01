using System.Diagnostics.CodeAnalysis;
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
/// 这里只放<b>读</b>端点，写路径各有归属：
/// <list type="bullet">
///   <item><description>字典写：<c>AdminDictionaryEndpoints</c>（<c>PUT/DELETE /admin/v1/dictionaries/...</c>）；</description></item>
///   <item><description>订阅写与死信重投：<c>EventSubscriptionEndpoints</c>（<c>/admin/v1/subscriptions...</c>，
///     旧面 <c>/api/admin/event-subscriptions</c> 仅兼容）；</description></item>
///   <item><description>客户端 / 租户写：<c>AdminClientEndpoints</c>。</description></item>
/// </list>
/// 读端点共用一套注册入口，避免复制五份 group + 鉴权 + 错误体。
/// </para>
/// <para>
/// <b>租户口径分两类</b>，不是随手写的：
/// <list type="bullet">
///   <item><description><b>按内容</b>（字典）：跟当前管理身份所属租户——字典是「这个租户的审批口径」，
///     跨租户并列没有意义；</description></item>
///   <item><description><b>按运营</b>（审计 / 订阅 / 死信 / 配额）：<b>未绑定租户</b>的平台凭据可以跨租户，
///     <c>?tenantId=</c> 作为可选过滤；<b>绑定了租户</b>的凭据一律收窄到自己那一个租户
///     （见 <see cref="TryResolveTenant"/>）——平台运维要能一屏看全网，租户管理员不能。</description></item>
/// </list>
/// </para>
/// <para>
/// 审计与筛选项额外要求 <c>workflow:admin:audit:read</c>，<b>不再借用</b> <c>workflow:admin:client</c>：
/// 能签发密钥不等于该看合规流水（最小权限）。
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

    /// <summary>配额总览单侧行数上限（租户 / 客户端各算一份）。</summary>
    /// <remarks>改游标分页后这只是「一页最多多少」，不再是「一次能看多少」。</remarks>
    private const int MaxQuotaRows = 500;

    /// <summary>配额总览单页默认行数。</summary>
    /// <remarks>
    /// 50 是「一屏能扫完」的量：管理端看配额是为了找异常，不是导全量。
    /// 要更多就翻页（nextCursor），而不是把 limit 调大。
    /// </remarks>
    private const int DefaultQuotaPageSize = 50;

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
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminAuditRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminAuditRead);
        }

        if (!TryResolveTenant(tenantId, http, context, out var scopedTenant, out var tenantProblem))
        {
            return tenantProblem;
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
                TenantId: scopedTenant,
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
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminAuditRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminAuditRead);
        }

        if (!TryResolveTenant(tenantId, http, context, out var scopedTenant, out var tenantProblem))
        {
            return tenantProblem;
        }

        // 窗口只影响「动作分布」这一段：下拉数据源本身是全量的，
        // 否则过完 30 天，下拉里就选不出上季度用过的动作，也就查不到那段时间的记录。
        var window = Math.Clamp(days ?? 30, 1, 365);
        var since = DateTime.UtcNow.AddDays(-window);

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

        if (!TryResolveTenant(tenantId, http, context, out var scopedTenant, out var tenantProblem))
        {
            return tenantProblem;
        }

        var subscriptions = await store.FindAsync(
            new EventSubscriptionQuery(
                TenantId: scopedTenant,
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

        if (!TryResolveTenant(tenantId, http, context, out var scopedTenant, out var tenantProblem))
        {
            return tenantProblem;
        }

        var pageSize = Math.Clamp(max ?? 20, 1, MaxDeadLetterPageSize);
        var skip = Math.Max(0, first ?? 0);

        var page = await store.ListDeadLettersAsync(
            new EventDeadLetterQuery(
                TenantId: scopedTenant,
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

        if (!TryResolveTenant(tenantId, http, context, out var tenant, out var tenantProblem))
        {
            return tenantProblem;
        }

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
        string? tenantCursor,
        string? clientCursor,
        int? max,
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

        // 配额总览是「一屏看全网」的运营视图，但绑定租户的凭据只该看到自己那一行 ——
        // 租户/客户端清单本身就是资产信息，不能靠一个 scope 换走
        if (!TryResolveTenant(null, http, context, out var scopedTenant, out var tenantProblem))
        {
            return tenantProblem;
        }

        string[]? tenantPosition;
        string[]? clientPosition;

        try
        {
            // 租户游标只有 id 一段；客户端游标是 (tenant_id, client_id) 两段（与 order by 一致）
            tenantPosition = PlatformPageCursor.Decode(tenantCursor, 1);
            clientPosition = PlatformPageCursor.Decode(clientCursor, 2);
        }
        catch (FormatException exception)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "分页游标格式不正确",
                StatusCodes.Status400BadRequest,
                exception.Message);
        }

        // 有上限而不是无限量：租户数 × 客户端数是乘积，没有上限等于允许一个请求把内存打满
        var limit = Math.Clamp(max ?? DefaultQuotaPageSize, 1, MaxQuotaRows);

        // 多取一条用来判断 hasMore：只凭「本页满了」判下一页会多出一个空页
        var tenantPage = (await tenants
            .ListPageAsync(tenantPosition?[0], limit + 1, ct)
            .ConfigureAwait(false))
            .Where(tenant => TenantScopes.IsVisible(context, tenant.TenantId))
            .ToArray();

        var clientPage = await clients
            .ListPageAsync(scopedTenant, clientPosition?[0], clientPosition?[1], limit + 1, ct)
            .ConfigureAwait(false);

        var tenantRows = tenantPage.Length > limit ? tenantPage[..limit] : tenantPage;
        var clientRows = clientPage.Count > limit ? clientPage.Take(limit).ToArray() : clientPage;

        // 数量不能从「本页」数出来：分页之后那只是本页的行数。一条 group by 拿到全量口径。
        var clientCounts = await clients
            .CountByTenantAsync(scopedTenant, ct)
            .ConfigureAwait(false);

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
            rateLimit.ApiPerMinute,
            TenantsNextCursor: tenantPage.Length > limit ? TenantCursor(tenantRows[^1].TenantId) : null,
            ClientsNextCursor: clientPage.Count > limit
                ? ClientCursor(clientRows[^1].TenantId, clientRows[^1].ClientId)
                : null));
    }

    private static string TenantCursor(string tenantId) => PlatformPageCursor.Encode(tenantId);

    private static string ClientCursor(string tenantId, string clientId) =>
        PlatformPageCursor.Encode(tenantId, clientId);

    // ─────────────────────────── 小工具 ───────────────────────────

    /// <summary>
    /// 把 <c>?tenantId=</c> 收窄到当前凭据所属租户（不传时同样落到该租户）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「跨租户运营视角」只属于<b>没有绑定租户</b>的平台凭据。凭据一旦绑定租户，
    /// 它就是一个租户管理员：查询参数不能把它变成平台运营。
    /// </para>
    /// <para>
    /// 口径与客户端管理面同一份 <see cref="TenantScopes"/>，避免「新面收窄、旧面放行」这类两面不一致。
    /// </para>
    /// </remarks>
    private static bool TryResolveTenant(
        string? requested,
        HttpContext http,
        RequestContext context,
        out string? tenantId,
        [NotNullWhen(false)] out IResult? problem) =>
        PlatformTenant.TryResolve(http, context, requested, "管理数据", out tenantId, out problem);

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

