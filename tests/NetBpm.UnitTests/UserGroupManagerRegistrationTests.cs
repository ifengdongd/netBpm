using Microsoft.Extensions.DependencyInjection;
using NetBpm.Identity;
using NetBpm.Runtime;

namespace NetBpm.UnitTests;

/// <summary>
/// <see cref="UserGroupManagerServiceCollectionExtensions.AddNetBpmUserGroupManager{TManager}"/> 的替换语义。
/// </summary>
/// <remarks>
/// 这两条断言守的是集成期最贵的坑：<c>AddNetBpm</c> 先注册了空实现，宿主再 <c>Add</c> 一份自己的实现时，
/// 容器里会有两份；解析单个服务取最后一份（看起来没事），但顺序一挪或多重解析就会拿回空实现，
/// 表现是"候选组展开 0 人、待办静默消失"。用 <c>Replace</c> 之后与调用顺序无关。
/// </remarks>
public sealed class UserGroupManagerRegistrationTests
{
    [Fact]
    public void AfterAddNetBpm_ShouldReplaceEmptyImplementation()
    {
        using var provider = Build(helperFirst: false);

        var managers = provider.GetServices<IUserGroupManager>().ToArray();

        Assert.Single(managers);
        Assert.IsType<FakeUserGroupManager>(managers[0]);
    }

    [Fact]
    public void BeforeAddNetBpm_ShouldStillWin()
    {
        using var provider = Build(helperFirst: true);

        var managers = provider.GetServices<IUserGroupManager>().ToArray();

        Assert.Single(managers);
        Assert.IsType<FakeUserGroupManager>(managers[0]);
    }

    [Fact]
    public void WithoutHelper_ShouldFallBackToEmptyImplementation()
    {
        var services = new ServiceCollection();
        services.AddNetBpm(options =>
        {
            options.UseInMemoryExecutionTree = true;
            options.EnableHistory = false;
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<EmptyUserGroupManager>(provider.GetRequiredService<IUserGroupManager>());
    }

    private static ServiceProvider Build(bool helperFirst)
    {
        var services = new ServiceCollection();

        if (helperFirst)
        {
            services.AddNetBpmUserGroupManager<FakeUserGroupManager>();
        }

        // 内存宿主：不连库，EnableHistory 必须关（数据库宿主才写 ACT_HI_*）
        services.AddNetBpm(options =>
        {
            options.UseInMemoryExecutionTree = true;
            options.EnableHistory = false;
        });

        if (!helperFirst)
        {
            services.AddNetBpmUserGroupManager<FakeUserGroupManager>();
        }

        return services.BuildServiceProvider();
    }

    /// <summary>只用来证明"容器里那一份是谁"，不实现任何候选展开逻辑。</summary>
    private sealed class FakeUserGroupManager : IUserGroupManager
    {
        public ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(string userId, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyCollection<string>>([]);

        public ValueTask<UserGroup?> FindGroupByIdAsync(string groupId, CancellationToken ct = default) =>
            ValueTask.FromResult<UserGroup?>(null);

        public ValueTask<string?> FindUserIdAsync(string userId, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(userId);

        public ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(string groupId, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyCollection<string>>([]);
    }
}
