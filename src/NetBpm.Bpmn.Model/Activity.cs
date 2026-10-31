namespace NetBpm.Bpmn.Model;

/// <summary>活动基类。对应 Activiti 的 <c>Activity</c>。</summary>
public abstract class Activity : FlowNode
{
    /// <summary>默认出流 id（对应 XML 属性 <c>default</c>）。</summary>
    public string? DefaultFlow { get; set; }

    /// <summary>
    /// 进入活动前先落一条异步作业（<c>activiti:asyncBefore</c>）：命令提交后由 JobExecutor 执行。
    /// </summary>
    public bool AsyncBefore { get; set; }

    /// <summary>
    /// 活动执行完成后不立即离开，而是落一条异步作业（<c>activiti:asyncAfter</c>）。
    /// </summary>
    public bool AsyncAfter { get; set; }

    /// <summary>
    /// 是否独占作业（<c>activiti:exclusive</c>，默认 true）。
    /// <para>本子集只支持独占：并发作业（exclusive=false）会让同一实例的多条令牌并行推进，</para>
    /// <para>与当前「一个命令一棵执行树」的并发模型冲突，由 <c>AsyncJobRule</c> 部署期拒绝。</para>
    /// </summary>
    public bool Exclusive { get; set; } = true;

    /// <summary>是否为补偿活动（本子集恒为 false）。</summary>
    public bool ForCompensation { get; set; }

    /// <summary>
    /// 多实例特性（<c>multiInstanceLoopCharacteristics</c>，会签 / 串签）。
    /// <para>
    /// 本子集只支持 <see cref="UserTask"/> 上的多实例（B1.2）；其它活动带多实例
    /// 会被 <c>MultiInstanceRule</c> 在部署期拒绝 —— 解析进模型是为了「配置了但不支持」
    /// 不被静默丢弃，从而能被发现。
    /// </para>
    /// </summary>
    public MultiInstanceLoopCharacteristics? LoopCharacteristics { get; set; }
}

/// <summary>
/// 多实例特性（<c>multiInstanceLoopCharacteristics</c>）。对应 Activiti 的 <c>MultiInstanceLoopCharacteristics</c>。
/// </summary>
public class MultiInstanceLoopCharacteristics
{
    /// <summary>是否串行（XML 属性 <c>isSequential</c>，缺省 false = 并行会签）。</summary>
    public bool IsSequential { get; set; }

    /// <summary>实例数（<c>&lt;loopCardinality&gt;</c> 文本，表达式或字面量）。</summary>
    public string? LoopCardinality { get; set; }

    /// <summary>集合表达式（<c>activiti:collection</c>，求值结果须为集合；集合元素数 = 实例数）。</summary>
    public string? Collection { get; set; }

    /// <summary>元素变量名（<c>activiti:elementVariable</c>，每个实例把集合元素写入该变量）。</summary>
    public string? ElementVariable { get; set; }

    /// <summary>完成条件（<c>&lt;completionCondition&gt;</c> 文本，如 ${nrOfCompletedInstances == 2}）。</summary>
    public string? CompletionCondition { get; set; }
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

/// <summary>
/// 服务任务实现方式（对应 <c>activiti:class</c> / <c>delegateExpression</c> / <c>expression</c> /
/// <c>topic</c>）。
/// <para>
/// 前三种是<b>嵌入式宿主</b>语义（引擎进程内解析并执行委托）；<see cref="ExternalTopic"/> 是
/// <b>独立部署</b>语义（引擎只登记一条外部任务，由 worker 拉走执行后回执）。
/// </para>
/// </summary>
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

    /// <summary>
    /// 外部任务主题（<c>activiti:topic</c>）：不在引擎进程内执行，等 worker 领取。
    /// </summary>
    ExternalTopic = 4,
}

/// <summary>
/// 服务任务（<c>serviceTask</c>）。
/// 三种进程内写法统一路由到 <c>ServiceTaskDelegateBehavior</c>（见 §1.2）；
/// <c>activiti:topic</c> 写法路由到外部任务行为（引擎只登记、等 worker 回执）。
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

    /// <summary>
    /// 外部任务主题（<c>activiti:topic</c>）。
    /// <para>非空表示该节点交给外部 worker 执行：引擎登记一条外部任务后原地等待，不解析任何委托。</para>
    /// </summary>
    public string? Topic { get; set; }

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
