using Microsoft.Extensions.DependencyInjection;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>待办读模型的服务注册。</summary>
/// <remarks>
/// 待办组装依赖引擎门面、流程定义仓储与审批语义字典，三者都已由宿主注册；
/// 这里只把 <see cref="ITaskCatalog"/> 挂进容器。它是有状态的（每请求一个），用 Scoped。
/// </remarks>
public static class TaskCatalogServiceExtensions
{
    /// <summary>注册待办读模型。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmTaskCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITaskCatalog, TaskCatalog>();

        return services;
    }
}
