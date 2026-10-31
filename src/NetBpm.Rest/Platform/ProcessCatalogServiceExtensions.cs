using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>流程目录的服务注册。</summary>
/// <remarks>
/// 目录是<b>读多写零</b>的：没有后台任务、没有写路径，只有「把三处信息合成一张名片」。
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

        // 作用域：它要读本次调用的身份（租户 / 白名单 / scope），身份是 scoped
        services.AddScoped<IProcessCatalog, ProcessCatalog>();

        return services;
    }
}
