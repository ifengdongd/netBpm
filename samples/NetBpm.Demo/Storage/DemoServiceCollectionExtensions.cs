using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Demo.Order;
using NetBpm.Identity;
using NetBpm.Persistence;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// Demo 的存储注册：引擎流程数据落真库（方言随 <c>NetBpm:DatabaseType</c>），
/// 业务侧与组织架构用示例自带的内存实现。
/// </summary>
/// <remarks>
/// <para>
/// 注册的是「一套三个」端口 —— 应用层门面、核对端口、订单行落点：
/// </para>
/// <list type="bullet">
///   <item><description><b>流程数据</b>：MySqlWorkflowDataStore / SqlServerWorkflowDataStore
///     （instances / instance_current_task / deal_history / workflow_parameters 四张流程视图表）；</description></item>
///   <item><description><b>订单行</b>：<see cref="InMemoryOrderBillStore"/> —— 业务单表归接入方自己的库，
///     平台脚本不再建 <c>order_bill</c>，示例也就不落库（端口语义不变，换成自己的表即可）；</description></item>
///   <item><description><b>组织架构</b>：<see cref="InMemoryOrgDirectory"/> —— 平台不持有组织主数据，
///     需要真表就自己实现 <see cref="IOrgDirectory"/> / <see cref="IUserGroupManager"/>。</description></item>
///   <item><description><b>IWorkflowStoreInspector</b>：业务数据核对端点（/api/leave/business）的只读口子。</description></item>
/// </list>
/// <para>
/// ⚠️ 引擎必须与业务数据一起落同一个真库（NetBpm:UseInMemoryExecutionTree=false）：
/// 内存宿主的流程实例 id 每次启动都从 1 重新开始，而真库里的 instances 是累积的 —— 混用会撞号覆盖。
/// </para>
/// </remarks>
public static class DemoServiceCollectionExtensions
{
    /// <summary>按 <c>NetBpm:DatabaseType</c> 注册存储端口。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置（读 <c>NetBpm:DatabaseType</c>，缺省 MySQL）。</param>
    public static IServiceCollection AddDemoStores(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseType = configuration.GetValue("NetBpm:DatabaseType", DatabaseType.MySql);

        // 订单业务行：方言无关（内存实现），换成自己的库时在这里替换
        services.AddSingleton<IOrderBillStore, InMemoryOrderBillStore>();

        // 组织架构：平台不持有组织主数据 —— 两种方言都用内置演示数据，换真表请自行实现两个端口
        services.AddSingleton<InMemoryOrgDirectory>();
        services.AddSingleton<IOrgDirectory>(sp => sp.GetRequiredService<InMemoryOrgDirectory>());
        services.Replace(ServiceDescriptor.Singleton<IUserGroupManager>(
            sp => sp.GetRequiredService<InMemoryOrgDirectory>()));

        // 流程数据（四张视图表）：方言决定 SQL 写法
        switch (databaseType)
        {
            case DatabaseType.SqlServer:
                services.AddNetBpmWorkflow<SqlServerWorkflowDataStore>();
                services.AddSingleton<IWorkflowStoreInspector>(sp =>
                    sp.GetRequiredService<SqlServerWorkflowDataStore>());
                break;

            default:
                services.AddNetBpmWorkflow<MySqlWorkflowDataStore>();
                services.AddSingleton<IWorkflowStoreInspector>(sp =>
                    sp.GetRequiredService<MySqlWorkflowDataStore>());
                break;
        }

        return services;
    }
}
