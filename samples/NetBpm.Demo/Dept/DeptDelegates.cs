using NetBpm.Demo.Delegates;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Dept;

/// <summary>
/// 部门审批流程的「结束收口」委托 —— 派生 <c>ApprovalCompletionDelegate</c>，
/// 部门审批无业务状态回写（审批结论由 CompleteAsync 统一落盘），钩子为空实现。
/// </summary>
/// <remarks>
/// 委托名与 BPMN 里的 <c>activiti:delegateExpression</c> 一一对应（<c>dept.ApproveComplete</c> /
/// <c>dept.RefuseComplete</c>），组合根 <c>AddServiceTaskDelegates</c> 扫程序集登记。
/// </remarks>
[ServiceTaskName("dept.ApproveComplete")]
public sealed class DeptApproveCompleteDelegate(IWorkflowService workflow)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Approved)
{
    /// <inheritdoc />
    protected override ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
        => ValueTask.CompletedTask;
}

[ServiceTaskName("dept.RefuseComplete")]
public sealed class DeptRefuseCompleteDelegate(IWorkflowService workflow)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Refused)
{
    /// <inheritdoc />
    protected override ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
        => ValueTask.CompletedTask;
}
