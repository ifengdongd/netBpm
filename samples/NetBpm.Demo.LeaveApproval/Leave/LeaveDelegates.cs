using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 请假流程的两个「结束收口」委托 —— 派生公共基类 <see cref="WorkflowCompletionDelegate"/>，
/// 每个只剩「类名 + 结论」两行；落盘逻辑全部在 <see cref="IWorkflowService.CompleteAsync"/>。
/// </summary>
/// <remarks>
/// 名字由 <see cref="NetBpm.Engine.Behavior.ServiceTaskNameAttribute"/> 声明，与 BPMN 里
/// <c>activiti:delegateExpression="leave.ApproveComplete"</c> / <c>leave.RefuseComplete</c> 对应；
/// 组合根只需 <c>AddServiceTaskDelegates</c> 扫一遍程序集。
/// </remarks>
[ServiceTaskName("leave.ApproveComplete")]
public sealed class ApproveCompleteDelegate(IWorkflowService workflow)
    : WorkflowCompletionDelegate(workflow, WorkflowOutcomes.Approved);

[ServiceTaskName("leave.RefuseComplete")]
public sealed class RefuseCompleteDelegate(IWorkflowService workflow)
    : WorkflowCompletionDelegate(workflow, WorkflowOutcomes.Refused);
