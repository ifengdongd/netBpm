using Microsoft.Extensions.Logging;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Leave;

/// <summary>
/// 流程级执行监听器 <c>WorkFlowProcessListener</c> —— 与 BPMN 里的
/// <c>activiti:executionListener activiti:delegateExpression="${WorkFlowProcessListener}"</c> 对应。
/// </summary>
/// <remarks>
/// 请假流程的 BPMN（<c>leave-process.bpmn20.xml</c>）在 start / take / end 上都挂了它；
/// 这里记日志与触发计数（<see cref="ListenerStats"/>），真实系统换成业务落库即可。
/// </remarks>
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
/// 流程级事件监听器 <c>WorkFlowTaskCompletedListener</c> —— 订阅 <c>TASK_COMPLETED</c>：
/// 任务每次完成都会通知到这里。真实系统在这里写审批历史 / 删当前待办；
/// NetBpm 由应用层（<c>WorkflowService.SubmitAsync</c>）落盘，这里只做日志演示。
/// </summary>
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
