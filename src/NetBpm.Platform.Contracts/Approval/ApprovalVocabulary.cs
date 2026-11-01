using System.Text.Json.Serialization;

namespace NetBpm.Platform.Contracts;

/// <summary>
/// 审批选项的<b>语义</b>——平台字典，跨流程稳定。
/// </summary>
/// <remarks>
/// <para>
/// 这是本方案对现有实现最重要的一次改写：今天 <c>ApprovalResult</c> 把结论固化成了
/// <c>1 = 通过 / 2 = 拒绝 / 3 = 退回</c> 三个魔法数字，并与 BPMN 的 <c>activiti:value/@id</c> 焊死。
/// 后果是「业务方只要按 1 写分支，流程哪天加一个『转财务复核』，分支就会静默走错」。
/// </para>
/// <para>
/// 新模型三码分离：<c>optionId</c>（流程定义拥有，可以是任意字符串）→
/// <c>semantic</c>（本枚举，稳定）→ <c>outcomeClass</c>（统计口径）。
/// <b>调用方只认后两个</b>，流程随便加选项都不会破坏集成代码。
/// </para>
/// </remarks>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ApprovalSemantic>))]
public enum ApprovalSemantic
{
    /// <summary>同意 / 通过：流程按「正向」分支继续。</summary>
    Approve = 0,

    /// <summary>不同意 / 拒绝：流程通常走向驳回结束。</summary>
    Reject = 1,

    /// <summary>退回：回到指定（或上一）环节重办，实例仍在运行。</summary>
    Return = 2,

    /// <summary>转办 / 移交：不表达意见，只转移办理人。</summary>
    Delegate = 3,

    /// <summary>流程自定义语义（如「转财务复核」「暂存」）；行为由流程定义决定。</summary>
    Custom = 4,

    /// <summary>
    /// 办结（无审批结论的终态）：实例走完了或被人为终止，但<b>没有任何一条审批决定</b>可以代表结论。
    /// </summary>
    /// <remarks>
    /// 只由平台在「结论三级回落」的最后一级产生（见 <see cref="ConclusionSource.Fallback"/>），
    /// 流程定义里不会出现这个 optionId。单独列出来是为了让审计能区分
    /// 「流程说通过了」和「平台只知道它结束了」——这两件事的合规含义完全不同。
    /// </remarks>
    Closed = 5,
}

/// <summary>
/// 选项的统计口径（BI / 看板用，与流程是否继续无关）。
/// </summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<ApprovalOutcomeClass>))]
public enum ApprovalOutcomeClass
{
    /// <summary>正向（计入「通过率」分子）。</summary>
    Positive = 0,

    /// <summary>负向（计入驳回 / 退回率）。</summary>
    Negative = 1,

    /// <summary>中性（转办、暂存、知会，不计入分子分母）。</summary>
    Neutral = 2,
}

/// <summary>
/// 一个环节上的可选项——<b>由流程定义声明，由平台字典补语义</b>。
/// </summary>
/// <remarks>
/// 调用方拿到 <see cref="ApprovalOption"/> 就能渲染按钮与校验规则，
/// 不需要知道 BPMN 里这个选项写成了 <c>activiti:value id="1"</c> 还是 <c>id="APPROVE"</c>。
/// </remarks>
/// <param name="OptionId">选项 id（流程定义拥有；提交时原样回传）。</param>
/// <param name="Label">按钮文案（如「同意」「退回修改」）。</param>
/// <param name="Semantic">语义（稳定，可据此做分支判断）。</param>
/// <param name="OutcomeClass">统计口径。</param>
/// <param name="RequiresComment">是否必须填写审批意见。</param>
/// <param name="RequiresTargetNode">是否必须指定目标节点（退回 / 跳转类）。</param>
/// <param name="AllowedTargetNodes">
/// 可选的目标节点白名单；为 <see langword="null"/> 表示按流程定义默认（通常是上一环节或指定回退点）。
/// </param>
/// <param name="CommentPrompt">意见输入框的提示文案。</param>
/// <param name="Attributes">流程自定义的附加属性（如「需二次确认」），调用方可忽略。</param>
public sealed record ApprovalOption(
    string OptionId,
    string Label,
    ApprovalSemantic Semantic,
    ApprovalOutcomeClass OutcomeClass,
    bool RequiresComment,
    bool RequiresTargetNode,
    IReadOnlyList<string>? AllowedTargetNodes,
    string? CommentPrompt,
    IReadOnlyDictionary<string, string>? Attributes);

