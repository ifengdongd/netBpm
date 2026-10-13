namespace NetBpm.Bpmn.Model;

/// <summary>活动基类。对应 Activiti 的 <c>Activity</c>。</summary>
public abstract class Activity : FlowNode
{
    /// <summary>默认出流 id（对应 XML 属性 <c>default</c>）。</summary>
    public string? DefaultFlow { get; set; }

    /// <summary>是否为补偿活动（本子集恒为 false）。</summary>
    public bool ForCompensation { get; set; }
}

/// <summary>任务基类。对应 Activiti 的 <c>Task</c>。</summary>
public abstract class Task : Activity;

/// <summary>
/// 用户任务（<c>userTask</c>）。
/// 属性对齐 Activiti 的 <c>UserTask</c>，办理逻辑见 <c>UserTaskActivityBehavior</c>（P7）。
/// </summary>
public class UserTask : Task
{
    /// <summary>办理人（<c>activiti:assignee</c>）。</summary>
    public string? Assignee { get; set; }

    /// <summary>
    /// 任务所有者（<c>activiti:owner</c>）。
    /// <para>Activiti 的 <c>handleAssignments</c> 会处理它，故保留（对应 <c>OWNER_</c> 列）。</para>
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>候选用户（<c>activiti:candidateUsers</c>，逗号分隔）。</summary>
    public IList<string> CandidateUsers { get; } = [];

    /// <summary>候选组（<c>activiti:candidateGroups</c>，逗号分隔）。</summary>
    public IList<string> CandidateGroups { get; } = [];

    /// <summary>表单 Key（<c>activiti:formKey</c>）—— 只透传，不渲染（§1.3 第 10 条）。</summary>
    public string? FormKey { get; set; }

    /// <summary>到期时间（<c>activiti:dueDate</c>），表达式或字面量。</summary>
    public string? DueDate { get; set; }

    /// <summary>优先级（<c>activiti:priority</c>），表达式或字面量。</summary>
    public string? Priority { get; set; }

    /// <summary>任务分类（<c>activiti:category</c>）。</summary>
    public string? Category { get; set; }

    /// <summary>跳过表达式（<c>activiti:skipExpression</c>）。</summary>
    public string? SkipExpression { get; set; }

    /// <summary>任务监听器（<c>activiti:taskListener</c>，可选能力）。</summary>
    public IList<TaskListener> TaskListeners { get; } = [];

    /// <summary>
    /// 表单属性（<c>activiti:formProperty</c>）—— 只透传，不执行（同 <see cref="FormKey"/>）。
    /// <para>模型必须承载它，否则解析时被静默丢弃、落库重序列化后永久丢失。</para>
    /// </summary>
    public IList<FormProperty> FormProperties { get; } = [];
}

/// <summary>服务任务实现方式（对应 <c>activiti:class</c> / <c>delegateExpression</c> / <c>expression</c>）。</summary>
public enum ServiceTaskImplementationType
{
    /// <summary>未指定（校验阶段报错）。</summary>
    None = 0,

    /// <summary>实现类全名。</summary>
    Class = 1,

    /// <summary>委托表达式。</summary>
    DelegateExpression = 2,

    /// <summary>值表达式。</summary>
    Expression = 3,
}

/// <summary>
/// 服务任务（<c>serviceTask</c>）。
/// 三种写法统一路由到 <c>ServiceTaskDelegateBehavior</c>（见 §1.2）。
/// </summary>
public class ServiceTask : Task
{
    /// <summary>实现方式。</summary>
    public ServiceTaskImplementationType ImplementationType { get; set; } = ServiceTaskImplementationType.None;

    /// <summary>实现类全名（<c>activiti:class</c>）。</summary>
    public string? ClassName { get; set; }

    /// <summary>委托表达式（<c>activiti:delegateExpression</c>）。</summary>
    public string? DelegateExpression { get; set; }

    /// <summary>值表达式（<c>activiti:expression</c>）。</summary>
    public string? Expression { get; set; }

    /// <summary>结果变量名（<c>activiti:resultVariableName</c>）。</summary>
    public string? ResultVariableName { get; set; }

    /// <summary>字段注入（<c>activiti:field</c>）。</summary>
    public IList<FieldExtension> Fields { get; } = [];
}

/// <summary>手工任务（<c>manualTask</c>）。仅记录历史，自动通过。</summary>
public class ManualTask : Task;

/// <summary>
/// 脚本任务（<c>scriptTask</c>）。使用 Roslyn Scripting 实现，按计划为二期（§1.2）。
/// </summary>
public class ScriptTask : Task
{
    /// <summary>脚本格式（<c>scriptFormat</c>）。</summary>
    public string? ScriptFormat { get; set; }

    /// <summary>脚本内容（<c>script</c>）。</summary>
    public string? Script { get; set; }

    /// <summary>是否自动存储变量（<c>activiti:autoStoreVariables</c>）。</summary>
    public bool AutoStoreVariables { get; set; }

    /// <summary>结果变量名（<c>activiti:resultVariable</c>）。</summary>
    public string? ResultVariable { get; set; }
}
