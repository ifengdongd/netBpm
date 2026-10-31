namespace NetBpm.Bpmn.Model;

/// <summary>
/// BPMN 2.0 与 Activiti 扩展的常量。
/// 命名空间必须保留 <c>http://activiti.org/bpmn</c> —— 这是与 Activiti 定义文件互解析的技术前提（§13）。
/// </summary>
public static class BpmnConstants
{
    /// <summary>BPMN 2.0 命名空间。</summary>
    public const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";

    /// <summary>Activiti 扩展命名空间（技术上必需保留，见 §13）。</summary>
    public const string ActivitiNamespace = "http://activiti.org/bpmn";

    /// <summary>Activiti 扩展命名空间前缀。</summary>
    public const string ActivitiPrefix = "activiti";

    /// <summary>BPMNDI 命名空间。</summary>
    public const string BpmnDiNamespace = "http://www.omg.org/spec/BPMN/20100524/DI";

    /// <summary>OMG DC 命名空间。</summary>
    public const string OmgDcNamespace = "http://www.omg.org/spec/DD/20100524/DC";

    /// <summary>OMG DI 命名空间。</summary>
    public const string OmgDiNamespace = "http://www.omg.org/spec/DD/20100524/DI";

    /// <summary>XSI 命名空间。</summary>
    public const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Activiti 属性：办理人。</summary>
    public const string AttributeAssignee = "assignee";

    /// <summary>Activiti 属性：候选用户。</summary>
    public const string AttributeCandidateUsers = "candidateUsers";

    /// <summary>Activiti 属性：候选组。</summary>
    public const string AttributeCandidateGroups = "candidateGroups";

    /// <summary>Activiti 属性：表单 Key。</summary>
    public const string AttributeFormKey = "formKey";

    /// <summary>Activiti 属性：到期时间。</summary>
    public const string AttributeDueDate = "dueDate";

    /// <summary>Activiti 属性：优先级。</summary>
    public const string AttributePriority = "priority";

    /// <summary>Activiti 属性：实现类。</summary>
    public const string AttributeClass = "class";

    /// <summary>Activiti 属性：委托表达式。</summary>
    public const string AttributeDelegateExpression = "delegateExpression";

    /// <summary>Activiti 属性：值表达式。</summary>
    public const string AttributeExpression = "expression";

    /// <summary>Activiti 属性：结果变量名。</summary>
    public const string AttributeResultVariableName = "resultVariableName";

    /// <summary>
    /// Activiti 属性：外部任务主题（<c>activiti:topic</c>）。
    /// <para>标记了 topic 的 <c>serviceTask</c> 不在引擎进程内执行，而是生成一条外部任务等 worker 领取。</para>
    /// </summary>
    public const string AttributeTopic = "topic";

    /// <summary>Activiti 属性：跳过表达式。</summary>
    public const string AttributeSkipExpression = "skipExpression";

    /// <summary>Activiti 属性：多实例集合表达式（<c>multiInstanceLoopCharacteristics/@collection</c>）。</summary>
    public const string AttributeCollection = "collection";

    /// <summary>Activiti 属性：多实例元素变量名（<c>multiInstanceLoopCharacteristics/@elementVariable</c>）。</summary>
    public const string AttributeElementVariable = "elementVariable";

    /// <summary>Activiti 元素：执行监听器。</summary>
    public const string ElementExecutionListener = "executionListener";

    /// <summary>Activiti 元素：任务监听器。</summary>
    public const string ElementTaskListener = "taskListener";

    /// <summary>Activiti 元素：流程级事件监听器。</summary>
    public const string ElementEventListener = "eventListener";

    /// <summary>Activiti 元素：字段注入。</summary>
    public const string ElementField = "field";

    /// <summary>Activiti 元素：表单属性。</summary>
    public const string ElementFormProperty = "formProperty";

    /// <summary>Activiti 元素：表单属性枚举选项。</summary>
    public const string ElementFormPropertyValue = "value";

    /// <summary>Activiti 元素：潜在启动者。</summary>
    public const string ElementPotentialStarter = "potentialStarter";

    /// <summary>BPMN 元素：边界事件（<c>boundaryEvent</c>）。</summary>
    public const string ElementBoundaryEvent = "boundaryEvent";

    /// <summary>BPMN 元素：定时事件定义（<c>timerEventDefinition</c>）。</summary>
    public const string ElementTimerEventDefinition = "timerEventDefinition";

    /// <summary>BPMN 元素：终止事件定义（<c>terminateEventDefinition</c>）。</summary>
    public const string ElementTerminateEventDefinition = "terminateEventDefinition";

    /// <summary>BPMN 属性：边界事件挂靠的活动 id（<c>attachedToRef</c>）。</summary>
    public const string AttributeAttachedToRef = "attachedToRef";

    /// <summary>BPMN 属性：边界事件是否中断挂靠活动（<c>cancelActivity</c>）。</summary>
    public const string AttributeCancelActivity = "cancelActivity";

    /// <summary>BPMN 元素：定时日期（<c>timeDate</c>）。</summary>
    public const string ElementTimeDate = "timeDate";

    /// <summary>BPMN 元素：定时时长（<c>timeDuration</c>）。</summary>
    public const string ElementTimeDuration = "timeDuration";

    /// <summary>BPMN 元素：定时周期（<c>timeCycle</c>）。</summary>
    public const string ElementTimeCycle = "timeCycle";
}
