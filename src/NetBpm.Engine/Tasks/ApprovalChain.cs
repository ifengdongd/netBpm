namespace NetBpm.Engine.Tasks;

/// <summary>本次审批链解析的上下文（只读）。</summary>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="ActivityId">当前用户任务节点 id。</param>
/// <param name="StartUserId">发起人（用于「发起人本人跳过」）。</param>
/// <param name="TenantId">租户。</param>
public sealed record ApprovalChainContext(
    string ProcessDefinitionId,
    string ActivityId,
    string? StartUserId,
    string? TenantId);

/// <summary>审批链解析结果。</summary>
/// <param name="Approvers">
/// 最终办理人集合。<b>空集</b>表示该节点应被自动跳过（自动通过）。
/// </param>
/// <param name="RemovedByRule">
/// 被规则摘除的办理人及原因（审计 / 审批轨迹展示用）。
/// </param>
public sealed record ApprovalChainResult(
    IReadOnlyList<string> Approvers,
    IReadOnlyDictionary<string, string> RemovedByRule);

/// <summary>
/// 审批链预处理钩子（<b>后置</b>：在节点配置解析出办理人集合之后介入）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是「后置」而不是「前置」：审批人的来源很多（固定人 / 表达式 / 候选组 / 未来的动态规则），
/// 只有拿到解析后的<b>具体人集合</b>才能做去重与兜底；在解析前介入只能改配置，无法处理
/// 「连续两级主管其实是同一个人」这类情况。
/// </para>
/// <para>
/// 契约（实现方必须遵守）：
/// <list type="bullet">
///   <item><description>保持<b>入参顺序</b>（审批链是有序的，会签 / 顺序会签依赖它）；</description></item>
///   <item><description>不得引入重复项；</description></item>
///   <item><description>返回空集 ⇒ 引擎跳过该节点（等同于自动通过），不产生待办。</description></item>
/// </list>
/// </para>
/// <para>
/// 调用时机：用户任务行已插入、办理关系尚未写入之前（见
/// <c>UserTaskActivityBehavior.HandleAssignmentsAsync</c>）。
/// </para>
/// </remarks>
public interface IApproverResolver
{
    /// <summary>对解析出的办理人集合做后置整形。</summary>
    ValueTask<IReadOnlyList<string>> PostProcessAsync(
        ApprovalChainContext context,
        IReadOnlyList<string> approvers,
        CancellationToken ct = default);
}

/// <summary>
/// 默认审批链整形规则：
/// <list type="number">
///   <item><description><b>发起人本人跳过</b>：审批人 == 发起人时摘除（自己审自己没有意义）；</description></item>
///   <item><description><b>连续相同审批人合并</b>：相邻节点审批人相同只保留第一个；</description></item>
///   <item><description><b>空审批人转管理员</b>：节点<b>原本就没配人</b>时转给管理员，而不是静默跳过
///     （配置缺失是错误，必须可见）；但若是有审批人被规则摘空，则跳过而不是升级。</description></item>
/// </list>
/// </summary>
public sealed class ApproverDeduplicationOptions
{
    /// <summary>是否摘除发起人本人。默认 <see langword="true"/>。</summary>
    public bool SkipInitiator { get; set; } = true;

    /// <summary>是否合并连续重复审批人。默认 <see langword="true"/>。</summary>
    public bool MergeConsecutiveDuplicates { get; set; } = true;

    /// <summary>
    /// 节点未配置任何审批人时的兜底管理员。
    /// 为 <see langword="null"/> 时不兜底 —— 节点直接被跳过（保持旧行为）。
    /// </summary>
    public string? FallbackAdminUserId { get; set; }
}

/// <summary>
/// 内置的审批链去重 / 自动跳过规则实现。
/// </summary>
public sealed class ApproverDeduplicationResolver : IApproverResolver
{
    private readonly ApproverDeduplicationOptions _options;

    public ApproverDeduplicationResolver(ApproverDeduplicationOptions? options = null) =>
        _options = options ?? new ApproverDeduplicationOptions();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> PostProcessAsync(
        ApprovalChainContext context,
        IReadOnlyList<string> approvers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(approvers);

        // ① 归一化：去空白
        var normalized = approvers
            .Where(approver => !string.IsNullOrWhiteSpace(approver))
            .Select(approver => approver.Trim())
            .ToList();

        // ② 兜底：节点原本就没配人 ⇒ 转管理员（配置缺失必须可见，不能静默通过）
        if (normalized.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(
                string.IsNullOrWhiteSpace(_options.FallbackAdminUserId)
                    ? []
                    : [_options.FallbackAdminUserId!.Trim()]);
        }

        // ③ 连续相同审批人合并（保序，只压掉相邻重复）
        var candidates = new List<string>(normalized.Count);

        foreach (var candidate in normalized)
        {
            if (candidates.Count == 0
                || !string.Equals(candidates[^1], candidate, StringComparison.Ordinal))
            {
                candidates.Add(candidate);
            }
        }

        // ④ 发起人本人跳过
        if (_options.SkipInitiator && !string.IsNullOrWhiteSpace(context.StartUserId))
        {
            var initiator = context.StartUserId!.Trim();

            candidates.RemoveAll(candidate => string.Equals(candidate, initiator, StringComparison.Ordinal));
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(candidates);
    }
}
