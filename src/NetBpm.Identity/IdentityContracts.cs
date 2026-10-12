namespace NetBpm.Identity;

/// <summary>
/// 候选关系类型。对应 Activiti 的 <c>IdentityLinkType</c>；
/// 取值必须与 <c>ACT_RU_IDENTITYLINK.TYPE_</c> 中的字符串完全一致（§7.1）。
/// </summary>
public static class IdentityLinkType
{
    /// <summary>办理人。</summary>
    public const string Assignee = "assignee";

    /// <summary>候选人 / 候选组。</summary>
    public const string Candidate = "candidate";

    /// <summary>任务所有者。</summary>
    public const string Owner = "owner";

    /// <summary>参与者。</summary>
    public const string Participant = "participant";

    /// <summary>流程发起人。</summary>
    public const string Starter = "starter";
}

/// <summary>组。</summary>
/// <param name="Id">组 id。</param>
/// <param name="Name">组名。</param>
/// <param name="Type">组类型。</param>
public sealed record UserGroup(string Id, string? Name = null, string? Type = null);

/// <summary>
/// 用户 / 组 / 成员关系的最小实现。
/// <para>
/// 本模块<b>刻意保持最小</b>：Activiti 的身份模块为可替换的独立组件，多数落地项目
/// 都会接既有权限体系。这里只定义引擎侧需要的最小契约（候选组展开）。
/// </para>
/// </summary>
public interface IUserGroupManager
{
    /// <summary>某用户所属的全部组 id。</summary>
    ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(string userId, CancellationToken ct = default);

    /// <summary>按 id 查组。</summary>
    ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default);

    /// <summary>查用户。</summary>
    ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default);
}
