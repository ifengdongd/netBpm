using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;

namespace NetBpm.Rest.Security;

/// <summary>
/// <c>/admin/v1</c> 的组级授权兜底：没有任何管理类 scope 的凭据一律 403。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：管理面的每个端点都在 handler 里自己判 <c>HasScope</c>（因为每个操作的
/// 细粒度 scope 不同）。这个写法正确，但<b>没有兜底</b>——新增一个端点时漏写一次
/// <c>if (!HasScope(...)) return InsufficientScope(...)</c>，它就变成「任何通过鉴权的调用方都能用」。
/// 这类漏洞不会在开发时暴露（本地 RequireCredential=false 时全放行），只会在某次「顺手加个端点」后
/// 悄悄上线。
/// </para>
/// <para>
/// <b>它不替代端点内的细粒度判定</b>：这里只要求「至少持有<b>一个</b>管理类 scope」，
/// 具体操作要哪一个仍由 handler 决定。两层叠加后，漏写 handler 判定的后果从「裸奔」降为
/// 「别的管理员能做」——仍然不对，但不再是任意接入方都能做。
/// </para>
/// <para>
/// <b>为什么用中间件而不是端点过滤器</b>：31 个管理端点分散在 5 个 <c>Map*Endpoints</c> 里，
/// 逐个挂过滤器正好又制造了 31 次「可能漏挂」。中间件按路径前缀生效，新增端点自动被覆盖。
/// </para>
/// </remarks>
public sealed class AdminSurfaceGuardMiddleware
{
    /// <summary>受保护的管理面路径前缀。</summary>
    private const string SurfacePrefix = "/admin/v1";

    /// <summary>任一命中即视为「管理类凭据」的 scope。</summary>
    private static readonly string[] AdminScopes =
    [
        PlatformScopes.AdminProcess,
        PlatformScopes.AdminInstance,
        PlatformScopes.AdminVariables,
        PlatformScopes.AdminClient,
        PlatformScopes.AdminAuditRead,
        PlatformScopes.EventSubscribe,
    ];

    private readonly RequestDelegate _next;

    /// <summary>构造。</summary>
    /// <param name="next">管道下一环。</param>
    public AdminSurfaceGuardMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        _next = next;
    }

    /// <summary>执行。</summary>
    /// <param name="http">当前请求。</param>
    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        // 只守管理面；/api/admin（旧面）走它自己的一套判定，避免把两套话术混在一起
        if (!http.Request.Path.StartsWithSegments(SurfacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(http).ConfigureAwait(false);

            return;
        }

        var context = http.RequestServices.GetRequiredService<RequestContext>();

        // 未启用鉴权（AuthMode.None）与旧 ApiKey 在 HasScope 里本身就是全放行，
        // 这里不另开口子：开发模式 / 内网可信服务的行为保持不变。
        if (!PlatformAuthorization.HasAnyScope(context, AdminScopes))
        {
            var result = PlatformProblems.Create(
                http,
                context,
                ErrorCodes.AuthInsufficientScope,
                "缺少授权范围",
                StatusCodes.Status403Forbidden,
                "调用 /admin/v1 至少需要一个管理类 scope（workflow:admin:process / "
                + "workflow:admin:instance / workflow:admin:variables / workflow:admin:client / "
                + "workflow:admin:audit:read / workflow:event:subscribe）。"
                + "具体操作所需的那一个仍由端点判定。");

            await result.ExecuteAsync(http).ConfigureAwait(false);

            return;
        }

        await _next(http).ConfigureAwait(false);
    }
}
