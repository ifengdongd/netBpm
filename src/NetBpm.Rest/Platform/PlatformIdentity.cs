using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 平台面的「管理视角」判定（一处定义，避免同一组 scope 在多个读模型里各写一遍）。
/// </summary>
/// <remarks>
/// <para>
/// 三个条件等价：旧的管理范围 <c>netbpm.admin</c>、实例干预 scope、以及审计用的
/// <c>workflow:history:read:all</c>。它们都代表「可以越过『只看自己相关』这条数据边界」。
/// </para>
/// <para>
/// 刻意<b>不</b>把 <c>netbpm.admin</c> 折算成 <c>visibleTo=ADMIN</c> 角色：
/// 管理视角在可见性上应当<b>不受限</b>，而「加上 ADMIN 角色」会让
/// <c>visibleTo="APPLICANT"</c> 的字段反而对管理员消失。
/// </para>
/// </remarks>
internal static class PlatformIdentity
{
    /// <summary>调用方是否持管理视角（可跨人看单据、可越权改参数）。</summary>
    /// <param name="context">调用方身份。</param>
    public static bool HasAdminPerspective(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.IsAdmin
            || context.HasAdminScope(TaskScopes.AdminInstanceJump)
            || context.HasScope(PlatformScopes.HistoryReadAll);
    }
}
