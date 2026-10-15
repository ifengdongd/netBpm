using NetBpm.Runtime;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Delegates;

/// <summary>
/// 请假流程的「结束收口」委托 —— 派生 <see cref="ApprovalCompletionDelegate"/>，
/// 请假无业务状态回写（审批结论由 CompleteAsync 统一落盘），钩子为空实现。
/// </summary>
[ServiceTaskName("leave.ApproveComplete")]
public sealed class LeaveApproveCompleteDelegate(IWorkflowService workflow)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Approved)
{
    /// <inheritdoc />
    protected override ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
        => ValueTask.CompletedTask;
}

[ServiceTaskName("leave.RefuseComplete")]
public sealed class LeaveRefuseCompleteDelegate(IWorkflowService workflow)
    : ApprovalCompletionDelegate(workflow, WorkflowOutcomes.Refused)
{
    /// <inheritdoc />
    protected override ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct)
        => ValueTask.CompletedTask;
}
