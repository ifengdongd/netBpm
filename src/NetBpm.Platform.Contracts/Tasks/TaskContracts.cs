using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 待办任务视图——<b>调用方渲染一个审批页所需的全部信息</b>。
/// </summary>
/// <remarks>
/// <para>
/// 关键设计：<see cref="Options"/> 是<b>服务端算好的</b>可选项集，不是让调用方去读
/// <c>activiti:formProperty</c> 或 <c>activiti:value</c>。这样流程定义怎么改（选项 id 从 <c>1</c>
/// 变成 <c>APPROVE</c>），调用方渲染按钮的代码都不用动。
/// </para>
/// <para>
/// <see cref="Form"/> 是<b>提交时的表单快照</b>对应的字段定义：历史详情页按当时的字段渲染，
/// 而不是按当前最新定义渲染——否则旧单据会显示不存在的字段。
/// </para>
/// </remarks>
/// <param name="TaskId">任务 id。</param>
/// <param name="InstanceId">所属实例 id。</param>
/// <param name="ActivityId">环节 id（BPMN userTask 的 id）。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="Assignee">当前办理人；未认领时为 <see langword="null"/>。</param>
/// <param name="CandidateUsers">候选用户。</param>
/// <param name="CandidateGroups">候选组。</param>
/// <param name="CreatedAt">任务创建时间。</param>
/// <param name="DueAt">要求完成时间（SLA）；无则为 <see langword="null"/>。</param>
/// <param name="Options">可选项（含语义与校验规则）。</param>
/// <param name="Form">表单字段定义（可空：该环节不需要填表单）。</param>
/// <param name="CurrentUserCanSubmit">当前主体是否有权办理（服务端已算好，调用方不必自己判断）。</param>
/// <param name="ProcessKey">所属流程 key（老流程没有定义记录时为空）。</param>
/// <param name="ProcessDefinitionId">所属流程定义 id。</param>
/// <param name="DefinitionVersion">
/// 所属定义的<b>版本</b>。表单快照要把它一起冻住——否则旧单据没法证明
/// 「当时是按第几版的定义办的」（§4.5：定义会升级，实例不会跟着升）。
/// </param>
public sealed record TaskView(
    string TaskId,
    string InstanceId,
    string? ActivityId,
    string? ActivityName,
    string? Assignee,
    IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string> CandidateGroups,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DueAt,
    IReadOnlyList<ApprovalOption> Options,
    IReadOnlyList<FormField>? Form,
    bool CurrentUserCanSubmit,
    string? ProcessKey = null,
    string? ProcessDefinitionId = null,
    int? DefinitionVersion = null);

/// <summary>
/// 待办摘要（待办箱列表用）。
/// </summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="InstanceId">实例 id。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessName">流程名称。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Title">单据标题。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="DueAt">要求完成时间。</param>
/// <param name="StartedBy">发起人。</param>
/// <param name="Summary">摘要行（服务端按流程模板渲染的关键字段，如「张三 请假 3 天」）。</param>
/// <param name="EntryUrl">办理页地址（由流程目录的 <c>pageUrls.approve</c> 模板填充 taskId 得到）。</param>
/// <param name="CompletedAt">办结时间（仅「我的已办」有值；待办恒为 <see langword="null"/>）。</param>
/// <param name="Decision">
/// 这条任务<b>当时记下的审批决定</b>（选项 / 语义 / 意见 / 时间）。只有走过平台
/// 提交端点、并被写时定稿进时间线的任务才有值——引擎不认识审批意见，
/// 「我批了什么」这个答案<b>不可能</b>从引擎表里读出来。
/// </param>
public sealed record TaskSummary(
    string TaskId,
    string InstanceId,
    string ProcessKey,
    string ProcessName,
    string? BusinessKey,
    string? Title,
    string? ActivityName,
    string? Assignee,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? DueAt,
    string? StartedBy,
    string? Summary,
    string? EntryUrl,
    DateTimeOffset? CompletedAt = null,
    Decision? Decision = null);

/// <summary>
/// 一次审批决定（「我批了什么」的标准答案）。
/// </summary>
/// <remarks>
/// 三个值一起给出而不是只给语义：<c>OptionId</c> 是流程定义里那个按钮的原样回显
/// （同一流程的不同版本可能改过 id），<c>Semantic</c> 是跨流程可比对的结论，
/// <c>Comment</c> 是人对人说话的地方。少任何一个，历史页都得回头猜。
/// </remarks>
/// <param name="OptionId">审批选项 id（如 <c>1</c> / <c>APPROVE</c>）。</param>
/// <param name="Semantic">审批语义（未映射时为 <see langword="null"/>）。</param>
/// <param name="Comment">审批意见。</param>
/// <param name="DecidedAt">决定时间（同一时间线上的发生时间）。</param>
public sealed record Decision(
    string? OptionId,
    ApprovalSemantic? Semantic,
    string? Comment,
    DateTimeOffset? DecidedAt);

/// <summary>
/// 待办列表查询条件（对应 <c>GET /workflow/v1/tasks</c>）。
/// </summary>
/// <remarks>
/// <see cref="Scope"/> 决定「查谁的待办」：默认只查当前主体的；查全员的待办需要
/// <see cref="PlatformScopes.HistoryReadAll"/> 或管理类 scope（服务端校验，不靠客户端自觉）。
/// </remarks>
/// <param name="Scope">待办范围。</param>
/// <param name="ProcessKey">按流程过滤。</param>
/// <param name="ActivityId">按环节过滤。</param>
/// <param name="Keyword">按标题 / 业务主键模糊匹配。</param>
/// <param name="OverdueOnly">只看已超期的。</param>
/// <param name="Cursor">分页游标。</param>
/// <param name="Limit">本页条数。</param>
public sealed record TaskQuery(
    TaskScope Scope = TaskScope.Mine,
    string? ProcessKey = null,
    string? ActivityId = null,
    string? Keyword = null,
    bool? OverdueOnly = null,
    string? Cursor = null,
    int? Limit = null);

