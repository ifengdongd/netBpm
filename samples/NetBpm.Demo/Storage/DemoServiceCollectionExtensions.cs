using NetBpm.Demo.Order;
using NetBpm.Persistence;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// Demo 的存储注册：业务数据落真库（与引擎同一个库），方言随 <c>NetBpm:DatabaseType</c>。
/// </summary>
/// <remarks>
/// <para>
/// 注册的是「一套三个」端口 —— 应用层门面、核对端口、订单行落点指向同一份实现：
/// </para>
/// <list type="bullet">
///   <item><description><b>MySql</b>：MySqlWorkflowDataStore（instances / instance_current_task /
///     deal_history / workflow_parameters 四张流程表）+ MySqlOrderBillStore（order_bill）；</description></item>
///   <item><description><b>SqlServer</b>：SqlServerWorkflowDataStore + SqlServerOrderBillStore
///     （同一套表结构，落在 <c>wf</c> schema，见 db/sqlserver/0003/0006/0007）；</description></item>
///   <item><description><b>IWorkflowStoreInspector</b>：业务数据核对端点（/api/leave/business）的只读口子。</description></item>
/// </list>
/// <para>
/// ⚠️ 引擎必须与业务数据一起落同一个真库（NetBpm:UseInMemoryExecutionTree=false）：
/// 内存宿主的流程实例 id 每次启动都从 1 重新开始，而真库里的 instances 是累积的 —— 混用会撞号覆盖。
/// </para>
/// </remarks>
public static class DemoServiceCollectionExtensions
{
    /// <summary>按 <c>NetBpm:DatabaseType</c> 注册业务数据存储的三个端口。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置（读 <c>NetBpm:DatabaseType</c>，缺省 MySQL）。</param>
    public static IServiceCollection AddDemoStores(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseType = configuration.GetValue("NetBpm:DatabaseType", DatabaseType.MySql);

        switch (databaseType)
        {
            case DatabaseType.SqlServer:
                services.AddNetBpmWorkflow<SqlServerWorkflowDataStore>();
                services.AddSingleton<IWorkflowStoreInspector>(sp =>
                    sp.GetRequiredService<SqlServerWorkflowDataStore>());
                services.AddSingleton<IOrderBillStore, SqlServerOrderBillStore>();
                break;

            default:
                services.AddNetBpmWorkflow<MySqlWorkflowDataStore>();
                services.AddSingleton<IWorkflowStoreInspector>(sp =>
                    sp.GetRequiredService<MySqlWorkflowDataStore>());
                services.AddSingleton<IOrderBillStore, MySqlOrderBillStore>();
                break;
        }

        return services;
    }
}
