using Microsoft.Extensions.DependencyInjection;

namespace NetBpm.Admin.Storage;

/// <summary>管理端存储注册：模型 + 流程类型两个 Dapper 存取器。</summary>
public static class AdminServiceCollectionExtensions
{
    /// <summary>注册管理端业务存储（依赖 <c>IDbConnectionFactory</c>，由 AddNetBpm 提供）。</summary>
    public static IServiceCollection AddAdminStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BpmnModelStore>();
        services.AddSingleton<ProcessTypeStore>();

        return services;
    }
}
