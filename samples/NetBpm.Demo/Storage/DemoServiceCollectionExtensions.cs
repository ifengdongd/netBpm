using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// Demo 的存储注册：按 <c>Workflow:Storage</c> 配置选择 MySQL / JSON 实现。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 注册的是「一套三个」端口 —— 应用层门面、核对端口、订单行落点指向同一份实现：
/// </para>
/// <list type="bullet">
///   <item><description>MySql：`MySqlWorkflowDataStore`（instances 等四张流程表）+ `MySqlOrderBillStore`（order_bill）；</description></item>
///   <item><description>Json：`JsonWorkflowDataStore`（data/workflow.json）+ `OrderBillStore`（data/order-bill.json）。</description></item>
/// </list>
/// <para>
/// ⚠️ 引擎与业务数据必须一起切：内存宿主的流程实例 id 每次启动都从 1 重新开始，
/// 而真库里的 instances 是累积的 —— 混用会撞号覆盖（见 appsettings.json 注释）。
/// </para>
/// </remarks>
public static class DemoServiceCollectionExtensions
{
    /// <summary>按配置注册业务数据存储的三个端口。</summary>
    public static IServiceCollection AddDemoStores(this IServiceCollection services, IConfiguration configuration)
    {
        var storage = configuration[ConstField.StorageConfigKey] ?? ConstField.DefaultStorage;

        if (string.Equals(storage, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            // MySQL 真库模式：instances / instance_current_task / deal_history / workflow_parameters + order_bill
            services.AddNetBpmWorkflow<Storage.MySqlWorkflowDataStore>();
            services.AddSingleton<IWorkflowStoreInspector>(provider =>
                provider.GetRequiredService<Storage.MySqlWorkflowDataStore>());
            services.AddSingleton<IOrderBillStore, Storage.MySqlOrderBillStore>();
        }
        else
        {
            // JSON 演示模式
            services.AddNetBpmWorkflow<JsonWorkflowDataStore>();
            services.AddSingleton<IWorkflowStoreInspector>(provider =>
                provider.GetRequiredService<JsonWorkflowDataStore>());
            services.AddSingleton<IOrderBillStore, OrderBillStore>();
        }

        return services;
    }
}
