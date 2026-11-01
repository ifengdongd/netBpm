using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Engine.Events;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 事件订阅管理端点：<c>/admin/v1/subscriptions</c>（旧路径 <c>/api/admin/event-subscriptions</c> 仅兼容）。
/// </summary>
/// <remarks>
/// <para>
/// 订阅是<b>把引擎内部的事件推给外部系统</b>的那根线：没有它，
/// <c>ACT_EVT_OUTBOX</c> 里攒着的事件只能靠订阅方自己来拉（而它根本不知道什么时候有新事件）。
/// </para>
/// <para>
/// 🔑 <b>密钥只在创建时出现一次</b>，之后只读接口一律只回 <c>secretPreview</c>（后 4 位）。
/// 订阅密钥等价于「能伪造 NetBpm 通知」的凭据，管理端列表页把它明文摊开是最常见的泄漏方式。
/// 忘了就调 <c>POST /{id}/rotate</c> 换一把。
/// </para>
/// <para>
/// 三条纪律（与客户端接入、字典写路径一致）：① 错误体一律 RFC 9457；② 租户不越界
/// （<see cref="PlatformTenant"/>）；③ 每次变更都进管理审计 —— 尤其是「谁换了密钥」。
/// </para>
/// </remarks>
public static class EventSubscriptionEndpoints
{
    private const string DefaultTenantId = "default";

    /// <summary>注册平台面的订阅管理端点（<c>/admin/v1</c>）。</summary>
    public static IEndpointRouteBuilder MapEventSubscriptionAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/admin/v1").WithTags("platform-admin");

        // 列表读已在 AdminReadEndpoints（GET /admin/v1/subscriptions）——
        // 同一条路由挂两遍会在运行时抛 AmbiguousMatchException，所以这里不再重复注册
        MapOperations(group, "subscriptions", includeList: false);

