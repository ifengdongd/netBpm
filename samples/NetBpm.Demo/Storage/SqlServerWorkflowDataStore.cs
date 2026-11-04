using NetBpm.Demo.Common;
using NetBpm.Persistence;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 流程数据落 <b>SQL Server</b>（库 netbpm_demo，统一 dbo，与 MySQL 表名逐字相同）的实现。
/// </summary>
/// <remarks>
/// 端口逻辑全部在 <see cref="WorkflowDataStoreBase"/>，本类只挑 SQL Server 方言
/// （<see cref="SqlServerWorkflowStoreDialect"/>：top (1) / 方括号保留字 / N 前缀字面量 /
/// 先 update-后 insert 的 upsert）。表结构见 db/sqlserver/netbpm_init.sql 的四张流程视图表。
/// </remarks>
public sealed class SqlServerWorkflowDataStore : WorkflowDataStoreBase
{
    /// <summary>构造存储。</summary>
    public SqlServerWorkflowDataStore(
        IDbConnectionFactory connections,
        WorkflowRuntimeContext runtime,
        IOrgDirectory org,
        IConfiguration configuration,
        ILogger<SqlServerWorkflowDataStore> logger)
        : base(SqlServerWorkflowStoreDialect.Default, connections, runtime, org, configuration, logger)
    {
    }
}
