using NetBpm.Engine.Execution;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 所有「可连接」元素（任务 / 网关 / 事件）的行为基类。
/// 对应 Activiti 的 <c>FlowNodeActivityBehavior</c>。
/// </summary>
public abstract class FlowNodeActivityBehavior : IActivityBehavior
{
    protected FlowNodeActivityBehavior(IEngineServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        BpmnActivityBehavior = new BpmnActivityBehavior(services);
    }

    /// <summary>引擎服务聚合。</summary>
    protected IEngineServices Services { get; }

    /// <summary>默认离开行为集合。</summary>
    protected BpmnActivityBehavior BpmnActivityBehavior { get; }

    /// <summary>默认行为：不做额外处理，直接离开。</summary>
    public virtual ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        LeaveAsync(execution, ct);

    /// <summary>默认离开方式：校验出流条件并取条件为真的流。</summary>
    public virtual ValueTask LeaveAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        BpmnActivityBehavior.PerformDefaultOutgoingBehaviorAsync(execution, ct);

    /// <summary>忽略条件的离开方式。</summary>
    public virtual ValueTask LeaveIgnoreConditionsAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        BpmnActivityBehavior.PerformIgnoreConditionsOutgoingBehaviorAsync(execution, ct);

    /// <inheritdoc />
    public virtual ValueTask TriggerAsync(
        IDelegateExecution execution,
        string? signalName,
        object? signalData,
        CancellationToken ct = default) =>
        throw new EngineException("this activity isn't waiting for a trigger");
}
