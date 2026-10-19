using System.Collections.Concurrent;

namespace NetBpm.Workflow.Hosting;

/// <summary>
/// 监听器触发计数器（观测用单例）—— 断言「监听器真的被引擎调到了」的数据源。
/// </summary>
/// <remarks>
/// 宿主的监听器每次被引擎回调都到这里 +1；暴露一个只读端点返回快照即可做端到端断言。
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
