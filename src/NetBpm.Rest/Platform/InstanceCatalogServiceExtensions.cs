using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>实例读模型的服务注册。</summary>
/// <remarks>
/// 实例组装依赖引擎门面、执行树管理器、流程定义仓储与待办读模型，四者都已由宿主注册；
/// 这里只把 <see cref="IInstanceCatalog"/> 挂进容器。它是有状态的（每请求一个），用 Scoped。
/// </remarks>
public static class InstanceCatalogServiceExtensions
{
    /// <summary>注册实例读模型。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInstanceCatalog, InstanceCatalog>();

        return services;
    }
}
