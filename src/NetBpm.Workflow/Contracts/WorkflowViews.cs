using System.Text.Json.Serialization;

namespace NetBpm.Workflow;

/// <summary>表单属性的枚举选项视图（对应 BPMN 里的 <c>activiti:value</c>）。</summary>
/// <param name="Id">选项值（提交时回传的就是它，也是流程变量的取值形态）。</param>
/// <param name="Name">选项显示名。</param>
public sealed record WorkflowFormOption(string Id, string? Name);

/// <summary>
/// 表单属性视图 —— <b>审批选项就是从 <c>activiti:formProperty</c> 里读出来的</b>，前端不写死任何选项。
/// </summary>
/// <param name="Id">属性 id（表单字段名，如 <c>approvalResult</c>）。</param>
/// <param name="Name">显示名。</param>
/// <param name="Type">类型（string / long / double / boolean / date / enum）。</param>
/// <param name="Required">是否必填。</param>
/// <param name="Variable">绑定到哪个流程变量；为空表示与 <paramref name="Id"/> 同名。</param>
/// <param name="Default">默认值。</param>
/// <param name="Options">enum 类型的候选选项。</param>
public sealed record WorkflowFormProperty(
    string Id,
    string? Name,
    string? Type,
    bool Required,
    string? Variable,
    string? Default,
    IReadOnlyList<WorkflowFormOption> Options);

/// <summary>待办任务视图（含表单定义）。</summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="Name">任务名。</param>
/// <param name="DefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="ProcessInstanceId">所属流程实例 id。</param>
/// <param name="FormKey">表单标识。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="CandidateUsers">候选用户。</param>
/// <param name="CandidateGroups">候选组。</param>
/// <param name="CreateTime">任务创建时间。</param>
/// <param name="Form">表单属性（含审批选项）。</param>
public sealed record WorkflowTaskView(
    string TaskId,
    string? Name,
    string? DefinitionKey,
    string? ProcessInstanceId,
    string? FormKey,
    string? Assignee,
    IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string> CandidateGroups,
    DateTime? CreateTime,
    IReadOnlyList<WorkflowFormProperty> Form);

/// <summary>流程环节的轻量引用（节点 id + 节点名）。</summary>
/// <param name="ActivityId">BPMN 元素 id（对用户任务而言 = <c>taskDefinitionKey</c>）。</param>
/// <param name="ActivityName">节点名。</param>
public sealed record WorkflowActivityRef(string ActivityId, string? ActivityName);

/// <summary>
/// 流程实例视图 —— <b>一次请求拿全</b>：变量 + 当前待办（含表单契约）+ 审批结论 + 流程参数。
/// </summary>
/// <remarks>
/// 这是「审批过程中获取流程环节信息」的返回体：前端拿它就能渲染出
/// 「现在停在哪个节点、这个节点要填什么、之前填进去的值是什么」，不必再发第二个请求。
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Running">是否仍在运行（结束后运行态行会被删除，只剩业务数据）。</param>
/// <param name="Variables">流程变量（扁平，键 = 变量名）。</param>
/// <param name="Tasks">当前待办。</param>
/// <param name="Result">审批结论（业务数据）；未结束为 <see langword="null"/>。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="Base">
/// 业务基础模型（7 个字段）。与 <paramref name="Variables"/> 的区别：这里是<b>按模型解读过</b>的视图，
/// 已经做过类型归一（<c>result</c> 从字符串还原成枚举、<c>applyTime</c> 还原成时间）。
/// </param>
/// <param name="Parameters">流程参数表内容（键 = 参数名，如 <c>days</c> / <c>reason</c>）。</param>
public sealed record WorkflowInstanceView(
    string ProcessInstanceId,
    string? BusinessKey,
    string ProcessDefinitionId,
    bool Running,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<WorkflowTaskView> Tasks,
    WorkflowOutcomeRecord? Result,
    DateTime? StartedAt,
    BaseModel Base,
    IReadOnlyDictionary<string, string?> Parameters)
{
    /// <summary>当前停留的环节 id 列表（按定义 id 升序，便于断言与展示）。</summary>
    public IReadOnlyList<string> CurrentActivityIds =>
        [.. Tasks.Where(task => task.DefinitionKey is not null)
            .Select(task => task.DefinitionKey!)
            .OrderBy(key => key, StringComparer.Ordinal)];
}

