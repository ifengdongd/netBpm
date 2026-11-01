using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 管理面：<b>租户与接入客户端</b>（<c>/admin/v1/tenants</c>、<c>/admin/v1/clients</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这组端点存在的理由很朴素：<b>密钥签发必须有入口</b>。没有它，<c>IClientRegistry</c> 只能被代码调用，
/// 接入方永远拿不到第一把密钥。它是旧 <c>/api/admin</c> 同面能力的平台面形态：
/// RFC 9457 错误体、<c>workflow:admin:client</c> 逐条判权、写动作全部进审计。
/// </para>
/// <para>
/// 🔑 <b>签发密钥的动作刻意不做幂等</b>（<c>POST /clients</c> 与 <c>POST /clients/{id}/rotate</c>）。
/// 平台面的幂等键会把<b>首次响应体</b>存进 <c>ACT_IDEMPOTENCY</c>，而这里的响应体里有明文密钥——
/// 「为了让重试返回同一个密钥」就得把明文密钥落进另一张表，直接破坏「只存摘要、只出现一次」。
/// 丢响应的正确恢复动作是 <c>/rotate</c>：旧密钥在宽限期（默认 7 天）内继续有效，
/// 不会把已上线的对接打断。其余写动作（PUT / PATCH / DELETE）本身就是幂等的。
/// </para>
/// <para>
/// 三条硬约束沿用旧面（它们是安全属性，不是风格）：
/// ① 明文密钥只在 <c>POST /clients</c> 与 <c>POST /clients/{id}/rotate</c> 的响应里出现一次；
/// ② 高危 scope 必须 <c>?confirmPrivilegedScopes=true</c> 才能授予；
/// ③ 租户边界：凭据绑定了租户时只能看 / 改自己租户的东西，跨租户读按不存在（404）、
/// 跨租户写按禁止（403，目标租户就在请求里，藏存在性没有意义）。
/// </para>
/// </remarks>
public static class AdminClientEndpoints
{
    private const string Tag = "platform-admin-client";

    /// <summary>注册端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapAdminClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/admin/v1").WithTags(Tag);

        group.MapGet("/tenants", ListTenantsAsync)
            .Produces<IReadOnlyList<TenantView>>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        // 幂等：已存在则原样返回，不覆盖名称与配额（否则每次启动引导都会把运维调过的值打回默认）
        group.MapPut("/tenants/{tenantId}", EnsureTenantAsync)
            .Accepts<RegisterTenantRequest>("application/json")
            .Produces<TenantView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapPut("/tenants/{tenantId}/status", SetTenantStatusAsync)
            .Accepts<SetTenantStatusRequest>("application/json")
            .Produces<TenantView>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/clients", ListClientsAsync)
            .Produces<IReadOnlyList<ClientDescriptor>>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/clients/{clientId}", FindClientAsync)
            .Produces<ClientDescriptor>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPost("/clients", RegisterClientAsync)
            .Accepts<RegisterClientRequest>("application/json")
            // 响应体含一次性明文密钥：退出幂等缓存（见 NoIdempotencyMetadata）
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<IssuedClientSecret>(StatusCodes.Status201Created)
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        // PATCH 而不是 PUT：漏传一个字段就清空 scope，是多系统接入时最常见的事故
        group.MapPatch("/clients/{clientId}", UpdateClientAsync)
            .Accepts<UpdateClientRequest>("application/json")
            .Produces<ClientDescriptor>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/clients/{clientId}/rotate", RotateSecretAsync)
            // 响应体含一次性明文密钥：退出幂等缓存
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<IssuedClientSecret>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapDelete("/clients/{clientId}", RevokeClientAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    // ─────────────────────────── 租户 ───────────────────────────

    private static async Task<IResult> ListTenantsAsync(
        HttpContext http,
        RequestContext context,
        ITenantRegistry registry,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var tenants = await registry.ListAsync(ct).ConfigureAwait(false);

        // 绑定租户的凭据只看得到自己那个租户：管理端点同样要守租户边界，
        // 否则一个租户的管理员能列出全网租户
        return Results.Ok(
            tenants
                .Where(tenant => TenantScopes.IsVisible(context, tenant.TenantId))
                .Select(ToView)
                .ToArray());
    }

    private static async Task<IResult> EnsureTenantAsync(
        string tenantId,
        RegisterTenantRequest? request,
        HttpContext http,
        RequestContext context,
        ITenantRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.TenantNameRequired,
                "缺少租户名称",
                StatusCodes.Status400BadRequest,
                "PUT /admin/v1/tenants/{tenantId} 需要 name（租户名进审计与运维看板）。");
        }

        if (!TenantScopes.IsVisible(context, tenantId.Trim()))
        {
            return CrossTenant(http, context, "登记租户", tenantId);
        }

        var tenant = await registry
            .EnsureAsync(new TenantRegistration(tenantId.Trim(), request.Name.Trim(), request.QuotaPerMinute ?? 600), ct)
            .ConfigureAwait(false);

