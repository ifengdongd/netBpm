using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>实例列表读模型的依赖注入注册。</summary>
public static class InstanceListCatalogServiceExtensions
{
    /// <summary>注册 <see cref="IInstanceListCatalog"/>（实例列表读模型）。</summary>
    /// <remarks>
    /// 与 <c>AddNetBpmInstanceCatalog</c>（单条视图）分开注册是刻意的：
    /// 列表与详情是两条独立的读路径，谁改了都不该逼着另一个重新注册。
    /// </remarks>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceList(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInstanceListCatalog, InstanceListCatalog>();

        return services;
    }
}
