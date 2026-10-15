namespace NetBpm.Demo.Order;

/// <summary>
/// 订单业务数据的端口：提交申请写入（status=1）、审批结束回写（100 / -1）、列表与详情读取。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 它是<b>业务自己的表</b>（<c>order_bill</c>，见 db/mysql/0006）的落点，与流程引擎的业务数据端口
/// <c>IWorkflowDataStore</c>（instances / 审批结论 / 参数表…）是两回事。
/// MySQL 实现见 <c>NetBpm.Demo.Storage.MySqlOrderBillStore</c>。
/// </para>
/// </remarks>
public interface IOrderBillStore
{
    /// <summary>落点自述（会打在启动日志里，方便打开核对）。</summary>
    string Location { get; }

    /// <summary>新增一行订单（提交申请时，status 固定 1=审批中）。</summary>
    ValueTask<OrderBillRow> AddAsync(OrderBillRow bill, CancellationToken ct = default);

    /// <summary>按订单号查一行；查不到返回 <see langword="null"/>。</summary>
    ValueTask<OrderBillRow?> FindAsync(long businessId, CancellationToken ct = default);

    /// <summary>全部订单（新的在前）。</summary>
    ValueTask<IReadOnlyList<OrderBillRow>> ListAsync(CancellationToken ct = default);

    /// <summary>回填流程实例 id（发起流程成功后调用，用于按订单反查审批进度）。</summary>
    ValueTask<OrderBillRow?> SetProcessInstanceAsync(long businessId, string processInstanceId, CancellationToken ct = default);

    /// <summary>
    /// 回写订单状态：-1=审批拒绝 / 100=审批通过（审批结束的服务任务委托调用），
    /// 同时记下最终审批意见。订单不存在返回 <see langword="null"/>。
    /// </summary>
    ValueTask<OrderBillRow?> UpdateStatusAsync(long businessId, short status, string? approveComment, CancellationToken ct = default);
}
