using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 实例状态（5 态，替换现有 <c>Running: bool</c> 的二值表达）。
/// </summary>
/// <remarks>
/// 现状只有「运行中 / 已结束」，而「已终止」「已挂起」「异常挂起」三种情况无法区分，
/// 调用方只能靠猜。平台化后必须是显式枚举，且迁移规则写进契约（新增状态只能追加，不复用旧值）。
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<InstanceStatus>))]
public enum InstanceStatus
{
    /// <summary>运行中：有活动待办。</summary>
    Running = 0,

    /// <summary>正常结束（走到结束事件）。</summary>
    Completed = 1,

    /// <summary>被终止（有人主动撤销 / 管理员干预）。</summary>
    Terminated = 2,

    /// <summary>被挂起：无人可办理，需恢复。</summary>
    Suspended = 3,

    /// <summary>异常：引擎内部错误挂起（如委托解析失败、表达式求值失败），需人工处理。</summary>
    Failed = 4,
}

/// <summary>
/// 实例结论的<b>推导来源</b>——用于审计分辨「这是流程自己说的」还是「平台猜的」。
/// </summary>
/// <remarks>
/// 独立部署后没有宿主委托来声明结论（现状是 <c>IWorkflowService.CompleteAsync(execution, WorkflowOutcomes.Approved)</c>），
/// 平台只能三级回落推导：流程显式声明 → 最后一条审批决定的语义 → 兜底文案。
/// 带上来历，审计才能区分可信度。
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ConclusionSource>))]
public enum ConclusionSource
{
    /// <summary>流程定义 / 服务任务显式声明（最可信）。</summary>
    Declared = 0,

    /// <summary>由最后一条审批决定的语义推导。</summary>
    LastDecision = 1,

    /// <summary>兜底（只有起止时间，没有任何决定）。</summary>
    Fallback = 2,
}

/// <summary>
/// 实例结论。
/// </summary>
/// <param name="Semantic">语义（与审批选项共用同一套稳定字典）。</param>
/// <param name="OptionId">导致该结论的选项 id（未知时为 <see langword="null"/>）。</param>
/// <param name="Label">结论展示文案（如「已通过」「已驳回」）。</param>
/// <param name="Source">推导来源。</param>
public sealed record InstanceConclusion(
    ApprovalSemantic Semantic,
    string? OptionId,
    string Label,
    ConclusionSource Source);

/// <summary>
/// 流程实例视图——<b>一次请求拿全</b>：状态、当前环节、待办、参数、环节聚合、结论。
/// </summary>
/// <remarks>
/// <para>
/// 这是外部系统最常用的一个响应体：拿到它就能渲染「这个单子现在停在哪、还能做什么、之前批了什么」。
/// 设计上有两条硬约束：
/// </para>
/// <list type="number">
///   <item><description><b>强类型</b>：不再允许宿主用 decorator 改形状（现状 <c>DecorateInstanceView</c> 返回 <c>object?</c>，导致无法生成 SDK）。</description></item>
///   <item><description><b>不含引擎原语</b>：没有 <c>ACT_*</c> 字段、没有裸 execution / task entity，只有审批业务需要的形状。</description></item>
/// </list>
/// </remarks>
/// <param name="InstanceId">实例 id。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="ProcessDefinitionId">定义 id。</param>
/// <param name="DefinitionVersion">定义版本。</param>
/// <param name="BusinessKey">业务主键（调用方自己的单据号）。</param>
/// <param name="Title">单据标题（服务端按流程的 <c>summaryTemplate</c> 渲染）。</param>
/// <param name="Status">实例状态。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="StartedBy">发起人（<c>subject</c>）。</param>
/// <param name="EndedAt">结束时间。</param>
/// <param name="CurrentActivityIds">当前停留的环节 id（可能多个：并行分支）。</param>
/// <param name="Revision">实例修订号（每次推进 +1，用于乐观并发）。</param>
/// <param name="Tasks">当前待办（含可选项）。</param>
/// <param name="Parameters">参数快照。</param>
/// <param name="NodeOutcomes">已办结环节的聚合结果（会签口径）。</param>
/// <param name="Conclusion">实例结论；运行中为 <see langword="null"/>。</param>
public sealed record InstanceView(
    string InstanceId,
    string ProcessKey,
    string ProcessDefinitionId,
    int DefinitionVersion,
    string? BusinessKey,
    string? Title,
    InstanceStatus Status,
    DateTimeOffset? StartedAt,
    string? StartedBy,
    DateTimeOffset? EndedAt,
    IReadOnlyList<string> CurrentActivityIds,
    long Revision,
    IReadOnlyList<TaskView> Tasks,
    ParameterSnapshot Parameters,
    IReadOnlyList<NodeOutcome> NodeOutcomes,
    InstanceConclusion? Conclusion);

