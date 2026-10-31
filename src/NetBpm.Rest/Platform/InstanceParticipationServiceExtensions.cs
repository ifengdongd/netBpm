using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>参与者索引的依赖注入注册。</summary>
public static class InstanceParticipationServiceExtensions
{
    /// <summary>注册 <see cref="IInstanceParticipation"/>（参与者索引）。</summary>
    /// <remarks>
    /// 单独注册而不是塞进 <c>AddNetBpmInstanceList</c>：这是跨列表（实例列表 / 待办已办 /
    /// 将来的时间线可见性）共享的「人 → 单据」入口，谁依赖它不该反过来被它的注册方式约束。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceParticipation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInstanceParticipation, InstanceParticipation>();

        return services;
    }
}
