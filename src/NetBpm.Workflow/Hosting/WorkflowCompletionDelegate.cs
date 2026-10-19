using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;

namespace NetBpm.Workflow.Hosting;

/// <summary>
/// 「流程结束收口」服务任务委托的<b>基类</b>：宿主的每个结束环节只需声明结论名，
/// 落盘那套（读变量 → 还原业务上下文 → 取参数快照 → 写结论）在
/// <see cref="IWorkflowService.CompleteAsync"/> 里。
/// </summary>
/// <remarks>
/// <para>
/// 用法：派生一个类，构造时把结论机器名交给基类，类上标 <c>[ServiceTaskName("流程.环节名")]</c>
/// 与 BPMN 里的 <c>activiti:delegateExpression</c> 对应，组合根 <c>AddServiceTaskDelegates</c>
/// 扫一遍程序集即可：
/// </para>
/// <code>
/// [ServiceTaskName("expense.ApproveComplete")]
/// public sealed class ExpenseApproveComplete(IWorkflowService workflow)
///     : WorkflowCompletionDelegate(workflow, WorkflowOutcomes.Approved);
/// </code>
/// <para>
//! 结论是<b>业务语义</b>（取值见 <see cref="WorkflowOutcomes"/>）：同一个流程可以有多个结束服务任务
/// （如「审批通过结束」与「拒绝结束」各挂一个），分别声明不同结论。
/// </para>
/// </remarks>
public abstract class WorkflowCompletionDelegate : IServiceTaskDelegate
{
    private readonly IWorkflowService _workflow;

    /// <summary>构造委托。</summary>
    /// <param name="workflow">应用层门面（负责把结论落进业务数据）。</param>
    /// <param name="outcome">结论机器名（<see cref="WorkflowOutcomes"/> 的取值或宿主自定义）。</param>
    protected WorkflowCompletionDelegate(IWorkflowService workflow, string outcome)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        _workflow = workflow;
        Outcome = outcome;
    }

    /// <summary>本委托写入的结论机器名。</summary>
    protected string Outcome { get; }

    /// <inheritdoc />
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        _workflow.CompleteAsync(execution, Outcome, ct);
}