/// <summary>
/// 实例摘要（列表用，不含待办与聚合细节）。
/// </summary>
/// <param name="InstanceId">实例 id。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Title">单据标题。</param>
/// <param name="Status">状态。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="StartedBy">发起人。</param>
/// <param name="EndedAt">结束时间。</param>
/// <param name="CurrentActivityIds">当前环节 id。</param>
/// <param name="CurrentAssignees">当前待办的办理人（去重，用于「待我处理」的列表展示）。</param>
/// <param name="Conclusion">结论。</param>
/// <param name="MyRoles">
/// 当前主体与该实例的<b>参与关系</b>（我发起 / 我审批过 / 我是候选人）；
/// 只有显式查「参与过的单据」时才带（<see cref="InstanceQuery.InvolvedUser"/>），其余场景为空。
/// </param>
public sealed record InstanceSummary(
    string InstanceId,
    string ProcessKey,
    string? BusinessKey,
    string? Title,
    InstanceStatus Status,
    DateTimeOffset? StartedAt,
    string? StartedBy,
    DateTimeOffset? EndedAt,
    IReadOnlyList<string> CurrentActivityIds,
    IReadOnlyList<string> CurrentAssignees,
    InstanceConclusion? Conclusion,
    IReadOnlyList<ParticipationRole>? MyRoles = null);

/// <summary>
/// 一个人与一条单据的参与关系（回答「这条单据在我的哪个列表里」）。
/// </summary>
/// <remarks>
/// <para>
/// 「我发起的」和「我审批过的」在界面上是两个不同的列表，但它们判定差之千里：
/// 前者看 <c>ACT_HI_PROCINST.START_USER_ID_</c>，后者看历史身份关系与历史任务的办理人。
/// 把它们合成一个<b>服务端算好的</b>枚举，调用方就不必自己去翻字段猜。
/// </para>
/// <para>
/// 关系是<b>并集</b>：一个人可以既是发起人又是审批人（自己发起自己批的流程很常见）。
/// </para>
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ParticipationRole>))]
public enum ParticipationRole
{
    /// <summary>我发起的。</summary>
    Starter = 0,

    /// <summary>我审批过的（历史任务的办理人）。</summary>
    Approver = 1,

    /// <summary>我是（或曾是）候选办理人，含候选组命中。</summary>
    Candidate = 2,

    /// <summary>流程实例级参与者（办理人 / 所有者 / 被 InvolveUser 拉进来的人）。</summary>
    Participant = 3,
}

/// <summary>
/// 实例列表查询条件（对应 <c>GET /workflow/v1/instances</c>）。
/// </summary>
/// <param name="ProcessKey">按流程过滤。</param>
/// <param name="BusinessKey">按业务主键精确过滤。</param>
/// <param name="Status">按状态过滤。</param>
/// <param name="StartedBy">按发起人过滤。</param>
/// <param name="InvolvedUser">
/// 按参与人过滤（我发起 / 我审批过 / 我是候选人的单据都算）——<b>不能</b>用
/// <see cref="StartedBy"/> 代替：审批人与发起人通常不是同一个人。
/// 服务端决定可用范围：无跨人权限时只能是自己（详见 <c>InstanceListCatalog</c>）。
/// </param>
/// <param name="StartedFrom">发起时间下限（含）。</param>
/// <param name="StartedTo">发起时间上限（含）。</param>
/// <param name="Cursor">分页游标。</param>
/// <param name="Limit">本页条数。</param>
/// <param name="Direction">时间排序方向，默认倒序。</param>
public sealed record InstanceQuery(
    string? ProcessKey = null,
    string? BusinessKey = null,
    InstanceStatus? Status = null,
    string? StartedBy = null,
    string? InvolvedUser = null,
    DateTimeOffset? StartedFrom = null,
    DateTimeOffset? StartedTo = null,
    string? Cursor = null,
    int? Limit = null,
    SortDirection Direction = SortDirection.Descending);