/// <summary>流程实例摘要（列表用）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Base">业务基础模型。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="Running">是否仍在运行（<see cref="Result"/> 为 <see langword="null"/> 即运行中）。</param>
/// <param name="Result">审批结论；未结束为 <see langword="null"/>。</param>
/// <param name="Parameters">流程参数表内容。</param>
public sealed record WorkflowInstanceSummary(
    string ProcessInstanceId,
    BaseModel Base,
    DateTime? StartedAt,
    bool Running,
    WorkflowOutcomeRecord? Result,
    IReadOnlyDictionary<string, string?> Parameters)
{
    /// <summary>列表展示用的状态字样：已结束取结论名，未结束固定 <see cref="WorkflowOutcomes.Running"/>。</summary>
    public string Status => Result?.Outcome ?? WorkflowOutcomes.Running;
}

/// <summary>审批历史里「环节序列」的来源。</summary>
/// <remarks>
/// 走 JSON 时序列化成<b>名字</b>（<c>"businessApprovals"</c> / <c>"engineHistory"</c>）而不是序号 ——
/// 这个字段是给人排查用的，<c>0</c> 与 <c>1</c> 没人记得住。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowStepSource>))]
public enum WorkflowStepSource
{
    /// <summary>
    /// 由<b>业务侧审批记录 + 当前待办</b>合成。
    /// <para>
    /// 内存宿主没有 <c>ACT_HI_*</c>（历史写入必须数据库宿主且显式开启，见 <c>NetBpmOptions.EnableHistory</c>），
    /// 但「谁在哪个环节提交过什么」由宿主在每次提交时记下来了，合成出来的时间线一样可用。
    /// </para>
    /// </summary>
    BusinessApprovals,

    /// <summary>来自引擎历史表 <c>ACT_HI_ACTINST</c>（权威、含自动节点与连线）。</summary>
    EngineHistory,
}

/// <summary>
/// 审批历史里的一个环节。
/// </summary>
/// <remarks>
/// 同时承载两种来源的数据，字段语义保持一致：<see cref="Decision"/> / <see cref="DecisionName"/> /
/// <see cref="Comment"/> 在两种来源下都来自业务侧的审批记录（引擎不认识它们）。
/// </remarks>
/// <param name="ActivityId">节点 id。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="ActivityType">节点类型（引擎历史才有；合成时为 <c>userTask</c> 或空）。</param>
/// <param name="TaskId">用户任务 id（自动节点为 <see langword="null"/>）。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="Decision">审批结果选项 id（如 <c>"1"</c>）；未提交或非审批环节为 <see langword="null"/>。</param>
/// <param name="DecisionName">审批结果中文名（如 <c>通过</c>）。</param>
/// <param name="Comment">审批说明。</param>
/// <param name="StartedAt">进入该环节的时间（合成来源可能为 <see langword="null"/>）。</param>
/// <param name="EndedAt">离开该环节的时间；为空表示正停在这里。</param>
public sealed record WorkflowStepRecord(
    string ActivityId,
    string? ActivityName,
    string? ActivityType,
    string? TaskId,
    string? Assignee,
    string? Decision,
    string? DecisionName,
    string? Comment,
    DateTime? StartedAt,
    DateTime? EndedAt)
{
    /// <summary>是否正停在该环节（<see cref="EndedAt"/> 为空）—— 流程环节着色的判据。</summary>
    public bool IsActive => EndedAt is null;
}

/// <summary>
/// 审批历史记录 —— 「这个单子走过哪些环节、谁在什么时候批了什么」。
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Running">是否仍在运行。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="EndedAt">结束时间（未结束为 <see langword="null"/>）。</param>
/// <param name="DurationInMilliseconds">历时（毫秒）。</param>
/// <param name="StepsSource"><see cref="Steps"/> 的来源，见 <see cref="WorkflowStepSource"/>。</param>
/// <param name="Steps">环节序列（按时间先后；最后可能有一个仍在进行的环节）。</param>
/// <param name="Approvals">审批记录（每次提交一条，含提交人与表单值快照）。</param>
/// <param name="Result">审批结论（未结束为 <see langword="null"/>）。</param>
public sealed record WorkflowHistoryView(
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string? BusinessKey,
    bool Running,
    DateTime? StartedAt,
    DateTime? EndedAt,
    long? DurationInMilliseconds,
    WorkflowStepSource StepsSource,
    IReadOnlyList<WorkflowStepRecord> Steps,
    IReadOnlyList<WorkflowApprovalRecord> Approvals,
    WorkflowOutcomeRecord? Result);
