using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Rest.Diagnostics;

namespace NetBpm.Rest.Security;

/// <summary>
/// 管理能力的细粒度授权。
/// <para>
/// 判定口径与任务 / 变量授权保持一致：
/// <list type="bullet">
///   <item><description>未启用凭据（开发 / 单机演示）：放行；</description></item>
///   <item><description>旧版 API Key：调用方是<b>可信服务</b>，服务级凭据本身即代表管理授权，放行；</description></item>
///   <item><description>JWT 用户 / 注册客户端：必须持有对应 <c>netbpm.admin.*</c> scope，或超级范围 <c>netbpm.admin</c>。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>注册客户端不再自动获得管理授权</b>。旧 API Key 是「一个密钥代表整个可信内网」的历史产物；
/// 客户端是「一个业务系统」——只被授予 <c>workflow:instance:read</c> 的系统若不逐条命中也放行，
/// 多系统接入后第一周就会出越权事故。
/// </para>
/// <para>
/// ⚠️ 这里只回答「是否具备管理能力」。业务参与人依业务规则可以做的事（办理人完成任务、发起人写变量等）
/// 不经过本类判定，否则会把正常办理也挡在门外。
/// </para>
/// </summary>
public static class AdminAuthorization
{
    /// <summary>
    /// 校验当前请求是否具备指定管理 scope；不具备时抛 <see cref="ForbiddenException"/>（映射 403）。
    /// </summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="scope">所需 scope（见 <see cref="TaskScopes"/>）。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    public static void Ensure(RequestContext context, string scope, string action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        // 只对「旧口径」放行：未启用鉴权 + 单值 API Key。
        // 客户端凭据（RequestAuthMode.Client）必须逐条命中 scope。
        if (context.AuthMode is RequestAuthMode.None or RequestAuthMode.ApiKey)
        {
            return;
        }

        if (context.HasAdminScope(scope))
        {
            return;
        }

        // 越权尝试计数（按 scope / 鉴权方式分桶）：给 RBAC 的「谁在撞门」提供可观测面
        AdminDiagnostics.AuthorizationDenied.Add(
            1,
            new(AdminDiagnostics.TagScope, scope),
            new(AdminDiagnostics.TagAuthMode, AdminDiagnostics.AuthModeTag(context.AuthMode)));

        throw new ForbiddenException(
            $"当前主体缺少 scope '{scope}'，无法{action}；持有 '{TaskScopes.Admin}' 可覆盖全部管理动作。");
    }

    /// <summary>
    /// 给端点挂上管理 scope 校验（在 handler 之前执行，越权返回 403）。
    /// </summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="scope">所需 scope。</param>
    /// <param name="action">动作描述（用于 403 文案）。</param>
    public static RouteHandlerBuilder RequireAdminScope(
        this RouteHandlerBuilder builder,
        string scope,
        string action)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        return builder.AddEndpointFilter(async (invocationContext, next) =>
        {
            var requestContext = invocationContext.HttpContext.RequestServices
                .GetRequiredService<RequestContext>();

            Ensure(requestContext, scope, action);

            return await next(invocationContext).ConfigureAwait(false);
        });
    }
}
