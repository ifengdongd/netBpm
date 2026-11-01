using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Rest.Platform;

/// <summary>实例流转状态图的服务注册。</summary>
/// <remarks>
/// Scoped 而不是 Singleton：它依赖 <see cref="IInstanceCatalog"/>，
/// 而那个是每请求一份（携带调用方身份做可见性判定）。
/// </remarks>
public static class InstanceDiagramServiceExtensions
{
    /// <summary>注册实例流转状态图。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceDiagram(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInstanceDiagram, InstanceDiagram>();

        return services;
    }
}
