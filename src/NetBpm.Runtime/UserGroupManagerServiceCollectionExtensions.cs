using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Identity;

namespace NetBpm.Runtime;

/// <summary>
/// 身份适配器的注册入口：把 <see cref="IUserGroupManager"/> 换成业务实现。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接让宿主写 <c>services.AddSingleton&lt;IUserGroupManager, X&gt;()</c>：
/// <c>AddNetBpm</c> 已经注册了 <see cref="EmptyUserGroupManager"/>，再 <c>Add</c> 一份看着像
/// 「后注册覆盖前注册」，其实容器里是两份；解析单个服务时取最后一份，看起来没事，但
/// 一旦顺序被挪到 <c>AddNetBpm</c> 之前、或有人按 <c>IEnumerable&lt;IUserGroupManager&gt;</c> 解析，
/// 就会拿回空实现 —— 候选组永远展开出 0 个人，待办静默消失且不报错（线上表现为"审批卡住"）。
/// </para>
/// <para>
/// <c>Replace</c>（配合 <c>AddNetBpm</c> 侧对默认实现的 <c>TryAdd</c>）让调用位置不再重要：
/// 放在 <c>AddNetBpm</c> 之前还是之后，容器里都只剩一份实现。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// builder.Services.AddNetBpm(builder.Configuration);
/// builder.Services.AddNetBpmUserGroupManager&lt;OpmsUserGroupManager&gt;();
/// </code>
/// </example>
public static class UserGroupManagerServiceCollectionExtensions
{
    /// <summary>用 <typeparamref name="TManager"/> 替换默认的空实现（与调用顺序无关）。</summary>
    /// <typeparam name="TManager">业务身份适配器（决定候选组 / 候选人如何展开成人）。</typeparam>
    /// <param name="services">服务集合。</param>
    /// <returns><paramref name="services"/>，便于链式调用。</returns>
    public static IServiceCollection AddNetBpmUserGroupManager<TManager>(this IServiceCollection services)
        where TManager : class, IUserGroupManager
    {
        ArgumentNullException.ThrowIfNull(services);

        // Replace 而非 Add：AddNetBpm 已注册 EmptyUserGroupManager，Add 会留下两份
        services.Replace(ServiceDescriptor.Singleton<IUserGroupManager, TManager>());

        return services;
    }
}
