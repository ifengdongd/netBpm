using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 接入管理端点：租户与客户端（<c>/api/admin/tenants</c>、<c>/api/admin/clients</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由很朴素：密钥签发必须有入口。没有这组端点，<c>IClientRegistry</c> 只能被代码调用，
/// 接入方永远拿不到第一把密钥。
/// </para>
/// <para>
/// ⚠️ <b>已被 <c>/admin/v1/tenants</c> 与 <c>/admin/v1/clients</c> 取代</b>
/// （<see cref="AdminClientEndpoints"/>）：那一组用 RFC 9457 错误体、逐条判 scope、写动作全部进审计。
/// 本组保留只为既有运维脚本；新接入不要用。
/// </para>
/// <para>
/// 三条硬约束：
/// ① 明文密钥只在 <c>POST /clients</c> 与 <c>POST /clients/{id}/rotate</c> 的响应里出现<b>一次</b>，
///   读接口永不返回；
/// ② 高危 scope 必须带 <c>?confirmPrivilegedScopes=true</c> 才能授予（不能跟着一串业务 scope 顺带发出去）；
/// ③ 每次变更都写管理审计，且明细里带 <c>caller</c>——「哪个系统给哪个系统发的密钥」必须可回查。
/// </para>
/// </remarks>
public static class ClientAdminEndpoints
{
    /// <summary>旧接入管理面的废弃文案（替代物必须写清楚，否则读的人只知道「别用」）。</summary>
    internal const string DeprecatedMessage =
        "旧接入管理面（/api/admin）。改用 /admin/v1/tenants 与 /admin/v1/clients："
        + "错误体是 RFC 9457（按 code 分支），租户边界与 scope 口径与业务面一致，写动作全部进管理审计。";

    /// <summary>注册端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    [Obsolete(DeprecatedMessage)]
    public static IEndpointRouteBuilder MapClientAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/admin").WithTags("platform-admin");

        // 租户：注册客户端前必须先有租户
        group.MapGet("/tenants", ListTenantsAsync)
            .Produces<IReadOnlyList<TenantView>>();

        group.MapPut("/tenants/{tenantId}", EnsureTenantAsync)
            .Produces<TenantView>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPut("/tenants/{tenantId}/status", SetTenantStatusAsync)
            .Produces<TenantView>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        // 客户端：一系统一客户端一密钥
        group.MapGet("/clients", ListClientsAsync)
            .Produces<IReadOnlyList<ClientDescriptor>>();

