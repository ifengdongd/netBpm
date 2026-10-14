using System.Globalization;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Order;

/// <summary>
/// 订单流程的「审批结束」服务任务委托基类 —— 流程走到
/// <c>order.ApproveComplete</c> / <c>order.RefuseComplete</c> 时，
/// 把对应的订单状态写回 <c>order_bill.status</c>，再让应用层落审批结论。
/// </summary>
/// <remarks>
/// <para>
/// 派生类只剩「委托名 + 结论 + 订单状态」一行（见下方两个实现）；类上的
/// <see cref="ServiceTaskNameAttribute"/> 与 BPMN 里的
/// <c>activiti:delegateExpression</c> 对应，组合根 <c>AddServiceTaskDelegates</c>
/// 扫一遍程序集完成注册。
/// </para>
/// <para>
/// 顺序：先回写订单状态（业务事实），再 <see cref="IWorkflowService.CompleteAsync"/>
/// （审批结论留痕）—— 后者失败时订单状态已经是终态，重试不会双写。
/// </para>
/// </remarks>
/// <param name="workflow">应用层门面（负责把结论落进业务数据）。</param>
/// <param name="store">订单业务数据端口（回写 <c>order_bill.status</c>）。</param>
/// <param name="outcome">结论机器名（<see cref="WorkflowOutcomes.Approved"/> / <see cref="WorkflowOutcomes.Refused"/>）。</param>
/// <param name="status">与结论对应的订单状态（<see cref="OrderBillStatus.Approved"/> / <see cref="OrderBillStatus.Refused"/>）。</param>
public abstract class OrderCompletionDelegate(
    IWorkflowService workflow,
    IOrderBillStore store,
    string outcome,
    short status) : IServiceTaskDelegate
{
    private readonly IWorkflowService _workflow = workflow;
    private readonly IOrderBillStore _store = store;

    /// <summary>本委托写入的结论机器名。</summary>
    protected string Outcome { get; } = outcome;

    /// <summary>本委托回写的订单状态。</summary>
    protected short Status { get; } = status;

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        // 订单号在发起时写进了流程变量 businessId（BaseModel 字段），从这里取
        var variables = await execution.GetVariablesAsync(ct).ConfigureAwait(false);
        var businessKey = variables.TryGetValue(BaseModelVariables.BusinessId, out var value)
            ? value?.ToString()
            : null;

        if (!OrderBusinessKey.IsValid(businessKey))
        {
            throw new InvalidOperationException(
                $"服务任务 '{execution.CurrentActivityId}' 找不到合法的订单号（流程变量 businessId='{businessKey}'），无法回写订单状态。");
        }

        var businessId = long.Parse(businessKey!, CultureInfo.InvariantCulture);

        // 最终审批意见来自审批表单的 comment 字段（以最后一次审批的提交为准）
        var comment = variables.TryGetValue(BaseModelVariables.Comment, out var commentValue)
            ? commentValue?.ToString()
            : null;

        var bill = await _store.UpdateStatusAsync(businessId, Status, comment, ct).ConfigureAwait(false);

        if (bill is null)
        {
            throw new InvalidOperationException(
                $"订单 {businessId} 不存在（order_bill 里没有这一行），无法回写状态 {Status}。");
        }

        // 审批结论落盘（WorkflowOutcomeRecord），委托返回后引擎继续推进到结束事件
        await _workflow.CompleteAsync(execution, Outcome, ct).ConfigureAwait(false);
    }
}

/// <summary>审批通过（<c>order.ApproveComplete</c>）：订单状态 → 100。</summary>
[ServiceTaskName("order.ApproveComplete")]
public sealed class ApproveCompleteDelegate(IWorkflowService workflow, IOrderBillStore store)
    : OrderCompletionDelegate(workflow, store, WorkflowOutcomes.Approved, OrderBillStatus.Approved);

/// <summary>审批拒绝（<c>order.RefuseComplete</c>）：订单状态 → -1。</summary>
[ServiceTaskName("order.RefuseComplete")]
public sealed class RefuseCompleteDelegate(IWorkflowService workflow, IOrderBillStore store)
    : OrderCompletionDelegate(workflow, store, WorkflowOutcomes.Refused, OrderBillStatus.Refused);
