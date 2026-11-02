using NetBpm.Identity;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 身份模块的<b>只读</b>查询端点：用户所属组、组成员、组信息。
/// <para>
/// 本子集不提供用户 / 组 CRUD —— 真身在客户的 IAM 或权限中台，引擎只通过
/// <see cref="IUserGroupManager"/> 展开候选组。默认注册的是空实现，
/// 因此这些端点返回空集是「未接入身份源」的正常表现，不是错误。
/// </para>
/// <para>
/// 🔐 注册客户端按 <c>workflow:task:read</c> 记账：组展开是「谁能办这个任务」的一部分，
/// 与待办读取同级；组 ID / 成员清单属于组织架构，只能被已授权读待办的接入方看到。
/// </para>
/// </summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").WithTags("identity");

        group.MapGet("/users/{userId}/groups", FindGroupsByUserAsync)
            .RequireEngineScope(PlatformScopes.TaskRead, "查询用户所属组")
            .Produces<IReadOnlyList<UserGroupResponse>>();
        group.MapGet("/groups/{groupId}", FindGroupAsync)
            .RequireEngineScope(PlatformScopes.TaskRead, "查询组信息")
            .Produces<UserGroupResponse>();
        group.MapGet("/groups/{groupId}/users", FindUsersInGroupAsync)
            .RequireEngineScope(PlatformScopes.TaskRead, "查询组成员")
            .Produces<IReadOnlyList<string>>();

        return endpoints;
    }

    /// <summary>某用户所属的全部组（用于前端展开「我的候选组待办」）。</summary>
    private static async Task<IResult> FindGroupsByUserAsync(
        string userId,
        RequestContext requestContext,
        IUserGroupManager identity,
        CancellationToken ct)
    {
        // 只有本人、管理员或可信服务（API Key / 未启用鉴权）能看别人的组：
        // 否则任意已登录用户都能枚举组织架构。API Key 与其它鉴权规则一致，代表可信服务。
        if (requestContext.IsUserPrincipal
            && !requestContext.IsAdmin
            && !string.Equals(requestContext.UserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("只能查询自己的组；查询他人组需要管理员权限。");
        }

        var groupIds = await identity.FindGroupIdsByUserAsync(userId, ct).ConfigureAwait(false);

        var groups = new List<UserGroupResponse>(groupIds.Count);

        foreach (var groupId in groupIds)
        {
            var group = await identity.FindGroupByIdAsync(groupId, ct).ConfigureAwait(false);

            groups.Add(group is null
                ? new UserGroupResponse(groupId, null, null)
                : UserGroupResponse.From(group));
        }

        return Results.Ok(groups);
    }

    /// <summary>按 id 查组；不存在返回 404。</summary>
    private static async Task<IResult> FindGroupAsync(
        string groupId,
        IUserGroupManager identity,
        CancellationToken ct)
    {
        var group = await identity.FindGroupByIdAsync(groupId, ct).ConfigureAwait(false);

        return group is null
            ? EndpointResults.NotFound($"组 '{groupId}' 不存在（身份模块可能未接入）。")
            : Results.Ok(UserGroupResponse.From(group));
    }

    /// <summary>组成员（返回用户 id；空集是合法结果，见接口注释）。</summary>
    private static async Task<IResult> FindUsersInGroupAsync(
        string groupId,
        RequestContext requestContext,
        IUserGroupManager identity,
        CancellationToken ct)
    {
        var group = await identity.FindGroupByIdAsync(groupId, ct).ConfigureAwait(false);

        if (group is null)
        {
            return EndpointResults.NotFound($"组 '{groupId}' 不存在（身份模块可能未接入）。");
        }

        // 管理员 / 可信服务可看任意组；普通 JWT 用户只能看自己所在的组（候选人前端需要展开本组）
        if (requestContext.IsUserPrincipal && !requestContext.IsAdmin)
        {
            var userId = requestContext.UserId
                ?? throw new ForbiddenException("当前请求没有可识别的用户身份。");

            var memberships = await identity.FindGroupIdsByUserAsync(userId, ct).ConfigureAwait(false);

            if (!memberships.Contains(groupId, StringComparer.Ordinal))
            {
                throw new ForbiddenException($"当前用户不在组 '{groupId}' 中，无权查看成员。");
            }
        }

        var userIds = await identity.FindUserIdsByGroupAsync(groupId, ct).ConfigureAwait(false);

        return Results.Ok(userIds);
    }
}
