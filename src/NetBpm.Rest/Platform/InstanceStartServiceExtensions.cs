using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>发起流程编排的服务注册。</summary>
public static class InstanceStartServiceExtensions
{
    /// <summary>注册发起编排 + 业务主键发号器（单例）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceStart(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBusinessKeyGenerator, BusinessKeyGenerator>();
        services.AddScoped<IInstanceStartService, InstanceStartService>();

        return services;
    }
}