/// <summary>待办查询范围。</summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<TaskScope>))]
public enum TaskScope
{
    /// <summary>我的待办（含候选组命中、未认领的）。</summary>
    Mine = 0,

    /// <summary>我已认领的。</summary>
    Claimed = 1,

    /// <summary>我的已办（<c>ACT_HI_TASKINST</c> 中已结束且办理人是我的任务）。</summary>
    /// <remarks>
    /// 「待办」与「已办」是两个不同的数据源：待办在 <c>ACT_RU_TASK</c>（任务完成即删行），
    /// 已办只能在历史表里查——同一份业务数据要让调用方握住两把互不相识的钥匙是不能接受的，
    /// 所以共用同一个待办端点与同一个 <see cref="TaskSummary"/> 形状，只用
    /// <see cref="TaskSummary.CompletedAt"/> 是否有值区分；「批了什么」走
    /// <see cref="TaskSummary.Decision"/>（来自写时定稿的时间线）。
    /// </remarks>
    Done = 3,

    /// <summary>全员的（需管理权限）。</summary>
    All = 2,
}

/// <summary>
/// 提交审批请求。
/// </summary>
/// <remarks>
/// 结构与现有 <c>SubmitRequest</c>（<c>result</c> + <c>comment</c> + <c>paraDict</c>）兼容但语义更严格：
/// <list type="bullet">
///   <item><description><c>optionId</c> 替代裸 <c>result</c>，并可选带 <c>semantic</c> 做一致性校验；</description></item>
///   <item><description><c>parameters</c> 走 schema 校验（必填 / 类型 / 可写性），不再静默展开；</description></item>
///   <item><description><c>expectedRevision</c> 提供乐观并发，挡住「两个人同时点提交」的重复推进。</description></item>
/// </list>
/// </remarks>
/// <param name="OptionId">选项 id。</param>
/// <param name="Semantic">调用方理解的语义（可选，用于一致性校验）。</param>
/// <param name="Comment">审批意见。</param>
/// <param name="TargetNodeId">目标节点（退回类必填）。</param>
/// <param name="Parameters">本次提交写入的参数（键 = <see cref="ParameterDefinition.ApiName"/>）。</param>
/// <param name="FormValues">表单值快照（键 = 字段 id，值 = 原始字符串），仅存档不参与流程判断。</param>
/// <param name="ExpectedRevision">期望的实例修订号。</param>
/// <param name="Attachments">附件引用（只存引用，不存二进制）。</param>
public sealed record SubmitTaskRequest(
    string OptionId,
    ApprovalSemantic? Semantic = null,
    string? Comment = null,
    string? TargetNodeId = null,
    IReadOnlyDictionary<string, string?>? Parameters = null,
    IReadOnlyDictionary<string, string?>? FormValues = null,
    long? ExpectedRevision = null,
    IReadOnlyList<AttachmentRef>? Attachments = null);

/// <summary>
/// 附件引用。
/// </summary>
/// <remarks>
/// 平台<b>不做附件存储</b>：本体留在调用方的对象存储里，这里只记 URL 与元数据，
/// 让审批记录能证明「当时提交了哪些材料」。<see cref="Sha256"/> 可选，用于事后校验文件未被替换。
/// </remarks>
/// <param name="Name">文件名。</param>
/// <param name="Url">可下载地址（建议带时效签名）。</param>
/// <param name="Size">字节数。</param>
/// <param name="ContentType">MIME 类型。</param>
/// <param name="Sha256">内容摘要（十六进制），可选。</param>
public sealed record AttachmentRef(
    string Name,
    string Url,
    long? Size = null,
    string? ContentType = null,
    string? Sha256 = null);

/// <summary>
/// 提交审批的结果——<b>一次往返把「推进之后的样子」全带回去</b>。
/// </summary>
/// <remarks>
/// 为了省掉调用方的第二次查询：提交完立刻知道实例到哪了、下一步该谁办、以及本次产生了哪些时间线条目。
/// <see cref="Events"/> 是本次提交产生的事件类型（供调用方做本地通知），不是订阅投递。
/// </remarks>
/// <param name="Instance">推进后的实例视图。</param>
/// <param name="NextTasks">推进后新产生的待办（可能为空：流程已结束）。</param>
/// <param name="TimelineDelta">本次提交追加到时间线的条目。</param>
/// <param name="Events">本次产生的事件类型（如 <c>task.completed</c>、<c>instance.completed</c>）。</param>
public sealed record SubmitTaskResult(
    InstanceView Instance,
    IReadOnlyList<TaskView> NextTasks,
    IReadOnlyList<TimelineEntry> TimelineDelta,
    IReadOnlyList<string> Events);

/// <summary>
/// 认领请求。
/// </summary>
/// <param name="ExpectedAssignee">期望的当前办理人；用于「抢单」场景的乐观并发。</param>
public sealed record ClaimTaskRequest(string? ExpectedAssignee = null);

/// <summary>
/// 转办请求。
/// </summary>
/// <param name="Assignee">新的办理人（<c>subject</c> id）。</param>
/// <param name="Comment">转办说明。</param>
public sealed record TransferTaskRequest(string Assignee, string? Comment = null);
