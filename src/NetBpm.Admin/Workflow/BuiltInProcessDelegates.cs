using Microsoft.Extensions.Logging;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.Admin.Workflow;

/// <summary>
/// 管理宿主的<b>内置占位委托</b>：让官方示例流程（请假 / 订单审批）在没有业务宿主的
/// 控制台里也能被解析并跑通，从而可端到端验证「发起 → 审批 → 流程图回显」。
/// </summary>
/// <remarks>
/// <para>
/// 示例 BPMN 里的 <c>activiti:delegateExpression</c> 本该由业务宿主实现
/// （见 samples/NetBpm.Demo：leave.ApproveComplete 写请假结论、order.ApproveComplete 写订单状态）。
/// 管理宿主不承载业务数据，这里只做「记录结论到流程变量 + 打日志」的等价占位，
/// 保证流程能推进，但<b>不会</b>写业务表。
/// </para>
/// <para>
/// 真实接入时：业务宿主注册自己的实现即可（同名的实现不要与管理宿主并存，
/// 一个宿主只应有一套实现；占位实现可用 <c>NetBpm:Admin:BuiltInDelegates:Enabled=false</c> 关闭）。
/// </para>
/// </remarks>
[ServiceTaskName("leave.ApproveComplete")]
[ServiceTaskName("leave.RefuseComplete")]
[ServiceTaskName("order.ApproveComplete")]
[ServiceTaskName("order.RefuseComplete")]
// 通用别名：示例请假流程的收口节点直接写 ${approveComplete} / ${refuseComplete}
// （不绑流程 key，接入方在自己的宿主里注册同名委托即可替换）。
[ServiceTaskName("approveComplete")]
[ServiceTaskName("refuseComplete")]
public sealed class BuiltInApprovalCompletionDelegate : IServiceTaskDelegate
{
    private readonly ILogger<BuiltInApprovalCompletionDelegate> _logger;

    /// <summary>构造占位委托。</summary>
    public BuiltInApprovalCompletionDelegate(ILogger<BuiltInApprovalCompletionDelegate> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        // 结论只落在流程变量里（供历史与流程图回显查看），业务落库由业务宿主负责。
        var approved = !execution.CurrentActivityId?.Contains("refuse", StringComparison.OrdinalIgnoreCase) ?? true;

        await execution.SetVariableAsync("approvalResult", approved ? "approved" : "refused", ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "内置占位委托：activity={ActivityId}，instance={ProcessInstanceId}，结论={Result}（业务落库需业务宿主实现）",
            execution.CurrentActivityId,
            execution.ProcessInstanceId,
            approved ? "approved" : "refused");
    }
}

/// <summary>
/// 流程级执行监听器占位实现（start / take / end）：只记日志，对应示例 BPMN 的
/// <c>${WorkFlowProcessListener}</c>。
/// </summary>
[ServiceTaskName("WorkFlowProcessListener")]
public sealed class BuiltInProcessListener : IExecutionListener
{
    private readonly ILogger<BuiltInProcessListener> _logger;

    /// <summary>构造监听器。</summary>
    public BuiltInProcessListener(ILogger<BuiltInProcessListener> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask NotifyAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "内置执行监听器：event={Event}，activity={ActivityId}，instance={ProcessInstanceId}",
            execution.EventName,
            execution.CurrentActivityId,
            execution.ProcessInstanceId);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 流程级事件监听器占位实现（<c>TASK_COMPLETED</c>）：只记日志，对应示例 BPMN 的
/// <c>${WorkFlowTaskCompletedListener}</c>。
/// </summary>
[ServiceTaskName("WorkFlowTaskCompletedListener")]
public sealed class BuiltInTaskCompletedListener : IEngineEventListener
{
    private readonly ILogger<BuiltInTaskCompletedListener> _logger;

    /// <summary>构造监听器。</summary>
    public BuiltInTaskCompletedListener(ILogger<BuiltInTaskCompletedListener> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "内置事件监听器：event={EventType}，entity={EntityId}，instance={ProcessInstanceId}",
            engineEvent.Type,
            engineEvent.EntityId,
            engineEvent.Execution.ProcessInstanceId);

        return ValueTask.CompletedTask;
    }
}
