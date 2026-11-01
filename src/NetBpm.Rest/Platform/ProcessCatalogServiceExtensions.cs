using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>流程目录的服务注册。</summary>
/// <remarks>
/// 目录主体是<b>读多写少</b>的：没有后台任务，只有「把三处信息合成一张名片」；
/// 唯一的写路径是管理面精修覆盖行（<c>/admin/v1/processes</c>），
/// 与被鉴权的公共读路径分开注册，见 <see cref="IProcessCatalogAdmin"/>。
/// 因此除了覆盖表要按部署形态二选一，其余都是进程内单例/作用域服务。
/// </remarks>
public static class ProcessCatalogServiceExtensions
{
    /// <summary>注册流程目录（有库走库、无库走内存）。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmProcessCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // BPMN 解析不便宜，而目录会被反复拉取（前端每次进审批中心都会刷一次）
        services.AddMemoryCache();

        services.AddSingleton<IProcessCatalogStore>(provider =>
            provider.GetRequiredService<NetBpmOptions>().UsesDatabase
                ? new DapperProcessCatalogStore(provider.GetRequiredService<IDbConnectionFactory>())
                : new InMemoryProcessCatalogStore());

        // 作用域：它要读本次调用的身份（租户 / 白名单 / scope），身份是 scoped。
        // 具体类型与两个接口指向同一个实例：读面与管理面共用一个目录对象，
        // 否则「判断流程 key 是否存在」这类逻辑会在两处实现并慢慢分叉。
        services.AddScoped<ProcessCatalog>();
        services.AddScoped<IProcessCatalog>(provider => provider.GetRequiredService<ProcessCatalog>());
        services.AddScoped<IProcessCatalogAdmin>(provider => provider.GetRequiredService<ProcessCatalog>());

        return services;
    }
}
