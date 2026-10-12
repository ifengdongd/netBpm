using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 活动行为。对应 Activiti 的 <c>ActivityBehavior</c> / <c>TriggerableActivityBehavior</c>。
/// <para>本子集共 8 个具体行为 + 3 个基类（Activiti 为 58 个，见 §1.2）。</para>
/// </summary>
public interface IActivityBehavior
{
    /// <summary>进入该节点时执行。默认语义为「直接离开」。</summary>
    ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default);

    /// <summary>被外部信号触发（等待型节点使用，如用户任务被完成）。</summary>
    ValueTask TriggerAsync(
        IDelegateExecution execution,
        string? signalName,
        object? signalData,
        CancellationToken ct = default);
}
