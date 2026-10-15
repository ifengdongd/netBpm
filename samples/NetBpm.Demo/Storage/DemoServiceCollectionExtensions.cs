using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// Demo 的存储注册：业务数据固定落 MySQL 真库（库 netbpm_demo）。
/// </summary>
/// <remarks>
/// <para>
/// 注册的是「一套三个」端口 —— 应用层门面、核对端口、订单行落点指向同一份实现：
/// </para>
/// <list type="bullet">
///   <item><description>MySqlWorkflowDataStore：instances / instance_current_task / deal_history / workflow_parameters 四张流程表；</description></item>
///   <item><description>MySqlOrderBillStore：order_bill 订单行；</description></item>
///   <item><description>IWorkflowStoreInspector：业务数据核对端点（/api/leave/business）的只读口子。</description></item>
/// </list>
/// <para>
/// ⚠️ 引擎必须与业务数据一起落 MySQL（NetBpm:UseInMemoryExecutionTree=false）：
/// 内存宿主的流程实例 id 每次启动都从 1 重新开始，而真库里的 instances 是累积的 —— 混用会撞号覆盖。
/// </para>
/// </remarks>
public static class DemoServiceCollectionExtensions
{
    /// <summary>注册业务数据存储的三个端口（MySQL）。</summary>
    public static IServiceCollection AddDemoStores(this IServiceCollection services)
    {
        services.AddNetBpmWorkflow<MySqlWorkflowDataStore>();
        services.AddSingleton<IWorkflowStoreInspector>(provider =>
            provider.GetRequiredService<MySqlWorkflowDataStore>());
        services.AddSingleton<IOrderBillStore, MySqlOrderBillStore>();

        return services;
    }
}
