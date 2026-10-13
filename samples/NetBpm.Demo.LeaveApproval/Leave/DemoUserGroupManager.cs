using NetBpm.Identity;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 演示用身份模块：把用户归到 <c>leaders</c> / <c>managers</c> 组。
/// </summary>
/// <remarks>
/// 为什么要换掉默认的 <see cref="EmptyUserGroupManager"/>：流程定义里写的是候选组
/// （<c>activiti:candidateGroups="leaders"</c>），而「按候选人查待办」必须先展开
/// 「该用户属于哪些组」；空实现下谁都查不到候选组任务，演示页的待办中心就永远是空的。
/// 真实项目这里应接既有权限体系（AD / 组织架构 / 权限中台）。
/// </remarks>
public sealed class DemoUserGroupManager : IUserGroupManager
{
    /// <summary>用户 → 所属组。</summary>
    private static readonly Dictionary<string, string[]> Membership = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alice"] = [],
        ["leader1"] = ["leaders"],
        ["manager1"] = ["managers"],
    };

    /// <summary>组定义。</summary>
    private static readonly Dictionary<string, UserGroup> Groups = new(StringComparer.Ordinal)
    {
        ["leaders"] = new UserGroup("leaders", "组长"),
        ["managers"] = new UserGroup("managers", "经理"),
    };

    /// <summary>
    /// 组 → 成员，由上面那张「用户 → 组」倒排出来 —— 两份表手工维护迟早会不一致。
    /// </summary>
    private static readonly Dictionary<string, string[]> Members = Membership
        .SelectMany(pair => pair.Value.Select(group => (Group: group, User: pair.Key)))
        .GroupBy(item => item.Group, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.Select(item => item.User).OrderBy(user => user, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(
        string userId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>(
            !string.IsNullOrWhiteSpace(userId) && Membership.TryGetValue(userId, out var groups)
                ? groups
                : []);

    /// <inheritdoc />
    public ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default) =>
        ValueTask.FromResult(
            !string.IsNullOrWhiteSpace(groupId) && Groups.TryGetValue(groupId, out var group)
                ? group
                : null);

    /// <inheritdoc />
    public ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(userId);

    /// <summary>
    /// 组里的成员。<b>「流程走到组长审批时，审批人到底是谁」就是靠它答出来的</b> ——
    /// 任务上挂的是候选组，不展开就只有组名、没有人。
    /// </summary>
    /// <remarks>
    /// 真实权限体系里这一步往往受限（只能按需展开、或需要服务账号），
    /// 展开不出来返回空集是允许的：那表示「审批人未知」，业务数据照常落。
    /// </remarks>
    public ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(
        string groupId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>(
            !string.IsNullOrWhiteSpace(groupId) && Members.TryGetValue(groupId, out var users)
                ? users
                : []);
}
