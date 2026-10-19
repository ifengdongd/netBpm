using NetBpm.Engine;

namespace NetBpm.Rest.Security;

/// <summary>
/// 请求声明的身份（请求体 / 查询参数）与鉴权上下文之间的唯一裁决点。
/// <para>
/// 规则：
/// <list type="bullet">
///   <item><description>JWT：令牌是唯一可信来源；请求声明了不同值 → 403；未声明 → 用令牌值。</description></item>
///   <item><description>API Key / 未启用鉴权：保留「显式传参优先，缺省回落到上下文」的既有行为。</description></item>
/// </list>
/// </para>
/// </summary>
internal static class RequestIdentity
{
    /// <summary>解析租户：JWT 下禁止用 query / body 跨租户；限定租户的服务密钥同样不能越界。</summary>
    public static string? Tenant(string? requested, RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.IsUserPrincipal)
        {
            EnsureMatches(requested, context.TenantId, "租户", normalizeEmpty: true);

            return context.TenantId;
        }

        var tenant = requested ?? context.TenantId;

        if (context.AuthMode == RequestAuthMode.ApiKey && context.AllowedTenants.Count > 0)
        {
            if (string.IsNullOrEmpty(tenant))
            {
                // 只授权了一个租户时自动落到它；多个租户必须显式指定，避免“全租户”查询
                if (context.AllowedTenants.Count == 1)
                {
                    tenant = context.AllowedTenants.First();
                }
                else
                {
                    throw new ForbiddenException(
                        "服务密钥配置了多个租户，请显式指定 tenantId（请求体或查询参数）。");
                }
            }
            else if (!context.AllowedTenants.Contains(tenant))
            {
                throw new ForbiddenException($"服务密钥无权访问租户 '{tenant}'。");
            }
        }

        return tenant;
    }

    /// <summary>解析操作人：JWT 下禁止用请求体冒充他人。</summary>
    public static string? User(string? requested, RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsUserPrincipal)
        {
            return requested ?? context.UserId;
        }

        EnsureMatches(requested, context.UserId, "操作人", normalizeEmpty: false);

        return context.UserId;
    }

    private static void EnsureMatches(string? requested, string? tokenValue, string field, bool normalizeEmpty)
    {
        if (requested is null)
        {
            return;
        }

        var left = normalizeEmpty ? requested.Trim() : requested;
        var right = normalizeEmpty ? (tokenValue ?? string.Empty).Trim() : tokenValue ?? string.Empty;

        if (!string.Equals(left, right, StringComparison.Ordinal))
        {
            throw new ForbiddenException(
                $"{field}与令牌不一致：请求声明 '{requested}'，令牌为 '{tokenValue ?? "(空)"}'。"
                + "JWT 模式下身份只能来自令牌。");
        }
    }
}

/// <summary>
/// 按 id 访问资源时的租户隔离。
/// <para>
/// JWT 一律受限；API Key 在配置了 <c>AllowedTenants</c> 时受限（服务作用域），
/// 未配置时保持“可信服务可跨租户”的旧行为。
/// 不匹配一律抛 <see cref="EngineObjectNotFoundException"/>（404），避免用 403 泄露对象是否存在。
/// </para>
/// </summary>
internal static class TenantGuard
{
    /// <summary>校验实体租户与生效租户一致；不一致按“不存在”处理。</summary>
    public static void Ensure(RequestContext context, string? entityTenantId, string objectName, string objectId)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsTenantRestricted)
        {
            return;
        }

        if (!string.Equals(
                entityTenantId ?? string.Empty,
                context.EffectiveTenantId ?? string.Empty,
                StringComparison.Ordinal))
        {
            throw new EngineObjectNotFoundException($"{objectName} '{objectId}' 不存在。");
        }
    }
}
