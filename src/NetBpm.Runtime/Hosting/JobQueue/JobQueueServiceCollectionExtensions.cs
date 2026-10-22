using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using StackExchange.Redis;

namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// 延迟作业队列的组合根接线：<b>由配置决定用哪种实现</b>。
/// </summary>
public static class JobQueueServiceCollectionExtensions
{
    /// <summary>配置键：<c>NetBpm:Jobs:Queue:Provider</c>。</summary>
    public const string ProviderKey = "Provider";

    /// <summary>
    /// 按 <c>NetBpm:Jobs:Queue:Provider</c> 注册 <see cref="IJobQueue"/>。
    /// </summary>
    /// <remarks>
    /// 未显式注册过时默认使用 <see cref="InMemoryJobQueue"/>；
    /// 若在此之前已经注册过 <see cref="IJobQueue"/>（宿主自行接管），本方法不会覆盖。
    /// </remarks>
    public static IServiceCollection AddNetBpmJobQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "NetBpm:Jobs:Queue")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(sectionName);
        var provider = section[ProviderKey];

        // Redis 段的键带 "Redis" 前缀：NetBpm:Jobs:Queue:Redis:Host 等
        services.AddOptions<RedisJobQueueOptions>()
            .Bind(configuration.GetSection(sectionName + ":Redis"));

        if (string.IsNullOrWhiteSpace(provider)
            || provider.Trim().Equals(nameof(JobQueueProvider.Memory), StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IJobQueue, InMemoryJobQueue>();

            return services;
        }

        if (provider.Trim().Equals(nameof(JobQueueProvider.Redis), StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IConnectionMultiplexer>(sp =>
                ConnectionMultiplexer.Connect(
                    RedisJobQueue.BuildConfiguration(
                        sp.GetRequiredService<IOptions<RedisJobQueueOptions>>().Value)));

            services.TryAddSingleton<IJobQueue>(sp => new RedisJobQueue(
                sp.GetRequiredService<IOptions<RedisJobQueueOptions>>(),
                sp.GetService<ILogger<RedisJobQueue>>()));

            return services;
        }

        throw new InvalidOperationException(
            $"未知的作业队列实现 '{provider}'（配置 {sectionName}:{ProviderKey}）。" +
            $"可选值：{nameof(JobQueueProvider.Memory)} / {nameof(JobQueueProvider.Redis)}。");
    }
}
