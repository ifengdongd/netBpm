namespace NetBpm.Identity;

/// <summary>
/// <see cref="IUserGroupManager"/> 的空实现：任何用户都不属于任何组，任何组都没有成员。
/// <para>用于「候选关系全部由外部系统给定」的场景，便于替换为真实的权限体系适配器。</para>
/// </summary>
public sealed class EmptyUserGroupManager : IUserGroupManager
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(string userId, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>([]);

    /// <inheritdoc />
    public ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default) =>
        ValueTask.FromResult<UserGroup?>(null);

    /// <inheritdoc />
    public ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(userId);

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(string groupId, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyCollection<string>>([]);
}
