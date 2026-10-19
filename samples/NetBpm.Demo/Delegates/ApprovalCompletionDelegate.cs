using NetBpm.Demo.Workflow;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.Delegates;

/// <summary>
/// <b>审批结束委托基类</b>（两条流程共用）—— 把委托的公共部分收干净：
/// 读流程变量取 <c>businessId</c> / <c>comment</c> → 业务回写钩子 → 结论落盘。
/// </summary>
/// <remarks>
/// <para>
/// 业务流程只需派生并实现 <see cref="OnApprovalCompletedAsync"/>（把结论翻译成业务变更，
/// 无回写需求就空实现），再标 <see cref="ServiceTaskNameAttribute"/>（与 BPMN 的
/// <c>delegateExpression</c> 对应）—— 组合根 <c>AddServiceTaskDelegates</c> 扫描注册。
/// </para>
/// <para>
/// 顺序：先业务回写（业务事实），再 <see cref="IWorkflowService.CompleteAsync"/>
/// （审批结论留痕）—— 后者失败时业务已是终态，重试不会双写。
/// </para>
/// </remarks>
public abstract class ApprovalCompletionDelegate : IServiceTaskDelegate
{
    private readonly IWorkflowService _workflow;

    /// <summary>构造委托。</summary>
    /// <param name="workflow">应用层门面（负责把结论落进业务数据）。</param>
    /// <param name="outcome">结论机器名（Approved / Refused）。</param>
    protected ApprovalCompletionDelegate(IWorkflowService workflow, string outcome)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        _workflow = workflow;
        Outcome = outcome;
    }

    /// <summary>本委托写入的结论机器名。</summary>
    protected string Outcome { get; }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        var variables = await execution.GetVariablesAsync(ct).ConfigureAwait(false);

        var businessKey = variables.TryGetValue(BaseModelVariables.BusinessId, out var business)
            ? business?.ToString()
            : null;

        if (!BusinessKeys.IsValid(businessKey))
        {
            throw new InvalidOperationException(
                $"服务任务 '{execution.CurrentActivityId}' 找不到合法的业务单号（流程变量 businessId='{businessKey}'），无法执行业务回写。");
        }

        var comment = variables.TryGetValue(BaseModelVariables.Comment, out var commentValue)
            ? commentValue?.ToString()
            : null;

        await OnApprovalCompletedAsync(businessKey!, comment, ct).ConfigureAwait(false);

        await _workflow.CompleteAsync(execution, Outcome, ct).ConfigureAwait(false);
    }

    /// <summary>业务回写钩子：把本结论翻译成业务数据变更（无回写需求就空实现）。</summary>
    /// <param name="businessKey">业务单号（流程变量 businessId，已通过校验）。</param>
    /// <param name="comment">最终审批意见（审批表单的 comment，以最后一次提交为准）。</param>
    protected abstract ValueTask OnApprovalCompletedAsync(string businessKey, string? comment, CancellationToken ct);
}
