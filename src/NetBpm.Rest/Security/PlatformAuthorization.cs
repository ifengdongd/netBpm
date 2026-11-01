using NetBpm.Platform.Contracts;

namespace NetBpm.Rest.Security;

/// <summary>
/// 平台面（<c>/workflow/v1</c>）的 scope 判定。
/// </summary>
/// <remarks>
/// <para>
/// 与管理面的 <see cref="AdminAuthorization"/> 是<b>同一套风险观</b>，但判定对象不同：
/// 那里判 <c>netbpm.admin.*</c>（能不能管引擎），这里判 <c>workflow:*</c>（能不能用流程）。
/// </para>
/// <para>
/// 四种凭据的口径：
/// <list type="bullet">
///   <item><description><see cref="RequestAuthMode.None"/>：未启用鉴权（开发 / 单机演示），放行——
///     否则开箱即用的体验就没了；</description></item>
///   <item><description><see cref="RequestAuthMode.ApiKey"/>：旧版单值服务密钥，历史上就是
///     「可信服务 = 全权限」。收紧会直接打断已上线调用方，留给迁移期；</description></item>
///   <item><description><see cref="RequestAuthMode.Jwt"/>：令牌里的 <c>scope</c> 必须命中；
///     引擎超管（<c>netbpm.admin</c>）视为平台管理员，否则默认管理员登进来调不了目录；</description></item>
///   <item><description><see cref="RequestAuthMode.Client"/>：<b>必须逐条命中</b>，没有任何隐式放行——
///     这是「一系统一客户端」形态的核心，放松一处等于整套 scope 白做。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ scope 只是<b>能力上限</b>。数据级授权（租户边界、流程白名单、任务归属）由各端点在此之上另行校验，
/// 两层都要过。
/// </para>
/// </remarks>
public static class PlatformAuthorization
{
    /// <summary>当前调用方是否具备指定的平台 scope。</summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="scope">所需 scope（见 <see cref="PlatformScopes"/>）。</param>
    public static bool HasScope(RequestContext context, string scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (context.AuthMode is RequestAuthMode.None or RequestAuthMode.ApiKey)
        {
            return true;
        }

        // 引擎超管就是平台管理员：不这样映射，运维登进来第一件事是「目录 403」
        if (context.AuthMode == RequestAuthMode.Jwt && context.IsAdmin)
        {
            return true;
        }

        if (PlatformScopes.IsSatisfiedBy([.. context.Scopes], scope))
        {
            return true;
        }

        // 同一能力的两种话术：JWT 管理员拿的是 netbpm.admin.audit.read，
        // 平台上叫 workflow:admin:audit:read。不互认的话，运维调 /admin/v1/audit 会被 403，
        // 而调 /api/admin/audit 又能过 —— 这不是权限收紧，是两套名字互相不认识。
        var engineScope = EngineSurfaceScopes.EngineEquivalent(scope);

        return engineScope is not null && EngineSurfaceScopes.IsAdminSatisfied(context, engineScope);
    }

    /// <summary>当前调用方是否具备其中<b>任意一个</b> scope。</summary>
    /// <param name="context">请求身份上下文。</param>
    /// <param name="scopes">候选 scope 集合。</param>
    public static bool HasAnyScope(RequestContext context, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scopes);

        return scopes.Any(scope => HasScope(context, scope));
    }
}
