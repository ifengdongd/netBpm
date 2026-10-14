using NetBpm.Bpmn.Model;
using NetBpm.El;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 暴露给「行为类 / 监听器 / 表达式」的执行视图。对应 Activiti 的 <c>DelegateExecution</c>。
/// <para>
/// 三个布尔标志位是流程正确性的根基（对应 DDL 中的 <c>IS_ACTIVE_</c> / <c>IS_CONCURRENT_</c> /
/// <c>IS_SCOPE_</c>），语义要点见开发计划 §5.1 与风险 R3。
/// </para>
/// </summary>
public interface IDelegateExecution : IVariableScope
{
    /// <summary>执行 id。流程实例根执行的 id 即流程实例 id。</summary>
    string Id { get; }

    /// <summary>流程实例 id。</summary>
    string? ProcessInstanceId { get; }

    /// <summary>流程定义 id。</summary>
    string? ProcessDefinitionId { get; }

    /// <summary>父执行 id；流程实例根执行为 <see langword="null"/>。</summary>
    string? ParentId { get; }

    /// <summary>当前活动 id（可写：推进流程时会改变）。</summary>
    string? CurrentActivityId { get; set; }

    /// <summary>当前流程元素，可为 <c>SequenceFlow</c> 或 <c>FlowNode</c>。</summary>
    IFlowElement? CurrentFlowElement { get; set; }

    /// <summary>是否处于「可推进」状态。</summary>
    bool IsActive { get; }

    /// <summary>是否是分支产生的子执行。</summary>
    bool IsConcurrent { get; }

    /// <summary>是否是作用域（能容纳子执行）。</summary>
    bool IsScope { get; }

    /// <summary>是否已结束。</summary>
    bool IsEnded { get; }

    /// <summary>是否属于流程实例类型（根执行）。</summary>
    bool IsProcessInstanceType { get; }

    /// <summary>把执行置为非活动。并行网关 Join 前会调用（见 §5.4 之二）。</summary>
    void Inactivate();

    /// <summary>把执行置为活动。</summary>
    void Activate();

    /// <summary>变量作用域（与自身等价，供表达式管理器使用）。</summary>
    IVariableScope Variables { get; }

    /// <summary>
    /// 本次监听器回调的事件名（<c>start</c> / <c>take</c> / <c>end</c> 等）。
    /// 仅在监听器回调期间有值 —— 对应 Activiti 的 <c>DelegateExecution.getEventName()</c>。
    /// </summary>
    string? EventName { get; set; }
}
