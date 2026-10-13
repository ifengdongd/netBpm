using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 服务任务 <c>leave.ApproveComplete</c>：流程「审批通过」后处理业务数据。
/// </summary>
/// <remarks>
/// <para>
/// 名字由 <see cref="ServiceTaskNameAttribute"/> 声明，与 BPMN 里
/// <c>activiti:delegateExpression="leave.ApproveComplete"</c> 对应；
/// 组合根只需 <c>AddServiceTaskDelegates</c> 扫一遍程序集，不必逐个手写 keyed 注册。
/// </para>
/// <para>
/// 落盘那套（读变量 → 还原 BaseModel → 取参数快照 → 写结论）在
/// <see cref="IWorkflowService.CompleteAsync"/> 里，所以这里只剩「结论是什么」一句话 ——
/// 另一条流程的委托也是同样的写法。
/// </para>
/// </remarks>
[ServiceTaskName("leave.ApproveComplete")]
public sealed class ApproveCompleteDelegate : IServiceTaskDelegate
{
    private readonly IWorkflowService _workflow;

    /// <summary>构造委托。</summary>
    /// <param name="workflow">应用层门面。</param>
    public ApproveCompleteDelegate(IWorkflowService workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        _workflow = workflow;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        _workflow.CompleteAsync(execution, WorkflowOutcomes.Approved, ct);
}

/// <summary>
/// 服务任务 <c>leave.RefuseComplete</c>：流程「拒绝结束」后处理业务数据。
/// </summary>
/// <remarks>
/// 名字由 <see cref="ServiceTaskNameAttribute"/> 声明，与 BPMN 里
/// <c>activiti:delegateExpression="leave.RefuseComplete"</c> 对应。
/// </remarks>
[ServiceTaskName("leave.RefuseComplete")]
public sealed class RefuseCompleteDelegate : IServiceTaskDelegate
{
    private readonly IWorkflowService _workflow;

    /// <summary>构造委托。</summary>
    /// <param name="workflow">应用层门面。</param>
    public RefuseCompleteDelegate(IWorkflowService workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        _workflow = workflow;
    }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        _workflow.CompleteAsync(execution, WorkflowOutcomes.Refused, ct);
}
