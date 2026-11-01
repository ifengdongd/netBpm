using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 管理面的租户口径：把 <c>?tenantId=</c> 收窄到当前凭据所属租户。
/// </summary>
/// <remarks>
/// <para>
/// 「跨租户运营视角」只属于<b>没有绑定租户</b>的平台凭据。凭据一旦绑定租户，
/// 它就是一个租户管理员：查询参数不能把它变成平台运营。
/// </para>
/// <para>
/// 统一放在这里而不是各端点各写一份：审计、订阅、配额、客户端列表必须是同一口径，
/// 否则「这里收窄、那里放行」就会变成一条现成的越权路径。
/// </para>
/// </remarks>
internal static class PlatformTenant
{
    /// <summary>
    /// 解析目标租户；跨出租户边界时返回 403 与可直接返回的 problem。
    /// </summary>
    /// <param name="http">当前请求。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="requested">请求里声明的租户（可为空）。</param>
    /// <param name="subject">用于错误说明的资源名（例如「事件订阅」）。</param>
    /// <param name="tenantId">解析出的租户。</param>
    /// <param name="problem">跨租户时的 403 响应。</param>
    public static bool TryResolve(
        HttpContext http,
        RequestContext context,
        string? requested,
        string subject,
        out string? tenantId,
        [NotNullWhen(false)] out IResult? problem)
    {
        try
        {
            tenantId = TenantScopes.Resolve(context, requested, subject);
            problem = null;
            return true;
        }
        catch (ForbiddenException)
        {
            tenantId = null;
            problem = PlatformProblems.Create(
                http,
                context,
                ErrorCodes.TenantForbidden,
                "跨租户操作被拒绝",
                StatusCodes.Status403Forbidden,
                $"当前凭据绑定租户 '{context.EffectiveTenantId}'，无法访问租户 '{requested?.Trim()}' 的{subject}。");
            return false;
        }
    }
}
