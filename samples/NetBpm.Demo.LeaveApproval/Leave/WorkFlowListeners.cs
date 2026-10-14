using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.Demo.LeaveApproval.Leave;

[ServiceTaskName("WorkFlowProcessListener")]
public sealed class WorkflowProcessListener : IExecutionListener
{
    private readonly ILogger<WorkflowProcessListener> _logger;
    private readonly ListenerStats _stats;

    /// <summary>构造监听器。</summary>
    public WorkflowProcessListener(ILogger<WorkflowProcessListener> logger, ListenerStats stats)
    {
        _logger = logger;
        _stats = stats;
    }

    /// <inheritdoc />
    public ValueTask NotifyAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        _stats.RecordExecution(execution.EventName);

        _logger.LogInformation(
            "执行监听器：event={Event}，activity={ActivityId}，instance={ProcessInstanceId}",
            execution.EventName,
            execution.CurrentActivityId,
            execution.ProcessInstanceId);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 流程级事件监听器 <c>WorkFlowTaskCompletedListener</c> —— 对齐 Java 端同名
/// <c>ActivitiEventListener</c>（订阅 <c>TASK_COMPLETED</c>）：任务每次完成都会通知到这里。
/// </summary>
/// <remarks>
/// 挂在流程级 <c>activiti:eventListener</c> 上（事件清单逗号分隔）；
/// <c>throwOnFailure</c> 对应 Java 端的 <c>isFailOnException()</c>。
/// Java 端在这里写 <c>deal_history</c> / 删 <c>instance_current_task</c>；
/// NetBpm 的请假 Demo 由应用层（<c>WorkflowService.SubmitAsync</c>）落这两张表，
/// 所以这里只做日志演示 —— 真实系统把它换成业务落库即可。
/// </remarks>
[ServiceTaskName("WorkFlowTaskCompletedListener")]
public sealed class WorkflowTaskCompletedListener : IEngineEventListener
{
    private readonly ILogger<WorkflowTaskCompletedListener> _logger;
    private readonly ListenerStats _stats;

    /// <summary>构造监听器。</summary>
    public WorkflowTaskCompletedListener(ILogger<WorkflowTaskCompletedListener> logger, ListenerStats stats)
    {
        _logger = logger;
        _stats = stats;
    }

    /// <inheritdoc />
    public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default)
    {
        _stats.RecordEngineEvent(engineEvent.Type);

        _logger.LogInformation(
            "事件监听器：event={EventType}，entity={EntityId}，instance={ProcessInstanceId}",
            engineEvent.Type,
            engineEvent.EntityId,
            engineEvent.Execution.ProcessInstanceId);

        return ValueTask.CompletedTask;
    }
}
