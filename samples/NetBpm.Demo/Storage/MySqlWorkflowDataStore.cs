using NetBpm.Demo.Common;
using NetBpm.Persistence;
using NetBpm.Workflow;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 业务数据落 <b>MySQL 业务表</b>（库 netbpm_demo，无 schema 前缀）的实现。
/// </summary>
/// <remarks>
/// 端口逻辑全部在 <see cref="WorkflowDataStoreBase"/>，本类只挑 MySQL 方言
/// （<see cref="MySqlWorkflowStoreDialect"/>：limit 1 / 反引号保留字 / on duplicate key upsert）。
/// 表结构见 db/mysql/0003 / 0005 / 0007。
/// </remarks>
public sealed class MySqlWorkflowDataStore : WorkflowDataStoreBase
{
    /// <summary>构造存储。</summary>
    public MySqlWorkflowDataStore(
        IDbConnectionFactory connections,
        WorkflowRuntimeContext runtime,
        IConfiguration configuration,
        ILogger<MySqlWorkflowDataStore> logger)
        : base(MySqlWorkflowStoreDialect.Default, connections, runtime, configuration, logger)
    {
    }
}