/// <summary>
/// 调用方提交的审批决定。
/// </summary>
/// <remarks>
/// <see cref="Semantic"/> 是<b>可选</b>的：带上它，服务端会做一次「你以为是同意，其实这个选项是退回」的
/// 一致性校验（不一致直接 422，见 <see cref="ErrorCodes.DecisionSemanticMismatch"/>）。
/// 建议 SDK 永远带上——它是唯一能挡住「流程定义被改而调用方不知情」的护栏。
/// </remarks>
/// <param name="OptionId">选项 id。</param>
/// <param name="Semantic">调用方理解的语义（可选，用于一致性校验）。</param>
/// <param name="Comment">审批意见。</param>
/// <param name="TargetNodeId">目标节点（退回类选项必填）。</param>
public sealed record ApprovalDecision(
    string OptionId,
    ApprovalSemantic? Semantic = null,
    string? Comment = null,
    string? TargetNodeId = null);

/// <summary>
/// 环节（节点）上多个人办理时的聚合口径。
/// </summary>
[JsonConverter(typeof(ScreamingSnakeCaseEnumConverter<NodeOutcomeAggregation>))]
public enum NodeOutcomeAggregation
{
    /// <summary>单人办理（或串签的当前一签），结果即该人的决定。</summary>
    Single = 0,

    /// <summary>会签全同意才算通过。</summary>
    AllApproved = 1,

    /// <summary>或签，任一人同意即通过。</summary>
    AnyApproved = 2,

    /// <summary>多数通过。</summary>
    Majority = 3,
}

/// <summary>
/// 环节聚合结果——「3 人 2 通过 1 拒绝」这件事的<b>标准表达</b>。
/// </summary>
/// <remarks>
/// 现状没有这个概念：<c>WorkflowStepRecord</c> 只能表达「某人在某环节提交了什么」，
/// 会签环节的整体结论得让调用方自己数。平台化后由服务端算好，调用方直接读。
/// </remarks>
/// <param name="ActivityId">节点 id。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="Aggregation">聚合口径。</param>
/// <param name="Satisfied">该环节是否已达成通过条件；未完成时为 <see langword="false"/>。</param>
/// <param name="Total">应办理人数（会签人数）。</param>
/// <param name="Positive">同意人数。</param>
/// <param name="Negative">不同意人数。</param>
/// <param name="Pending">尚未办理人数。</param>
/// <param name="DecidedAt">环节达成结论的时间；未完成为 <see langword="null"/>。</param>
/// <param name="Round">
/// 这是该节点的第几轮（<b>1 起</b>；<c>0</c> = 老数据，轮次未知）。
/// <para>
/// 同一个 <see cref="ActivityId"/> 在结果里出现多次不是重复，而是「退回 / 跳转后重办了」：
/// 每轮单独成账，人数不能跨轮相加。调用方要按 <see cref="Round"/> 区分，
/// 否则「第 1 轮 1 票 + 第 2 轮 2 票」会被误读成「3 人 3 票」。
/// </para>
/// </param>
public sealed record NodeOutcome(
    string ActivityId,
    string? ActivityName,
    NodeOutcomeAggregation Aggregation,
    bool Satisfied,
    int Total,
    int Positive,
    int Negative,
    int Pending,
    DateTimeOffset? DecidedAt,
    int Round = 0);

/// <summary>
/// 节点级审批选项集——按节点给出「这里能点哪几个按钮」。
/// </summary>
/// <param name="ActivityId">节点 id。</param>
/// <param name="ActivityName">节点名。</param>
/// <param name="Options">可选项。</param>
public sealed record NodeApprovalOptions(
    string ActivityId,
    string? ActivityName,
    IReadOnlyList<ApprovalOption> Options);
