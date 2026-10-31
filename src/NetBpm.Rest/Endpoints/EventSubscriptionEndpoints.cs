using NetBpm.Engine.Events;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 事件订阅管理端点（M13）：<c>/api/admin/event-subscriptions</c>。
/// <para>
/// 订阅是<b>把引擎内部的事件推给外部系统</b>的那根线：没有它，
/// <c>ACT_EVT_OUTBOX</c> 里攒着的事件只能靠订阅方自己来拉（而它根本不知道什么时候有新事件）。
/// </para>
/// <para>
/// 🔑 <b>密钥只在创建时出现一次</b>，之后只读接口一律只回 <c>secretPreview</c>（后 4 位）。
/// 订阅密钥等价于「能伪造 NetBpm 通知」的凭据，管理端列表页把它明文摊开是最常见的泄漏方式。
/// 忘了就调 <c>POST /{id}/rotate</c> 换一把。
/// </para>
/// </summary>
public static class EventSubscriptionEndpoints
{
    /// <summary>注册端点。</summary>
    public static IEndpointRouteBuilder MapEventSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/admin").WithTags("platform-admin");

        group.MapGet("/event-subscriptions", ListSubscriptionsAsync)
            .Produces<IReadOnlyList<EventSubscriptionView>>();

        group.MapPost("/event-subscriptions", CreateSubscriptionAsync)
            .Accepts<CreateEventSubscriptionRequest>("application/json")
            .Produces<CreatedEventSubscription>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/event-subscriptions/{subscriptionId}", FindSubscriptionAsync)
            .Produces<EventSubscriptionView>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPatch("/event-subscriptions/{subscriptionId}", UpdateSubscriptionAsync)
            .Accepts<UpdateEventSubscriptionRequest>("application/json")
            .Produces<EventSubscriptionView>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/event-subscriptions/{subscriptionId}", DeleteSubscriptionAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/event-subscriptions/{subscriptionId}/status", SetSubscriptionStatusAsync)
            .Produces<EventSubscriptionView>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        // 换密钥：旧密钥立刻失效，正在投递中的请求会用新密钥重签（下一次重试生效）
        group.MapPost("/event-subscriptions/{subscriptionId}/rotate", RotateSubscriptionSecretAsync)
            .Produces<IssuedEventSubscriptionSecret>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        // 投递记录：回答「这条事件到底有没有送到」
        group.MapGet("/event-subscriptions/{subscriptionId}/deliveries", ListDeliveriesAsync)
            .Produces<IReadOnlyList<EventDeliveryView>>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/events/{eventId}/deliveries/{subscriptionId}/retry", RetryDeliveryAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListSubscriptionsAsync(
        string? tenantId,
        bool? enabled,
        int? maxResults,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "查看事件订阅");

        var scope = ClientAdminEndpoints.ResolveTenantScope(context, tenantId);

        var items = await store.FindAsync(
            new EventSubscriptionQuery(
                scope,
                enabled,
                maxResults is > 0 ? Math.Min(maxResults.Value, 200) : 100),
            ct).ConfigureAwait(false);

        return Results.Ok(items.Select(EventSubscriptionView.From).ToArray());
    }

    private static async Task<IResult> CreateSubscriptionAsync(
        CreateEventSubscriptionRequest? request,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "创建事件订阅");

        if (request is null)
        {
            throw new BadHttpRequestException("请求体不能为空（至少要有 name 与 endpointUrl）。");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BadHttpRequestException("name 不能为空：它是管理端区分订阅的唯一标识。");
        }

        if (string.IsNullOrWhiteSpace(request.EndpointUrl)
            || !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BadHttpRequestException("endpointUrl 必须是 http/https 绝对地址。");
        }

        // 租户不能越出凭据所属范围：否则一个租户的凭据就能往任意租户的事件上挂回调
        var tenantId = ClientAdminEndpoints.ResolveTenantScope(context, request.TenantId) ?? "default";

        var subscription = new EventSubscription
        {
            TenantId = tenantId,
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
        };

        var created = await store.CreateAsync(subscription, ct).ConfigureAwait(false);

        // 明文密钥只在这一次响应里出现
        return Results.Created(
            $"/api/admin/event-subscriptions/{created.Id}",
            new CreatedEventSubscription(EventSubscriptionView.From(created), created.Secret));
    }

    private static async Task<IResult> FindSubscriptionAsync(
        string subscriptionId,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "查看事件订阅");

        var subscription = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        return subscription is null || !ClientAdminEndpoints.IsVisible(context, subscription.TenantId)
            ? Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"))
            : Results.Ok(EventSubscriptionView.From(subscription));
    }

    private static async Task<IResult> UpdateSubscriptionAsync(
        string subscriptionId,
        UpdateEventSubscriptionRequest? request,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "修改事件订阅");

        if (request is null)
        {
            throw new BadHttpRequestException("请求体不能为空。");
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !ClientAdminEndpoints.IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
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
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new BadHttpRequestException("endpointUrl 必须是 http/https 绝对地址。");
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

        await store.UpdateAsync(existing, ct).ConfigureAwait(false);

        return Results.Ok(EventSubscriptionView.From(existing));
    }

    private static async Task<IResult> DeleteSubscriptionAsync(
        string subscriptionId,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "删除事件订阅");

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !ClientAdminEndpoints.IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        await store.DeleteAsync(subscriptionId, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> SetSubscriptionStatusAsync(
        string subscriptionId,
        bool? enabled,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "切换事件订阅状态");

        if (enabled is null)
        {
            throw new BadHttpRequestException("查询参数 enabled 不能为空（true/false）。");
        }

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !ClientAdminEndpoints.IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        if (!await store.SetEnabledAsync(subscriptionId, enabled.Value, ct).ConfigureAwait(false))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        existing.Enabled = enabled.Value;

        return Results.Ok(EventSubscriptionView.From(existing));
    }

    private static async Task<IResult> RotateSubscriptionSecretAsync(
        string subscriptionId,
        RequestContext context,
        IEventSubscriptionStore store,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "轮换事件订阅密钥");

        var existing = await store.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (existing is null || !ClientAdminEndpoints.IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        existing.Secret = EventSubscriptionSecret.New();

        await store.UpdateAsync(existing, ct).ConfigureAwait(false);

        return Results.Ok(new IssuedEventSubscriptionSecret(subscriptionId, existing.Secret));
    }

    private static async Task<IResult> ListDeliveriesAsync(
        string subscriptionId,
        string? eventId,
        RequestContext context,
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "查看事件投递记录");

        var subscription = await subscriptions.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (subscription is null || !ClientAdminEndpoints.IsVisible(context, subscription.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        // 存储侧只有「按事件查全部订阅」的索引；按订阅查要全表扫，因此 eventId 是必填条件
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new BadHttpRequestException("查询参数 eventId 不能为空（投递记录按事件归集，不提供会退化成全表扫描）。");
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
        RequestContext context,
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.EventSubscribe, "重投事件投递");

        var subscription = await subscriptions.FindByIdAsync(subscriptionId, ct).ConfigureAwait(false);

        if (subscription is null || !ClientAdminEndpoints.IsVisible(context, subscription.TenantId))
        {
            return Results.NotFound(new ErrorResponse(
                $"事件订阅 '{subscriptionId}' 不存在。",
                Code: "event_subscription_not_found"));
        }

        if (await deliveries.FindAsync(eventId, subscriptionId, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"事件 '{eventId}' 对订阅 '{subscriptionId}' 没有投递记录。",
                Code: "event_delivery_not_found"));
        }

        if (!await deliveries.RetryAsync(eventId, subscriptionId, DateTime.UtcNow, ct).ConfigureAwait(false))
        {
            return Results.Conflict(new ErrorResponse(
                $"该投递不是 {EventDeliveryStatus.DeadLetter} 状态，无需重投。",
                Code: "event_delivery_not_dead_letter"));
        }

        return Results.NoContent();
    }

    private static string[] NormalizeTypes(IReadOnlyList<string>? types) =>
        types?.Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static void EnsureAdmin(RequestContext context, string scope, string action) =>
        ClientAdminEndpoints.EnsureAdmin(context, scope, action);
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
public sealed record CreateEventSubscriptionRequest(
    string Name,
    string EndpointUrl,
    string? TenantId = null,
    IReadOnlyList<string>? EventTypes = null,
    string? ProcessDefinitionId = null,
    string? Secret = null,
    IDictionary<string, string>? Headers = null,
    bool? Enabled = null,
    int? MaxAttempts = null);

/// <summary>更新订阅的请求（<b>不含 secret</b> —— 换密钥走 <c>/rotate</c>）。</summary>
/// <param name="Name">订阅名。</param>
/// <param name="EventTypes">事件类型白名单。</param>
/// <param name="ProcessDefinitionId">流程定义 id 过滤。</param>
/// <param name="EndpointUrl">回调地址。</param>
/// <param name="Headers">附加静态请求头。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="MaxAttempts">最大投递尝试次数。</param>
public sealed record UpdateEventSubscriptionRequest(
    string? Name = null,
    IReadOnlyList<string>? EventTypes = null,
    string? ProcessDefinitionId = null,
    string? EndpointUrl = null,
    IDictionary<string, string>? Headers = null,
    bool? Enabled = null,
    int? MaxAttempts = null);

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