        group.MapGet("/clients/{clientId}", FindClientAsync)
            .Produces<ClientDescriptor>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/clients", RegisterClientAsync)
            .Accepts<RegisterClientRequest>("application/json")
            // 响应体含一次性明文密钥：退出幂等缓存（见 NoIdempotencyMetadata）
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<IssuedClientSecret>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status403Forbidden);

        group.MapPatch("/clients/{clientId}", UpdateClientAsync)
            .Accepts<UpdateClientRequest>("application/json")
            .Produces<ClientDescriptor>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/clients/{clientId}/rotate", RotateSecretAsync)
            // 响应体含一次性明文密钥：退出幂等缓存
            .WithMetadata(new NoIdempotencyMetadata())
            .Produces<IssuedClientSecret>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapDelete("/clients/{clientId}", RevokeClientAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListTenantsAsync(
        RequestContext context,
        ITenantRegistry registry,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "查看租户");

        var tenants = await registry.ListAsync(ct).ConfigureAwait(false);

        // 与 /admin/v1/tenants 同口径：绑定租户的凭据只看得到自己那个租户。
        // 少了这一行，旧面就是新面租户边界的旁路（同一个 scope 在两边得到不同结果）。
        return Results.Ok(
            tenants
                .Where(tenant => IsVisible(context, tenant.TenantId))
                .Select(ToView)
                .ToArray());
    }

    private static async Task<IResult> EnsureTenantAsync(
        string tenantId,
        RegisterTenantRequest? request,
        RequestContext context,
        ITenantRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "登记租户");

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("请提供租户名称。", Code: ErrorCodes.TenantNameRequired));
        }

        // 跨租户写按禁止（403）而不是 404：目标租户就在请求路径里，调用方本来就知道它存在
        if (!IsVisible(context, tenantId.Trim()))
        {
            throw new ForbiddenException($"无权登记租户 '{tenantId.Trim()}'。");
        }

        var registration = new TenantRegistration(
            tenantId.Trim(),
            request.Name.Trim(),
            request.QuotaPerMinute ?? 600);

        var tenant = await registry.EnsureAsync(registration, ct).ConfigureAwait(false);

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
        RequestContext context,
        ITenantRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "修改租户状态");

        if (request is null)
        {
            return Results.BadRequest(new ErrorResponse("请提供 active 字段。", Code: ErrorCodes.RequestBodyRequired));
        }

        // 跨租户写按禁止（403）：停用开关是整租户的止血闸，不能由别的租户的管理员按下
        if (!IsVisible(context, tenantId))
        {
            throw new ForbiddenException($"无权修改租户 '{tenantId}' 的状态。");
        }

        // 自己的租户不能自己停用：一旦停用，当前这把凭据立刻失效，连「改回来」都做不到
        if (string.Equals(context.EffectiveTenantId, tenantId, StringComparison.Ordinal) && !request.Active)
        {
            return Results.BadRequest(new ErrorResponse(
                "不能停用当前凭据所属的租户：停用后本次调用的凭据立即失效，无法自行恢复。",
                Code: ErrorCodes.TenantSelfSuspend));
        }

        var status = request.Active ? TenantStatus.Active : TenantStatus.Suspended;
        var updated = await registry.SetStatusAsync(tenantId, status, ct).ConfigureAwait(false);

        if (!updated)
        {
            return Results.NotFound(new ErrorResponse(
                $"租户 '{tenantId}' 不存在。",
                Code: ErrorCodes.TenantNotFound));
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
            ? Results.NotFound(new ErrorResponse($"租户 '{tenantId}' 不存在。", Code: ErrorCodes.TenantNotFound))
            : Results.Ok(ToView(tenant));
    }

    private static async Task<IResult> ListClientsAsync(
        string? tenantId,
        RequestContext context,
        IClientRegistry registry,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "查看客户端");

        // 带租户的凭据只能看自己租户：管理端点同样要守租户边界，否则一个租户的管理员能列出全网客户端
        var scopedTenant = ResolveTenantScope(context, tenantId);

        var clients = await registry.ListAsync(scopedTenant, ct).ConfigureAwait(false);

        return Results.Ok(clients);
    }

    private static async Task<IResult> FindClientAsync(
        string clientId,
        RequestContext context,
        IClientRegistry registry,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "查看客户端");

        var client = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        // 跨租户按不存在处理（404，不泄露存在性）
        return client is null || !IsVisible(context, client.TenantId)
            ? Results.NotFound(new ErrorResponse($"客户端 '{clientId}' 不存在。", Code: ErrorCodes.ClientNotFound))
            : Results.Ok(client);
    }

    private static async Task<IResult> RegisterClientAsync(
        RegisterClientRequest? request,
        bool? confirmPrivilegedScopes,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "注册客户端");

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("请提供系统名称。", Code: ErrorCodes.ClientNameRequired));
        }

        if (!IsVisible(context, request.TenantId))
        {
            throw new ForbiddenException($"无权为租户 '{request.TenantId}' 注册客户端。");
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
            return Results.Created($"/api/admin/clients/{issued.ClientId}", issued);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientRegister, request.Name, exception, ct)
                .ConfigureAwait(false);

            throw;
        }
    }

    private static async Task<IResult> UpdateClientAsync(
        string clientId,
        UpdateClientRequest? request,
        bool? confirmPrivilegedScopes,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "修改客户端");

        if (request is null)
        {
            return Results.BadRequest(new ErrorResponse("请提供要修改的字段。", Code: ErrorCodes.RequestBodyRequired));
        }

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        // 先判可见再改：跨租户直接按不存在处理，避免「改一下就知道它存在」
        if (existing is null || !IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse($"客户端 '{clientId}' 不存在。", Code: ErrorCodes.ClientNotFound));
        }

        try
        {
            var updated = await registry
                .UpdateAsync(clientId, request, confirmPrivilegedScopes ?? false, ct)
                .ConfigureAwait(false);

            if (updated is null)
            {
                return Results.NotFound(new ErrorResponse(
                    $"客户端 '{clientId}' 不存在。",
                    Code: ErrorCodes.ClientNotFound));
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
                    Caller = context.CallerId,
                },
                ct: ct).ConfigureAwait(false);

            return Results.Ok(updated);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientUpdate, clientId, exception, ct)
                .ConfigureAwait(false);

            throw;
        }
    }

    private static async Task<IResult> RotateSecretAsync(
        string clientId,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "轮换客户端密钥");

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        if (existing is null || !IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse($"客户端 '{clientId}' 不存在。", Code: ErrorCodes.ClientNotFound));
        }

        try
        {
            var issued = await registry.RotateSecretAsync(clientId, validDays: null, ct).ConfigureAwait(false);

            if (issued is null)
            {
                return Results.NotFound(new ErrorResponse(
                    $"客户端 '{clientId}' 不存在。",
                    Code: ErrorCodes.ClientNotFound));
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(context, auditor, AdminAuditActions.ClientRotate, clientId, exception, ct)
                .ConfigureAwait(false);

            throw;
        }
    }

    private static async Task<IResult> RevokeClientAsync(
        string clientId,
        RequestContext context,
        IClientRegistry registry,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        EnsureAdmin(context, PlatformScopes.AdminClient, "注销客户端");

        var existing = await registry.FindAsync(clientId, ct).ConfigureAwait(false);

        if (existing is null || !IsVisible(context, existing.TenantId))
        {
            return Results.NotFound(new ErrorResponse($"客户端 '{clientId}' 不存在。", Code: ErrorCodes.ClientNotFound));
        }

        var revoked = await registry.RevokeAsync(clientId, ct).ConfigureAwait(false);

        if (!revoked)
        {
            return Results.NotFound(new ErrorResponse($"客户端 '{clientId}' 不存在。", Code: ErrorCodes.ClientNotFound));
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

    /// <summary>
    /// 管理授权：只看「旧口径」与显式 scope。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AdminAuthorization"/> 的区别是这里的 scope 用平台词汇（<c>workflow:admin:*</c>）：
    /// 接入管理属于平台能力，旧 <c>netbpm.admin.*</c> 只是引擎管理。
    /// 两个都认是为了让既有管理员账号在过渡期仍能发密钥。
    /// </remarks>
    internal static void EnsureAdmin(RequestContext context, string scope, string action)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AuthMode is RequestAuthMode.None or RequestAuthMode.ApiKey)
        {
            return;
        }

        if (context.HasAdminScope(TaskScopes.Admin)
            || PlatformScopes.IsSatisfiedBy([.. context.Scopes], scope))
        {
            return;
        }

        throw new ForbiddenException(
            $"当前主体缺少 scope '{scope}'，无法{action}；持有 '{TaskScopes.Admin}' 可覆盖全部管理动作。");
    }

    /// <summary>租户可见性（实现见 <see cref="TenantScopes.IsVisible"/>，两处口径必须一致）。</summary>
    internal static bool IsVisible(RequestContext context, string tenantId) =>
        TenantScopes.IsVisible(context, tenantId);

    /// <summary>列表的租户过滤（实现见 <see cref="TenantScopes.Resolve"/>）。</summary>
    internal static string? ResolveTenantScope(RequestContext context, string? requested) =>
        TenantScopes.Resolve(context, requested, "客户端");

    private static async Task RecordFailureAsync(
        RequestContext context,
        AdminActionAuditor auditor,
        string action,
        string targetId,
        Exception exception,
        CancellationToken ct)
    {
        await auditor.RecordAsync(
            context,
            action,
            AdminAuditTargets.Client,
            targetId,
            detail: new { Caller = context.CallerId },
            success: false,
            error: exception.Message,
            ct: ct).ConfigureAwait(false);
    }

    private static TenantView ToView(TenantDescriptor tenant) => new(
        tenant.TenantId,
        tenant.Name,
        tenant.IsUsable,
        tenant.QuotaPerMinute,
        tenant.CreatedAt);
}
