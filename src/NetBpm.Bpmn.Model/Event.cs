namespace NetBpm.Bpmn.Model;

/// <summary>
/// 事件基类。
/// <para>
/// 本子集支持：空开始事件、空结束事件、<b>终止结束事件</b>（<c>terminateEventDefinition</c>）、
/// <b>定时边界事件</b>（<c>boundaryEvent</c> + <c>timerEventDefinition</c>）。
/// 其它事件定义仍由 <c>UnsupportedEventDefinitionRule</c> 在部署期显式拒绝。
/// </para>
/// </summary>
#pragma warning disable CA1716 // 类名与 Activiti 的 Event 对齐（§1.1 L2：同元素名、同属性名）
public abstract class Event : FlowNode
{
    /// <summary>
    /// 声明了的事件定义（XML 子元素本地名，如 <c>timerEventDefinition</c>）。
    /// <para>
    /// <b>必须把「配置了事件定义」这件事记下来</b>，否则解析时整棵忽略，
    /// 定时/消息流程会「部署成功但语义丢失」。记录后由校验层逐类裁定支持与否。
    /// </para>
    /// </summary>
    public IList<string> EventDefinitions { get; } = [];

    /// <summary>是否有事件定义。</summary>
    public bool HasEventDefinition { get; set; }

    /// <summary>是否有多个事件定义。</summary>
    public bool HasMultipleEventDefinitions { get; set; }

    /// <summary>
    /// 定时器定义（仅 <c>timerEventDefinition</c> 有值）。
    /// <para>三者（timeDate / timeDuration / timeCycle）只会有一个非空。</para>
    /// </summary>
    public TimerEventDefinition? Timer { get; set; }
}
#pragma warning restore CA1716

/// <summary>
/// 定时事件定义（<c>timerEventDefinition</c>）的内容。
/// <para>
/// 只支持一次性定时：<c>timeDuration</c>（ISO-8601 时长，如 <c>PT2H</c>）与
/// <c>timeDate</c>（ISO-8601 时间点）。<c>timeCycle</c>（重复/cron）部署期拒绝。
/// 值可以写成表达式（<c>${timeout}</c>），在创建定时器时求值。
/// </para>
/// </summary>
public sealed class TimerEventDefinition
{
    /// <summary><c>&lt;timeDate&gt;</c> 文本（ISO-8601 时间点或表达式）。</summary>
    public string? TimeDate { get; set; }

    /// <summary><c>&lt;timeDuration&gt;</c> 文本（ISO-8601 时长或表达式）。</summary>
    public string? TimeDuration { get; set; }

    /// <summary><c>&lt;timeCycle&gt;</c> 文本（本子集不支持，仅记录以便拒绝）。</summary>
    public string? TimeCycle { get; set; }

    /// <summary>是否已配置任一取值。</summary>
    public bool HasValue =>
        !string.IsNullOrWhiteSpace(TimeDate)
        || !string.IsNullOrWhiteSpace(TimeDuration)
        || !string.IsNullOrWhiteSpace(TimeCycle);
}

/// <summary>
/// 边界事件（<c>boundaryEvent</c>）。
/// <para>
/// 只支持挂在<b>用户任务</b>上的中断型定时边界事件：到点后取消任务、沿边界事件的出流继续。
/// 非中断型（<c>cancelActivity="false"</c>）与其它事件定义部署期拒绝。
/// </para>
/// </summary>
public class BoundaryEvent : Event
{
    /// <summary>挂靠的活动 id（<c>attachedToRef</c>）。</summary>
    public string? AttachedToRef { get; set; }

    /// <summary>挂靠的活动（解析后回填；解析不到时由校验规则报错）。</summary>
    public Activity? AttachedTo { get; set; }

    /// <summary>是否中断挂靠活动（<c>cancelActivity</c>，缺省 true）。</summary>
    public bool CancelActivity { get; set; } = true;
}

/// <summary>
/// 开始事件（<c>startEvent</c>，空事件定义）。
/// 对应 Activiti 的 <c>StartEvent</c> + <c>NoneStartEventActivityBehavior</c>。
/// </summary>
public class StartEvent : Event
{
    /// <summary>
    /// 发起人变量名（<c>activiti:initiator</c>）。
    /// <para>
    /// 实例启动时，引擎把<b>发起人（认证用户）</b>写进这个名字的流程变量 —— 这是 Activiti 的标准用法，
    /// 后续节点可以用 <c>${initiator}</c> 引用它（例如 <c>humanPerformer</c> 的办理人表达式）。
    /// 认证用户为 null 时变量仍会被创建（null 类型），与 Activiti 一致。
    /// </para>
    /// </summary>
    public string? Initiator { get; set; }

    /// <summary>
    /// 起始表单 key（<c>activiti:formKey</c>）。
    /// <para>
    /// 有没有这一列决定 <c>ACT_RE_PROCDEF.HAS_START_FORM_KEY_</c> 写 1 还是 0 ——
    /// Activiti 用这个标记告诉调用方「发起这个流程要先填一张表单」。
    /// 表单内容本身不在子集内（不渲染、不校验），但<b>这个标记要如实落库</b>：
    /// 对拍里 7 条语料就差在这一位上。
    /// </para>
    /// </summary>
    public string? FormKey { get; set; }
}

/// <summary>
/// 结束事件（<c>endEvent</c>）。
/// <para>
/// 空结束事件对应 Activiti 的 <c>NoneEndEventActivityBehavior</c>；
/// 带 <c>terminateEventDefinition</c> 时对应 <c>TerminateEndEventActivityBehavior</c>，
/// 语义为<b>终止整个流程实例</b>（本子集无子流程，不存在「只终止当前作用域」的分支）。
/// </para>
/// </summary>
public class EndEvent : Event
{
    /// <summary>是否为终止结束事件（含 <c>terminateEventDefinition</c>）。</summary>
    public bool IsTerminate =>
        EventDefinitions.Contains(BpmnConstants.ElementTerminateEventDefinition, StringComparer.Ordinal);
}