/// <summary>
/// 发起流程请求。
/// </summary>
/// <remarks>
/// <b>没有</b> <c>startedBy</c> 字段：发起人只来自令牌的 <c>sub</c>。
/// 一旦允许请求体声明发起人，调用方就能替任意人发起单据，数据授权形同虚设
/// （现有实现里 <c>startUserId</c> 与令牌不一致才拒绝，属于事后校验；新契约从结构上就不给这个口子）。
/// </remarks>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="EntryPointKey">入口标识；为空表示默认入口。</param>
/// <param name="BusinessKey">业务主键；为空则服务端按流程配置自动发号。</param>
/// <param name="Title">单据标题；为空则服务端按模板渲染。</param>
/// <param name="Parameters">参数（键 = <see cref="ParameterDefinition.ApiName"/>）。</param>
public sealed record StartInstanceRequest(
    string ProcessKey,
    string? EntryPointKey = null,
    string? BusinessKey = null,
    string? Title = null,
    IReadOnlyDictionary<string, string?>? Parameters = null);

/// <summary>
/// 发起流程的结果。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Instance"/> 复用 <see cref="InstanceView"/>（实例读模型的单一出口），
/// 不另拼一份发起后的最小视图——保证「发起返回的实例」与「随后 GET /instances/{id}」长得一样。
/// </para>
/// <para>
/// <see cref="Instance"/> 只在一种情况下为 <see langword="null"/>：流程发起后立即走完
/// （无待办、无中间等待）且历史被禁用——此时运行态行已被删、历史查不到，实例视图组装不出来。
/// 但 <see cref="InstanceId"/> 与 <see cref="BusinessKey"/> 仍然可靠返回，调用方不丢主键。
/// </para>
/// </remarks>
/// <param name="InstanceId">实例 id（= 根执行 id）。</param>
/// <param name="ProcessKey">流程 key。</param>
/// <param name="BusinessKey">业务主键（调用方传入或服务端发号）。</param>
/// <param name="Instance">发起后的完整实例视图；见 <see cref="InstanceView"/>。</param>
public sealed record StartInstanceResult(
    string InstanceId,
    string ProcessKey,
    string? BusinessKey,
    InstanceView? Instance);

/// <summary>
/// 平台约定的流程变量名（不属于业务模型，由平台 / 宿主写入）。
/// </summary>
/// <remarks>
/// 与 <c>BaseModelVariables</c>（业务字段）分开：这些变量的语义由平台定义，
/// 业务方不应读写，否则会与平台的推导逻辑打架。
/// </remarks>
public static class PlatformVariables
{
    /// <summary>
    /// 流程显式声明的结论：值可以是 <see cref="ApprovalSemantic"/> 名称（<c>APPROVE</c> / <c>REJECT</c> …）
    /// 或选项 id。
    /// </summary>
    /// <remarks>
    /// 这是 §4.7 结论三级回落的<b>第一级</b>的运行时落点。BPMN 侧对应的写法是
    /// <c>&lt;netbpm:outcome semantic="APPROVE"/&gt;</c>——解析器支持扩展元素后由引擎在流程结束时
    /// 写入同名变量，读侧（实例读模型）不需要改。
    /// <para>
    /// 名字带 <c>netbpm_</c> 前缀是刻意的：业务方自己的变量叫 <c>outcome</c> 是很常见的，
    /// 一旦撞名就会把业务值当成流程结论，而这种错误在联调阶段几乎发现不了。
    /// </para>
    /// </remarks>
    public const string Outcome = "netbpm_outcome";
}

/// <summary>
/// 终止实例请求。
/// </summary>
/// <param name="Reason">终止原因（必填，进审批记录）。</param>
/// <param name="ExpectedRevision">期望的实例修订号；带上则做乐观并发校验。</param>
public sealed record TerminateInstanceRequest(
    string Reason,
    long? ExpectedRevision = null);
