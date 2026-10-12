namespace NetBpm.Bpmn.Model;

/// <summary>事件基类。本子集只支持「空开始事件」与「空结束事件」（见 §1.3 排除定时/消息/信号等）。</summary>
#pragma warning disable CA1716 // 类名与 Activiti 的 Event 对齐（§1.1 L2：同元素名、同属性名）
public abstract class Event : FlowNode;
#pragma warning restore CA1716

/// <summary>
/// 开始事件（<c>startEvent</c>，空事件定义）。
/// 对应 Activiti 的 <c>StartEvent</c> + <c>NoneStartEventActivityBehavior</c>。
/// </summary>
public class StartEvent : Event
{
    /// <summary>是否有事件定义（子集内恒为 false）。</summary>
    public bool HasEventDefinition { get; set; }

    /// <summary>是否有多个事件定义。</summary>
    public bool HasMultipleEventDefinitions { get; set; }
}

/// <summary>
/// 结束事件（<c>endEvent</c>，空事件定义）。
/// 对应 Activiti 的 <c>EndEvent</c> + <c>NoneEndEventActivityBehavior</c>。
/// </summary>
public class EndEvent : Event;
