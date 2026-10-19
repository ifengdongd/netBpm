namespace NetBpm.Rest.Security;

/// <summary>
/// 已认证，但请求声明的身份 / 租户与令牌不一致（映射为 403）。
/// <para>
/// 典型场景：JWT 里 <c>sub=alice</c>，请求体却传 <c>startUserId=bob</c>；
/// 或令牌租户是 <c>tenant-a</c>，查询参数却指定 <c>tenantId=tenant-b</c>。
/// </para>
/// </summary>
public sealed class ForbiddenException : Exception
{
    public ForbiddenException(string message)
        : base(message)
    {
    }
}
