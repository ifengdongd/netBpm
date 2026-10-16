using NetBpm.Demo.Common;
using NetBpm.Demo.Order;
using NetBpm.Demo.Storage.DataAccess;
using NetBpm.Persistence;

namespace NetBpm.Demo.Storage;

/// <summary>
/// 订单业务数据落 <b>MySQL <c>order_bill</c> 表</b>（无 schema 前缀）的实现。
/// </summary>
/// <remarks>
/// CRUD 全部委托 <see cref="IDataAccessor{TEntity}"/>（<see cref="MySqlDataAccessor{TEntity}"/>：
/// 反引号引用、limit 行数上限），本类只给出落点展示名。
/// 表结构见 db/mysql/0006_create_order_bill.sql。
/// </remarks>
public sealed class MySqlOrderBillStore : OrderBillStoreBase
{
    /// <summary>构造存储。</summary>
    public MySqlOrderBillStore(
        IDataAccessor<OrderBillRow> accessor,
        IConfiguration configuration,
        ILogger<MySqlOrderBillStore> logger)
        : base(accessor, configuration, logger)
    {
    }

    /// <inheritdoc />
    protected override string DisplayName => "MySQL";
}
