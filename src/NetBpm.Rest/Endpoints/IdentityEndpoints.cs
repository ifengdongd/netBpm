using NetBpm.Identity;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 身份模块的<b>只读</b>查询端点：用户所属组、组成员、组信息。
/// <para>
/// 本子集不提供用户 / 组 CRUD —— 真身在客户的 IAM 或权限中台，引擎只通过
/// <see cref="IUserGroupManager"/> 展开候选组。默认注册的是空实现，
/// 因此这些端点返回空集是「未接入身份源」的正常表现，不是错误。
/// </para>
/// </summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity").WithTags("identity");

        group.MapGet("/users/{userId}/groups", FindGroupsByUserAsync)
            .Produces<IReadOnlyList<UserGroupResponse>>();
        group.MapGet("/groups/{groupId}", FindGroupAsync).Produces<UserGroupResponse>();
        group.MapGet("/groups/{groupId}/users", FindUsersInGroupAsync).Produces<IReadOnlyList<string>>();

        return endpoints;
    }

    /// <summary>某用户所属的全部组（用于前端展开「我的候选组待办」）。</summary>
    private static async Task<IResult> FindGroupsByUserAsync(
        string userId,
        IUserGroupManager identity,
        CancellationToken ct)
    {
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
            ? Results.NotFound(new ErrorResponse($"组 '{groupId}' 不存在（身份模块可能未接入）。"))
            : Results.Ok(UserGroupResponse.From(group));
    }

    /// <summary>组成员（返回用户 id；空集是合法结果，见接口注释）。</summary>
    private static async Task<IResult> FindUsersInGroupAsync(
        string groupId,
        IUserGroupManager identity,
        CancellationToken ct)
    {
        var group = await identity.FindGroupByIdAsync(groupId, ct).ConfigureAwait(false);

        if (group is null)
        {
            return Results.NotFound(new ErrorResponse($"组 '{groupId}' 不存在（身份模块可能未接入）。"));
        }

        var userIds = await identity.FindUserIdsByGroupAsync(groupId, ct).ConfigureAwait(false);

        return Results.Ok(userIds);
    }
}