        await auditor.RecordAsync(
            context,
            AdminAuditActions.TenantRegister,
            AdminAuditTargets.Tenant,
            tenant.TenantId,
            detail: new { tenant.Name, tenant.QuotaPerMinute, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.Ok(ToView(tenant));
    }

    private static async Task<IResult> SetTenantStatusAsync(
        string tenantId,
        SetTenantStatusRequest? request,
        HttpContext http,
        RequestContext context,
        ITenantRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        if (request is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestBodyRequired,
                "缺少请求体",
                StatusCodes.Status400BadRequest,
                "需要 { \"active\": true|false }。");
        }

        if (!TenantScopes.IsVisible(context, tenantId))
        {
            return CrossTenant(http, context, "修改租户状态", tenantId);
        }

        // 自己的租户不能自己停用：一旦停用，当前这把凭据立刻失效，连「改回来」都做不到
        if (string.Equals(context.EffectiveTenantId, tenantId, StringComparison.Ordinal) && !request.Active)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.TenantSelfSuspend,
                "不能停用当前凭据所属的租户",
                StatusCodes.Status400BadRequest,
                "停用后本次调用的凭据立即失效，无法自行恢复；请用另一个租户的管理凭据操作。");
        }

        var status = request.Active ? TenantStatus.Active : TenantStatus.Suspended;

        if (!await registry.SetStatusAsync(tenantId, status, ct).ConfigureAwait(false))
        {
            return PlatformProblems.TenantNotFound(http, context, tenantId);
        }

        await auditor.RecordAsync(
            context,
            AdminAuditActions.TenantStatusChange,
            AdminAuditTargets.Tenant,
            tenantId,
            detail: new { Active = request.Active, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        var tenant = await registry.FindAsync(tenantId, ct).ConfigureAwait(false);

        return tenant is null
            ? PlatformProblems.TenantNotFound(http, context, tenantId)
            : Results.Ok(ToView(tenant));
    }

    // ─────────────────────────── 客户端 ───────────────────────────

    private static async Task<IResult> ListClientsAsync(
        string? tenantId,
        HttpContext http,
        RequestContext context,
        IClientDirectory registry,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var requested = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
        var scoped = context.EffectiveTenantId;

        // 显式参数不得越出凭据所属租户（否则「列出全网客户端」只要拼一个查询参数）
        if (scoped is not null && requested is not null && !string.Equals(scoped, requested, StringComparison.Ordinal))
        {
            return CrossTenant(http, context, "查看客户端", requested);
        }

        var clients = await registry.ListAsync(scoped ?? requested, ct).ConfigureAwait(false);

        return Results.Ok(clients);
    }

    private static async Task<IResult> FindClientAsync(
        string clientId,
        HttpContext http,
        RequestContext context,
        IClientDirectory registry,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var client = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        // 跨租户按不存在处理（404，不泄露存在性）
        return client is null || !TenantScopes.IsVisible(context, client.TenantId)
            ? PlatformProblems.ClientNotFound(http, context, clientId)
            : Results.Ok(client);
    }

    private static async Task<IResult> RegisterClientAsync(
        RegisterClientRequest? request,
        bool? confirmPrivilegedScopes,
        HttpContext http,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.ClientNameRequired,
                "缺少系统名称",
                StatusCodes.Status400BadRequest,
                "注册需要 name（进审计与运维看板）。");
        }

        if (!TenantScopes.IsVisible(context, request.TenantId ?? string.Empty))
        {
            return CrossTenant(http, context, "为其它租户注册客户端", request.TenantId ?? string.Empty);
        }

        try
        {
            var issued = await registry
                .RegisterAsync(request, confirmPrivilegedScopes ?? false, ct)
                .ConfigureAwait(false);

            await auditor.RecordAsync(
                context,
                AdminAuditActions.ClientRegister,
                AdminAuditTargets.Client,
                issued.ClientId,
                detail: new
                {
                    request.Name,
                    request.TenantId,
                    request.Scopes,
                    request.AllowedProcessKeys,
                    request.QuotaPerMinute,
                    ConfirmedPrivilegedScopes = confirmPrivilegedScopes ?? false,
                    Caller = context.CallerId,
                },
                ct: ct).ConfigureAwait(false);

            // 201 + Location：明文密钥就在响应体里，且只此一次
            return Results.Created($"/admin/v1/clients/{issued.ClientId}", issued);
        }
        catch (ClientRegistryException exception)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientRegister, request.Name, exception, ct)
                .ConfigureAwait(false);

            return FromRegistryException(http, context, exception);
        }
    }

    private static async Task<IResult> UpdateClientAsync(
        string clientId,
        UpdateClientRequest? request,
        bool? confirmPrivilegedScopes,
        HttpContext http,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        if (request is null)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestBodyRequired,
                "缺少请求体",
                StatusCodes.Status400BadRequest,
                "PATCH 至少给一个字段；全部字段为 null 等于什么都不改。");
        }

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        // 先判可见再改：跨租户直接按不存在处理，避免「改一下就知道它存在」
        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return PlatformProblems.ClientNotFound(http, context, clientId);
        }

        try
        {
            var updated = await registry
                .UpdateAsync(clientId, request, confirmPrivilegedScopes ?? false, ct)
                .ConfigureAwait(false);

            if (updated is null)
            {
                return PlatformProblems.ClientNotFound(http, context, clientId);
            }

            await auditor.RecordAsync(
                context,
                AdminAuditActions.ClientUpdate,
                AdminAuditTargets.Client,
                clientId,
                detail: new
                {
                    request.Name,
                    Status = request.Status?.ToString(),
                    request.Scopes,
                    request.AllowedProcessKeys,
                    request.QuotaPerMinute,
                    ConfirmedPrivilegedScopes = confirmPrivilegedScopes ?? false,
                    Caller = context.CallerId,
                },
                ct: ct).ConfigureAwait(false);

            return Results.Ok(updated);
        }
        catch (ClientRegistryException exception)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientUpdate, clientId, exception, ct)
                .ConfigureAwait(false);

            return FromRegistryException(http, context, exception);
        }
    }

    private static async Task<IResult> RotateSecretAsync(
        string clientId,
        HttpContext http,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return PlatformProblems.ClientNotFound(http, context, clientId);
        }

        try
        {
            var issued = await registry.RotateSecretAsync(clientId, validDays: null, ct).ConfigureAwait(false);

            if (issued is null)
            {
                return PlatformProblems.ClientNotFound(http, context, clientId);
            }

            await auditor.RecordAsync(
                context,
                AdminAuditActions.ClientRotate,
                AdminAuditTargets.Client,
                clientId,
                detail: new
                {
                    issued.SecretExpiresAt,
                    issued.PreviousSecretValidUntil,
                    Caller = context.CallerId,
                },
                ct: ct).ConfigureAwait(false);

            return Results.Ok(issued);
        }
        catch (ClientRegistryException exception)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientRotate, clientId, exception, ct)
                .ConfigureAwait(false);

            return FromRegistryException(http, context, exception);
        }
    }

    private static async Task<IResult> RevokeClientAsync(
        string clientId,
        HttpContext http,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.AdminClient))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.AdminClient);
        }

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        if (existing is null || !TenantScopes.IsVisible(context, existing.TenantId))
        {
            return PlatformProblems.ClientNotFound(http, context, clientId);
        }

        if (!await registry.RevokeAsync(clientId, ct).ConfigureAwait(false))
        {
            return PlatformProblems.ClientNotFound(http, context, clientId);
        }

        await auditor.RecordAsync(
            context,
            AdminAuditActions.ClientRevoke,
            AdminAuditTargets.Client,
            clientId,
            detail: new { existing.Name, Caller = context.CallerId },
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>
    /// 跨租户写：<c>403</c> 而不是 404。
    /// </summary>
    /// <remarks>
    /// 目标租户就在请求里（路径或请求体），调用方已经知道它存在——
    /// 这里再按「不存在」处理只会让人以为是自己拼错了 id。
    /// </remarks>
    private static IResult CrossTenant(HttpContext http, RequestContext context, string action, string tenantId) =>
        PlatformProblems.Create(
            http,
            context,
            ErrorCodes.TenantForbidden,
            "跨租户操作被拒绝",
            StatusCodes.Status403Forbidden,
            $"当前凭据绑定租户 '{context.EffectiveTenantId}'，无法{action}（目标租户 '{tenantId}'）。");

    /// <summary>
    /// 把注册表异常翻译成平台错误体。
    /// </summary>
    /// <remarks>
    /// <c>tenant.not_found</c> 是 404（所属租户不存在），其余（未定义 scope / 高危未确认 / 已注销）是 422：
    /// 请求本身不合法而不是资源不存在。旧面把它们统一成 400，客户端只能靠 message 猜。
    /// </remarks>
    private static IResult FromRegistryException(
        HttpContext http,
        RequestContext context,
        ClientRegistryException exception) =>
        PlatformProblems.Create(
            http,
            context,
            exception.Code,
            string.Equals(exception.Code, ErrorCodes.TenantNotFound, StringComparison.Ordinal)
                ? "租户不存在"
                : "客户端请求不被接受",
            string.Equals(exception.Code, ErrorCodes.TenantNotFound, StringComparison.Ordinal)
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status422UnprocessableEntity,
            exception.Message);

    private static async Task RecordFailureAsync(
        RequestContext context,
        AdminActionAuditor auditor,
        string action,
        string targetId,
        Exception exception,
        CancellationToken ct) =>
        await auditor.RecordAsync(
            context,
            action,
            AdminAuditTargets.Client,
            targetId,
            detail: new { Caller = context.CallerId },
            success: false,
            error: exception.Message,
            ct: ct).ConfigureAwait(false);

    private static TenantView ToView(TenantDescriptor tenant) => new(
        tenant.TenantId,
        tenant.Name,
        tenant.IsUsable,
        tenant.QuotaPerMinute,
        tenant.CreatedAt);
}
