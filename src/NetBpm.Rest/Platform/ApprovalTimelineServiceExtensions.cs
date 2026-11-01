using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>审批记录时间线的服务注册。</summary>
/// <remarks>
/// 与语义字典不同，时间线是<b>写多读多</b>的：每次发起 / 提交都会追加一条。
/// 因此存储按作用域注册（<c>Scoped</c>），避免单例持有连接工厂以外的可变状态。
/// 无库宿主自动落到内存实现，测试不需要额外配置。
/// </remarks>
public static class ApprovalTimelineServiceExtensions
{
    /// <summary>注册审批记录时间线（有库走库、无库走内存）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmApprovalTimeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IApprovalTimelineStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperApprovalTimelineStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryApprovalTimelineStore());

        // 环节聚合读的是时间线条目，跟时间线一起注册；它是无状态的（只有流程定义缓存），用 Singleton。
        // 注册顺序无所谓，但必须早于任何依赖它的读模型解析 —— 容器解析时才校验，这里只是写明意图。
        services.AddSingleton<INodeOutcomeAggregator, NodeOutcomeAggregator>();

        services.AddScoped<IApprovalTimeline, ApprovalTimeline>();

        return services;
    }
}
