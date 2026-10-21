using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Demo.Storage.DataAccess;
using NetBpm.Persistence;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 订单业务数据落 <b>SQL Server <c>order_bill</c> 表</b>（dbo，与 MySQL 表名逐字相同）的实现。
/// </summary>
/// <remarks>
/// CRUD 全部委托 <see cref="IDataAccessor{TEntity}"/>（<see cref="SqlServerDataAccessor{TEntity}"/>：
/// 方括号引用、top 行数上限；<c>wf</c> schema 前缀在访问器里拼在表名前），
/// 本类只给出落点展示名。表结构见 db/sqlserver/netbpm_init.sql。
/// </remarks>
public sealed class SqlServerOrderBillStore : OrderBillStoreBase
{
    /// <summary>构造存储。</summary>
    public SqlServerOrderBillStore(
        IDataAccessor<OrderBillRow> accessor,
        IConfiguration configuration,
        ILogger<SqlServerOrderBillStore> logger)
        : base(accessor, configuration, logger)
    {
    }

    /// <inheritdoc />
    protected override string DisplayName => "SQL Server";
}
