using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 监听器触发计数器（观测用单例）—— e2e 断言「监听器真的被引擎调到了」的数据源。
/// </summary>
/// <remarks>
/// 下面的两个监听器每次被引擎回调都到这里 +1；<c>GET /api/leave/listener-stats</c> 返回快照。
/// 生产系统不需要它 —— 真实监听器做的是业务落库，不是计数。
/// </remarks>
public sealed class ListenerStats
{
    /// <summary>固定键集：未触发过的键也返回 0，方便断言做差值。</summary>
    private static readonly string[] KnownKeys =
    [
        "executionStart", "executionTake", "executionEnd",
        "taskCreated", "taskAssignment", "taskCompleted",
        "activityStarted", "activityCompleted",
        "processStarted", "processCompleted",
    ];

    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);

    /// <summary>执行监听器回调 +1（按 <c>execution.EventName</c> 归类）。</summary>
    public void RecordExecution(string? eventName) => Record(ExecutionKey(eventName));

    /// <summary>流程级事件监听器回调 +1（按事件类型归类）。</summary>
    public void RecordEngineEvent(string? eventType) => Record(EventKey(eventType));

    /// <summary>取快照（全部键、含 0）。</summary>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var key in KnownKeys)
        {
            snapshot[key] = _counters.GetValueOrDefault(key);
        }

        return snapshot;
    }

    private void Record(string key) => _counters.AddOrUpdate(key, 1, (_, current) => current + 1);

    private static string ExecutionKey(string? eventName) => eventName switch
    {
        "start" => "executionStart",
        "take" => "executionTake",
        "end" => "executionEnd",
        _ => "executionOther",
    };

    private static string EventKey(string? eventType) => eventType switch
    {
        "TASK_CREATED" => "taskCreated",
        "TASK_COMPLETED" => "taskCompleted",
        "ACTIVITY_STARTED" => "activityStarted",
        "ACTIVITY_COMPLETED" => "activityCompleted",
        "PROCESS_STARTED" => "processStarted",
        "PROCESS_COMPLETED" => "processCompleted",
        _ => "eventOther",
    };
}

/// <summary>
/// 执行监听器 <c>WorkFlowProcessListener</c> —— 对齐 Java 端同名
/// <c>ExecutionListener</c>：start / take / end 三个事件都会通知到这里，
/// 通过 <see cref="IDelegateExecution.EventName"/> 区分。
/// </summary>
/// <remarks>
/// 名字由 <see cref="ServiceTaskNameAttribute"/> 声明，与 BPMN 里
/// <c>activiti:delegateExpression="${WorkFlowProcessListener}"</c> 对应；
/// 组合根只需 <c>AddServiceTaskDelegates</c> 扫一遍程序集（监听器实现与
/// 服务任务委托走同一条注册通道）。Demo 里只做日志演示 —— 真实系统在这里挂
/// 「流程结束后回写业务状态」之类的钩子（Java 端的 end 分支）。
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
