namespace NetBpm.Platform.Contracts;

/// <summary>
/// 流程的<b>能力声明</b>——调用方据此决定「我现在能为这条流程做什么」，
/// 而不是先调一次再按错误码猜。
/// </summary>
/// <remarks>
/// <para>
/// 这些字段全部是服务端<b>已经判定过</b>的结论，不是原始事实：例如
/// <see cref="CanStart"/> 同时考虑了流程状态（是否挂起 / 已下线）、是否部署过、
/// 以及本次调用方的客户端白名单与 scope。调用方直接照着用。
/// </para>
/// <para>
/// 与 <see cref="ProcessDescriptor.Status"/> 的分工：状态是「流程本身怎么样」，
/// 能力是「<b>你</b>能对它做什么」。两者会不一致（流程是 <c>ACTIVE</c>，
/// 但你这个客户端只有只读 scope，于是 <see cref="CanStart"/> 为 <see langword="false"/>）。
/// </para>
/// </remarks>
/// <param name="CanStart">
/// 是否可以发起新实例：流程已部署、状态为生效、且本次调用方被允许发起。
/// </param>
/// <param name="CanReadDefinition">是否可以读取定义原文（BPMN XML）。</param>
/// <param name="CanReadDiagram">是否可以读取流程图（SVG）。</param>
/// <param name="HasParameterSchema">
/// 是否声明了参数规范。为 <see langword="false"/> 表示该流程未声明参数，
/// 调用方应按「不校验额外参数」处理（保持旧流程可用）。
/// </param>
/// <param name="HasFormSchema">是否声明了表单规范（审批页字段）。</param>
/// <param name="SupportedSemantics">
/// 该流程认可哪些审批语义（从流程定义的审批选项推导）。
/// <para>
/// 空数组表示<b>未声明</b>，不是「什么都不支持」——此时调用方应按引擎默认口径
/// （<c>APPROVE</c> / <c>REJECT</c> / <c>RETURN</c>）处理。
/// </para>
/// </param>
public sealed record ProcessCapabilities(
    bool CanStart,
    bool CanReadDefinition,
    bool CanReadDiagram,
    bool HasParameterSchema,
    bool HasFormSchema,
    IReadOnlyList<ApprovalSemantic> SupportedSemantics);

/// <summary>
/// 审批选项 id 到平台语义的<b>默认字典</b>（三码模型的第一层）。
/// </summary>
/// <remarks>
/// <para>
/// 引擎侧的 <c>optionId</c> 是流程定义自己的词汇（可以是 <c>"1"</c>、<c>"pass"</c>、
/// <c>"refund_to_finance"</c>）；平台只保证四个核心语义稳定。没有字典时，
/// 调用方就只能硬编码 <c>"1"</c>，一旦流程改选项 id 就静默走错分支。
/// </para>
/// <para>
/// 本类给出<b>跨流程通用的默认映射</b>（数字 1/2/3 与常见英文词）。
/// 流程特有的选项（如「转财务复核」）由流程在
/// <c>nb_approval_dictionary</c> 里声明，最后仍然落到 <see cref="ApprovalSemantic.Custom"/>。
/// 未识别的 id 一律视为 <see cref="ApprovalSemantic.Custom"/>，<b>不猜测</b>——
/// 猜错的后果是「把退回当成通过」，比返回「自定义」严重得多。
/// </para>
/// </remarks>
public static class ApprovalSemantics
{
    private static readonly Dictionary<string, ApprovalSemantic> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = ApprovalSemantic.Approve,
            ["approve"] = ApprovalSemantic.Approve,
            ["approved"] = ApprovalSemantic.Approve,
            ["agree"] = ApprovalSemantic.Approve,
            ["pass"] = ApprovalSemantic.Approve,
            ["passed"] = ApprovalSemantic.Approve,
            ["yes"] = ApprovalSemantic.Approve,
            ["true"] = ApprovalSemantic.Approve,

            ["2"] = ApprovalSemantic.Reject,
            ["reject"] = ApprovalSemantic.Reject,
            ["rejected"] = ApprovalSemantic.Reject,
            ["refuse"] = ApprovalSemantic.Reject,
            ["refused"] = ApprovalSemantic.Reject,
            ["decline"] = ApprovalSemantic.Reject,
            ["disagree"] = ApprovalSemantic.Reject,
            ["no"] = ApprovalSemantic.Reject,
            ["false"] = ApprovalSemantic.Reject,

            ["3"] = ApprovalSemantic.Return,
            ["return"] = ApprovalSemantic.Return,
            ["returned"] = ApprovalSemantic.Return,
            ["back"] = ApprovalSemantic.Return,
            ["rework"] = ApprovalSemantic.Return,
            ["recall"] = ApprovalSemantic.Return,

            ["delegate"] = ApprovalSemantic.Delegate,
            ["transfer"] = ApprovalSemantic.Delegate,
            ["forward"] = ApprovalSemantic.Delegate,
        };

    /// <summary>
    /// 把选项 id 解析成平台语义；未识别返回 <see cref="ApprovalSemantic.Custom"/>。
    /// </summary>
    /// <param name="optionId">流程定义里的 <c>activiti:value/@id</c>。</param>
    /// <exception cref="ArgumentException">选项 id 为空。</exception>
    public static ApprovalSemantic Resolve(string optionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);

        return Known.TryGetValue(optionId.Trim(), out var semantic)
            ? semantic
            : ApprovalSemantic.Custom;
    }

    /// <summary>语义对应的统计口径（BI / 看板用）。</summary>
    /// <param name="semantic">平台语义。</param>
    /// <remarks>
    /// 退回计入负向：它意味着这一单<b>没能</b>在本环节通过，虽然实例还在运行。
    /// 把退回算成中性会让「驳回率」被人为压低。
    /// </remarks>
    public static ApprovalOutcomeClass OutcomeClassOf(ApprovalSemantic semantic) => semantic switch
    {
        ApprovalSemantic.Approve => ApprovalOutcomeClass.Positive,
        ApprovalSemantic.Reject => ApprovalOutcomeClass.Negative,
        ApprovalSemantic.Return => ApprovalOutcomeClass.Negative,
        _ => ApprovalOutcomeClass.Neutral,
    };
}
