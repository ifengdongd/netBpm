using System.Globalization;
using NetBpm.Demo.Order;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Delegates;

/// <summary>
/// 订单流程的「结束收口」委托 —— 派生 <see cref="ApprovalCompletionDelegate"/>，
/// 钩子里把结论翻译成 <c>order_bill.status</c> 回写（100=通过 / -1=拒绝）。
/// </summary>
[ServiceTaskName("order.ApproveComplete")]
public sealed class OrderApproveCompleteDelegate(IWorkflowService workflow, IOrderBillStore store)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Approved)
{
    /// <summary>回写订单状态 = 100（审批通过）。</summary>
    protected override async ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
    {
        var businessId = long.Parse(businessKey, CultureInfo.InvariantCulture);
        var bill = await store.UpdateStatusAsync(businessId, OrderBillStatus.Approved, comment, ct).ConfigureAwait(false);

        if (bill is null)
        {
            throw new InvalidOperationException($"订单 {businessId} 不存在（order_bill 里没有这一行），无法回写状态 {OrderBillStatus.Approved}。");
        }
    }
}

[ServiceTaskName("order.RefuseComplete")]
public sealed class OrderRefuseCompleteDelegate(IWorkflowService workflow, IOrderBillStore store)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Refused)
{
    /// <summary>回写订单状态 = -1（审批拒绝）。</summary>
    protected override async ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
    {
        var businessId = long.Parse(businessKey, CultureInfo.InvariantCulture);
        var bill = await store.UpdateStatusAsync(businessId, OrderBillStatus.Refused, comment, ct).ConfigureAwait(false);

        if (bill is null)
        {
            throw new InvalidOperationException($"订单 {businessId} 不存在（order_bill 里没有这一行），无法回写状态 {OrderBillStatus.Refused}。");
        }
    }
}
