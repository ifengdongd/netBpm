using NetBpm.Demo.Common;
using NetBpm.Persistence;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 流程数据落 <b>MySQL</b>（库 netbpm_demo，无 schema 前缀）的实现。
/// </summary>
/// <remarks>
/// 端口逻辑全部在 <see cref="WorkflowDataStoreBase"/>，本类只挑 MySQL 方言
/// （<see cref="MySqlWorkflowStoreDialect"/>：limit 1 / 反引号保留字 / on duplicate key upsert）。
/// 表结构见 db/mysql/netbpm_init.sql 的 instances / instance_current_task / deal_history /
/// workflow_parameters 四张流程视图表（业务单表平台不再建）。
/// </remarks>
public sealed class MySqlWorkflowDataStore : WorkflowDataStoreBase
{
    /// <summary>构造存储。</summary>
    public MySqlWorkflowDataStore(
        IDbConnectionFactory connections,
        WorkflowRuntimeContext runtime,
        IOrgDirectory org,
        IConfiguration configuration,
        ILogger<MySqlWorkflowDataStore> logger)
        : base(MySqlWorkflowStoreDialect.Default, connections, runtime, org, configuration, logger)
    {
    }
}
