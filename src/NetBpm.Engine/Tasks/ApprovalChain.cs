namespace NetBpm.Engine.Tasks;

/// <summary>本次审批链解析的上下文（只读）。</summary>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="ActivityId">当前用户任务节点 id。</param>
/// <param name="StartUserId">发起人（用于「发起人本人跳过」）。</param>
/// <param name="TenantId">租户。</param>
/// <param name="BusinessKey">流程实例的业务主键（发起时传的业务单号）。动态审批人来源用它回查业务归属。</param>
/// <param name="Variables">当前节点可见的流程变量快照（如 <c>applyDeptId</c>）。动态审批人来源用「部门 + 职位」定位审批人。</param>
public sealed record ApprovalChainContext(
    string ProcessDefinitionId,
    string ActivityId,
    string? StartUserId,
    string? TenantId,
    string? BusinessKey = null,
    IReadOnlyDictionary<string, object?>? Variables = null)
{
    /// <summary>取流程变量；不存在时返回 <paramref name="defaultValue"/>。</summary>
    /// <param name="name">变量名。</param>
    /// <param name="defaultValue">缺省值。</param>
    /// <returns>变量值或缺省值。</returns>
    public object? GetVariable(string name, object? defaultValue = null) =>
        Variables is not null && Variables.TryGetValue(name, out var value) ? value : defaultValue;

    /// <summary>取流程变量的字符串形式；不存在或非字符串时返回 <see langword="null"/>。</summary>
    /// <param name="name">变量名。</param>
    /// <returns>字符串值。</returns>
    public string? GetStringVariable(string name) =>
        GetVariable(name)?.ToString();
}

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
/// 审批人解析钩子：既做<b>后置整形</b>（对已解析出的办理人集合做去重 / 兜底），
/// 也能充当<b>动态审批人来源</b>（节点未静态指定审批人时，按 <see cref="ApprovalChainContext.BusinessKey"/>
/// 与 <see cref="ApprovalChainContext.Variables"/> 里的部门 / 职位等业务信息查外部系统定人）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是「后置」而不是「前置」：审批人的来源很多（固定人 / 表达式 / 候选组 / 动态规则），
/// 只有拿到解析后的<b>具体人集合</b>才能做去重与兜底；在解析前介入只能改配置，无法处理
/// 「连续两级主管其实是同一个人」这类情况。
/// </para>
/// <para>
/// <b>动态审批人来源</b>：当节点既没配 <c>assignee</c>、也没配 <c>candidateUsers</c> /
/// <c>candidateGroups</c> 时，引擎仍会调用本钩子，此时 <paramref name="approvers"/> 为空集合。
/// 实现方可据此用 <see cref="ApprovalChainContext"/>（businessKey + 变量 + 节点 id 表达职位）
/// 查外部系统返回审批人；返回非空集合会写入候选用户（进「我的待办」候选池）。
/// 返回空集合仍表示「该节点自动跳过」。
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
    /// <summary>对解析出的办理人集合做后置整形 / 动态补人。</summary>
    /// <param name="context">审批链上下文（含 businessKey 与流程变量）。</param>
    /// <param name="approvers">已解析出的办理人；节点未静态配人时为空集合。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>最终办理人集合（空集 = 跳过节点）。</returns>
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