        return endpoints;
    }

    /// <summary>注册旧面的订阅管理端点（<c>/api/admin/event-subscriptions</c>）。</summary>
    /// <remarks>
    /// 只保留给已经上线的运维脚本；错误的形状、租户口径、审计与 <c>/admin/v1</c> 完全一致
    /// （共用同一批处理器），下个大版本删除。
    /// </remarks>
    [Obsolete("事件订阅的写路径已迁到 /admin/v1/subscriptions，旧路径仅保留兼容。")]
    public static IEndpointRouteBuilder MapEventSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/admin").WithTags("platform-admin");

        MapOperations(group, "event-subscriptions", includeList: true);

        return endpoints;
    }

    private static void MapOperations(RouteGroupBuilder group, string resource, bool includeList)
    {
        if (includeList)
        {
            group.MapGet($"/{resource}", ListSubscriptionsAsync)
                .Produces<IReadOnlyList<EventSubscriptionView>>();
        }

        group.MapPost($"/{resource}", CreateSubscriptionAsync)
            .Accepts<CreateEventSubscriptionRequest>("application/json")
            // 响应体含一次性明文密钥：退出幂等缓存（见 NoIdempotencyMetadata）
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<CreatedEventSubscription>(StatusCodes.Status201Created)
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest);

        group.MapGet($"/{resource}/{{subscriptionId}}", FindSubscriptionAsync)
            .Produces<EventSubscriptionView>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPatch($"/{resource}/{{subscriptionId}}", UpdateSubscriptionAsync)
            .Accepts<UpdateEventSubscriptionRequest>("application/json")
            .Produces<EventSubscriptionView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapDelete($"/{resource}/{{subscriptionId}}", DeleteSubscriptionAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPost($"/{resource}/{{subscriptionId}}/status", SetSubscriptionStatusAsync)
            .Produces<EventSubscriptionView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        // 换密钥：旧密钥立刻失效，正在投递中的请求会用新密钥重签（下一次重试生效）
        group.MapPost($"/{resource}/{{subscriptionId}}/rotate", RotateSubscriptionSecretAsync)
            // 响应体含一次性明文密钥：退出幂等缓存
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<IssuedEventSubscriptionSecret>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        // 投递记录：回答「这条事件到底有没有送到」
        group.MapGet($"/{resource}/{{subscriptionId}}/deliveries", ListDeliveriesAsync)
            .Produces<IReadOnlyList<EventDeliveryView>>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPost($"/events/{{eventId}}/deliveries/{{subscriptionId}}/retry", RetryDeliveryAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListSubscriptionsAsync(
        HttpContext http,
        string? tenantId,
        bool? enabled,
        int? maxResults,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        if (!PlatformTenant.TryResolve(http, context, tenantId, "事件订阅", out var scopedTenant, out var problem))
        {
            return problem;
        }

        var items = await store.FindAsync(
            new EventSubscriptionQuery(
                scopedTenant,
                enabled,
                maxResults is > 0 ? Math.Min(maxResults.Value, 200) : 100),
            ct).ConfigureAwait(false);

        return Results.Ok(items.Select(EventSubscriptionView.From).ToArray());
    }

    private static async Task<IResult> CreateSubscriptionAsync(
        HttpContext http,
        CreateEventSubscriptionRequest? request,
        RequestContext context,
        IEventSubscriptionStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        if (request is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestBodyRequired,
                "缺少请求体",
                StatusCodes.Status400BadRequest,
                "请求体不能为空（至少要有 name 与 endpointUrl）。");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "请求无效",
                StatusCodes.Status400BadRequest,
                "name 不能为空：它是管理端区分订阅的唯一标识。");
        }

        if (!TryValidateEndpoint(request.EndpointUrl, out var endpointDetail))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "请求无效",
                StatusCodes.Status400BadRequest,
                endpointDetail);
        }

        // 租户不能越出凭据所属范围：否则一个租户的凭据就能往任意租户的事件上挂回调
        if (!PlatformTenant.TryResolve(http, context, request.TenantId, "事件订阅", out var tenantId, out var problem))
        {
            return problem;
        }

        var subscription = new EventSubscription
        {
            TenantId = tenantId ?? DefaultTenantId,
            Name = request.Name.Trim(),
            EventTypes = NormalizeTypes(request.EventTypes),
            ProcessDefinitionId = request.ProcessDefinitionId,
            EndpointUrl = request.EndpointUrl.Trim(),
            Secret = string.IsNullOrWhiteSpace(request.Secret)
                ? string.Empty
                : request.Secret.Trim(),
            Headers = request.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Enabled = request.Enabled ?? true,
            MaxAttempts = request.MaxAttempts is > 0 ? Math.Min(request.MaxAttempts.Value, 20) : 5,
            MaxPerMinute = NormalizePerMinute(request.MaxPerMinute),
        };

        var created = await store.CreateAsync(subscription, ct).ConfigureAwait(false);

        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventSubscriptionCreate,
            AdminAuditTargets.EventSubscription,
            created.Id,
            detail: new
            {
                created.TenantId,
                created.Name,
                Endpoint = HostOf(created.EndpointUrl),
                created.EventTypes,
                created.ProcessDefinitionId,
                created.Enabled,
                created.MaxAttempts,
                created.MaxPerMinute,
                // 只记「是不是自定义密钥」，不记密钥本身
                CustomSecret = !string.IsNullOrEmpty(request.Secret),
                Caller = context.CallerId,
            },
            ct: ct).ConfigureAwait(false);

        // 明文密钥只在这一次响应里出现
        return Results.Created(
            $"/admin/v1/subscriptions/{created.Id}",
            new CreatedEventSubscription(EventSubscriptionView.From(created), created.Secret));
    }

    private static async Task<IResult> FindSubscriptionAsync(
        string subscriptionId,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var subscription = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        return subscription is null || !TenantScopes.IsVisible(context, subscription.TenantId)
            ? NotFound(http, context, subscriptionId)
            : Results.Ok(EventSubscriptionView.From(subscription));
    }

    private static async Task<IResult> UpdateSubscriptionAsync(
        string subscriptionId,
        UpdateEventSubscriptionRequest? request,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        if (request is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestBodyRequired,
                "缺少请求体",
                StatusCodes.Status400BadRequest,
                "PATCH 至少要带一个待修改字段。");
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        if (request.Name is { Length: > 0 } name)
        {
            existing.Name = name.Trim();
        }

        if (request.EventTypes is { } types)
        {
            existing.EventTypes = NormalizeTypes(types);
        }

        if (request.ProcessDefinitionId is { } definitionId)
        {
            existing.ProcessDefinitionId = string.IsNullOrWhiteSpace(definitionId) ? null : definitionId.Trim();
        }

        if (request.EndpointUrl is { Length: > 0 } url)
        {
            if (!TryValidateEndpoint(url, out var endpointDetail))
            {
                return PlatformProblems.Create(
                    http,
                    context,
                    ErrorCodes.RequestMalformed,
                    "请求无效",
                    StatusCodes.Status400BadRequest,
                    endpointDetail);
            }

            existing.EndpointUrl = url.Trim();
        }

        // 🔑 secret 不接受通过 PATCH 修改：走 /rotate 才有明确的「换密钥」语义与审计点，
        // 混在通用更新里容易出现「以为改了地址结果把密钥也换了」的事故
        if (request.Headers is { } headers)
        {
            existing.Headers = headers;
        }

        if (request.Enabled is { } enabled)
        {
            existing.Enabled = enabled;
        }

        if (request.MaxAttempts is > 0)
        {
            existing.MaxAttempts = Math.Min(request.MaxAttempts.Value, 20);
        }

        // 显式传 0 是「取消限速」这个有效指令，因此判 null 而不是判 > 0：
        // 否则一旦配错上限就再也改不回来，只能直接改库
        if (request.MaxPerMinute is { } perMinute)
        {
            existing.MaxPerMinute = NormalizePerMinute(perMinute);
        }

        await store.UpdateAsync(existing, ct).ConfigureAwait(false);

        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventSubscriptionUpdate,
            AdminAuditTargets.EventSubscription,
            subscriptionId,
            detail: new
            {
                existing.Name,
                Endpoint = HostOf(existing.EndpointUrl),
                existing.EventTypes,
                existing.ProcessDefinitionId,
                existing.Enabled,
                existing.MaxAttempts,
                existing.MaxPerMinute,
                HeaderKeys = existing.Headers.Keys.ToArray(),
                Caller = context.CallerId,
            },
            ct: ct).ConfigureAwait(false);

        return Results.Ok(EventSubscriptionView.From(existing));
    }

    private static async Task<IResult> DeleteSubscriptionAsync(
        string subscriptionId,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        await store.DeleteAsync(subscriptionId, ct).ConfigureAwait(false);

        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventSubscriptionDelete,
            AdminAuditTargets.EventSubscription,
            subscriptionId,
            detail: new
            {
                existing.TenantId,
                existing.Name,
                Endpoint = HostOf(existing.EndpointUrl),
                Caller = context.CallerId,
            },
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> SetSubscriptionStatusAsync(
        string subscriptionId,
        bool? enabled,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        if (enabled is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "请求无效",
                StatusCodes.Status400BadRequest,
                "查询参数 enabled 不能为空（true/false）。");
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        if (!await store.SetEnabledAsync(subscriptionId, enabled.Value, ct).ConfigureAwait(false))
        {
            return NotFound(http, context, subscriptionId);
        }

        existing.Enabled = enabled.Value;

        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventSubscriptionStatus,
            AdminAuditTargets.EventSubscription,
            subscriptionId,
            detail: new { existing.Name, existing.Enabled, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.Ok(EventSubscriptionView.From(existing));
    }

    private static async Task<IResult> RotateSubscriptionSecretAsync(
        string subscriptionId,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        existing.Secret = EventSubscriptionSecret.New();

        await store.UpdateAsync(existing, ct).ConfigureAwait(false);

        // 审计只记「谁换了、换了哪一条」—— 密钥本身绝不进审计表
        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventSubscriptionRotate,
            AdminAuditTargets.EventSubscription,
            subscriptionId,
            detail: new { existing.TenantId, existing.Name, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.Ok(new IssuedEventSubscriptionSecret(subscriptionId, existing.Secret));
    }

    private static async Task<IResult> ListDeliveriesAsync(
        string subscriptionId,
        string? eventId,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var subscription = await subscriptions.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (subscription is null || !TenantScopes.IsVisible(context, subscription.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        // 存储侧只有「按事件查全部订阅」的索引；按订阅查要全表扫，因此 eventId 是必填条件
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "请求无效",
                StatusCodes.Status400BadRequest,
                "查询参数 eventId 不能为空（投递记录按事件归集，不提供会退化成全表扫描）。");
        }

        var items = await deliveries.FindByEventAsync(eventId, ct).ConfigureAwait(false);

        return Results.Ok(items
            .Where(item => string.Equals(item.SubscriptionId, subscriptionId, StringComparison.Ordinal))
            .Select(EventDeliveryView.From)
            .ToArray());
    }

    private static async Task<IResult> RetryDeliveryAsync(
        string eventId,
        string subscriptionId,
        HttpContext http,
        RequestContext context,
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.EventSubscribe))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.EventSubscribe);
        }

        var subscription = await subscriptions.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (subscription is null || !TenantScopes.IsVisible(context, subscription.TenantId))
        {
            return NotFound(http, context, subscriptionId);
        }

        if (await deliveries.FindAsync(eventId, subscriptionId, ct).ConfigureAwait(false) is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.EventDeliveryNotFound,
                "投递记录不存在",
                StatusCodes.Status404NotFound,
                $"事件 '{eventId}' 对订阅 '{subscriptionId}' 没有投递记录。");
        }

        if (!await deliveries.RetryAsync(eventId, subscriptionId, DateTime.UtcNow, ct).ConfigureAwait(false))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.EventDeliveryNotDeadLetter,
                "投递状态冲突",
                StatusCodes.Status409Conflict,
                $"该投递不是 {EventDeliveryStatus.DeadLetter} 状态，无需重投。");
        }

        await auditor.RecordAsync(
            context,
            AdminAuditActions.EventDeliveryRetry,
            AdminAuditTargets.EventSubscription,
            subscriptionId,
            detail: new { EventId = eventId, subscription.TenantId, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>订阅不存在与「不属于当前租户」共用一个 404，避免给枚举 id 的接口。</summary>
    private static IResult NotFound(HttpContext http, RequestContext context, string subscriptionId) =>
        PlatformProblems.Create(
            http,
            context,
            ErrorCodes.EventSubscriptionNotFound,
            "事件订阅不存在",
            StatusCodes.Status404NotFound,
            $"事件订阅 '{subscriptionId}' 不存在，或不属于当前凭据的租户。");

    private static bool TryValidateEndpoint(string? endpointUrl, out string detail)
    {
        if (!string.IsNullOrWhiteSpace(endpointUrl)
            && Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            detail = string.Empty;
            return true;
        }

        detail = "endpointUrl 必须是 http/https 绝对地址。";
        return false;
    }

    /// <summary>
    /// 审计里记的「地址」：只到路径，去掉查询串。
    /// </summary>
    /// <remarks>
    /// 回调地址常常带 <c>?token=...</c> 这类凭据，整串进审计表等于把密钥换个地方存。
    /// </remarks>
    private static string HostOf(string endpointUrl) =>
        Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : endpointUrl;

    private static string[] NormalizeTypes(IReadOnlyList<string>? types) =>
        types?.Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    /// <summary>
    /// 每分钟上限的夹取：<c>&lt;= 0</c> 归一到 0（不限），正数封顶 6000（100 条/秒）。
    /// </summary>
    /// <remarks>
    /// 上限存在的意义是挡住「手滑多打两个零」：配成 100000 会让限速形同虚设，
    /// 而配错的限速比没配更危险 —— 运维以为自己在保护订阅方，实际什么都没保护。
    /// </remarks>
    private static int NormalizePerMinute(int? perMinute) =>
        perMinute is > 0 ? Math.Min(perMinute.Value, 6000) : 0;
}

/// <summary>创建订阅的请求。</summary>
/// <param name="Name">订阅名（管理端显示用）。</param>
/// <param name="EndpointUrl">回调地址。</param>
/// <param name="TenantId">租户；为空用凭据所属租户，再退到 <c>default</c>。</param>
/// <param name="EventTypes">关心的引擎事件类型；空 = 全部。</param>
/// <param name="ProcessDefinitionId">只关心该流程定义 id 的事件；空 = 不限。</param>
/// <param name="Secret">自定义签名密钥；为空由服务端生成（推荐）。</param>
/// <param name="Headers">附加静态请求头。</param>
/// <param name="Enabled">是否启用（默认 true）。</param>
/// <param name="MaxAttempts">最大投递尝试次数（默认 5，上限 20）。</param>
/// <param name="MaxPerMinute">每分钟最多投递次数（默认 0 = 不限，上限 6000）。超限是推迟不是失败。</param>
public sealed record CreateEventSubscriptionRequest(
    string Name,
    string EndpointUrl,
    string? TenantId = null,
    IReadOnlyList<string>? EventTypes = null,
    string? ProcessDefinitionId = null,
    string? Secret = null,
    IDictionary<string, string>? Headers = null,
    bool? Enabled = null,
    int? MaxAttempts = null,
    int? MaxPerMinute = null);

/// <summary>更新订阅的请求（<b>不含 secret</b> —— 换密钥走 <c>/rotate</c>）。</summary>
/// <param name="Name">订阅名。</param>
/// <param name="EventTypes">事件类型白名单。</param>
/// <param name="ProcessDefinitionId">流程定义 id 过滤。</param>
/// <param name="EndpointUrl">回调地址。</param>
/// <param name="Headers">附加静态请求头。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="MaxAttempts">最大投递尝试次数。</param>
/// <param name="MaxPerMinute">每分钟最多投递次数；显式传 0 = 取消限速。</param>
public sealed record UpdateEventSubscriptionRequest(
    string? Name = null,
    IReadOnlyList<string>? EventTypes = null,
    string? ProcessDefinitionId = null,
    string? EndpointUrl = null,
    IDictionary<string, string>? Headers = null,
    bool? Enabled = null,
    int? MaxAttempts = null,
    int? MaxPerMinute = null);

/// <summary>
/// 订阅视图。<b>不含明文密钥</b> —— 只有 <see cref="SecretPreview"/>。
/// </summary>
/// <param name="Id">订阅 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="Name">订阅名。</param>
/// <param name="EventTypes">事件类型白名单（空 = 全部）。</param>
/// <param name="ProcessDefinitionId">流程定义 id 过滤。</param>
/// <param name="EndpointUrl">回调地址。</param>
/// <param name="SecretPreview">密钥后 4 位。</param>
/// <param name="Headers">附加静态请求头。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="MaxAttempts">最大投递尝试次数。</param>
/// <param name="MaxPerMinute">每分钟最多投递次数（0 = 不限）。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
public sealed record EventSubscriptionView(
    string Id,
    string TenantId,
    string Name,
    IReadOnlyList<string> EventTypes,
    string? ProcessDefinitionId,
    string EndpointUrl,
    string SecretPreview,
    IReadOnlyDictionary<string, string> Headers,
    bool Enabled,
    int MaxAttempts,
    int MaxPerMinute,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>从实体构造（明文密钥不会出现在视图里）。</summary>
    public static EventSubscriptionView From(EventSubscription subscription) => new(
        subscription.Id,
        subscription.TenantId,
        subscription.Name,
        subscription.EventTypes,
        subscription.ProcessDefinitionId,
        subscription.EndpointUrl,
        subscription.SecretPreview,
        new Dictionary<string, string>(subscription.Headers, StringComparer.Ordinal),
        subscription.Enabled,
        subscription.MaxAttempts,
        subscription.MaxPerMinute,
        subscription.CreatedAt,
        subscription.UpdatedAt);
}

/// <summary>创建订阅的响应（<b>唯一一次</b>给出明文密钥）。</summary>
/// <param name="Subscription">订阅视图。</param>
/// <param name="Secret">明文签名密钥，只在本次响应中出现。</param>
public sealed record CreatedEventSubscription(EventSubscriptionView Subscription, string Secret);

/// <summary>轮换密钥的响应。</summary>
/// <param name="SubscriptionId">订阅 id。</param>
/// <param name="Secret">新的明文密钥。</param>
public sealed record IssuedEventSubscriptionSecret(string SubscriptionId, string Secret);

/// <summary>投递记录视图。</summary>
/// <param name="EventId">事件 id。</param>
/// <param name="SubscriptionId">订阅 id。</param>
/// <param name="TenantId">租户。</param>
/// <param name="Status">状态。</param>
/// <param name="Attempts">已尝试次数。</param>
/// <param name="LastError">最近失败原因。</param>
/// <param name="NextAttemptAt">下次可投时间。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
/// <param name="DeliveredAt">投递成功时间。</param>
public sealed record EventDeliveryView(
    string EventId,
    string SubscriptionId,
    string TenantId,
    string Status,
    int Attempts,
    string? LastError,
    DateTime? NextAttemptAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeliveredAt)
{
    /// <summary>从实体构造。</summary>
    public static EventDeliveryView From(EventDelivery delivery) => new(
        delivery.EventId,
        delivery.SubscriptionId,
        delivery.TenantId,
        delivery.Status,
        delivery.Attempts,
        delivery.LastError,
        delivery.NextAttemptAt,
        delivery.CreatedAt,
        delivery.UpdatedAt,
        delivery.DeliveredAt);
}
