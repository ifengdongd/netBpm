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

    /// <summary>
    /// 发起人变量名（<c>activiti:initiator</c>）。
    /// <para>
    /// 实例启动时，引擎把<b>发起人（认证用户）</b>写进这个名字的流程变量 —— 这是 Activiti 的标准用法，
    /// 后续节点可以用 <c>${initiator}</c> 引用它（例如 <c>humanPerformer</c> 的办理人表达式）。
    /// 认证用户为 null 时变量仍会被创建（null 类型），与 Activiti 一致。
    /// </para>
    /// </summary>
    public string? Initiator { get; set; }
}

/// <summary>
/// 结束事件（<c>endEvent</c>，空事件定义）。
/// 对应 Activiti 的 <c>EndEvent</c> + <c>NoneEndEventActivityBehavior</c>。
/// </summary>
public class EndEvent : Event;
