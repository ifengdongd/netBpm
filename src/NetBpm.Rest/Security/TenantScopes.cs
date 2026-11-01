namespace NetBpm.Rest.Security;

/// <summary>
/// 租户边界判定（管理面共用的两个小函数）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么抽出来：接入管理（<c>/api/admin</c> 与 <c>/admin/v1</c>）、事件订阅管理都要回答同样两个问题——
/// 「这个租户我看不看得到」「列表该按哪个租户过滤」。原来这两个函数挂在
/// <c>ClientAdminEndpoints</c> 上，于是订阅端点要 <c>using</c> 一个和自己无关的端点类去拿它，
/// 等那组端点评 <c>[Obsolete]</c> 之后调用方还得跟着加 <c>#pragma</c>——纯属被结构拖累。
/// </para>
/// <para>
/// 🔑 判据只用 <see cref="RequestContext.EffectiveTenantId"/>：为空表示这份凭据不受租户约束
/// （旧 API Key 未配置 <c>ApiKeyTenants</c> 的情形），此时不做收窄。**不要**改成
/// 「<see cref="RequestContext.IsTenantRestricted"/> 为真但租户为空就一律拒绝」——
/// 那样会让所有没声明租户的旧 Key 当场失去管理能力。
/// </para>
/// </remarks>
public static class TenantScopes
{
    /// <summary>这份凭据能不能看 / 改该租户的数据。</summary>
    /// <param name="context">调用方身份。</param>
    /// <param name="tenantId">目标租户。</param>
    public static bool IsVisible(RequestContext context, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantId);

        return context.EffectiveTenantId is not { } scoped
            || string.Equals(scoped, tenantId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 列表接口的租户过滤：显式参数不得越出凭据所属租户。
    /// </summary>
    /// <param name="context">调用方身份。</param>
    /// <param name="requested">请求里的 <c>?tenantId=</c>。</param>
    /// <param name="resource">越界时错误文案里的资源名（如「客户端」）。</param>
    /// <exception cref="ForbiddenException">请求的租户与凭据所属租户不一致。</exception>
    public static string? Resolve(RequestContext context, string? requested, string resource = "数据")
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EffectiveTenantId is not { } scoped)
        {
            return string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            return scoped;
        }

        if (!string.Equals(scoped, requested.Trim(), StringComparison.Ordinal))
        {
            throw new ForbiddenException($"无权查看租户 '{requested.Trim()}' 的{resource}。");
        }

        return scoped;
    }
}
