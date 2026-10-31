namespace NetBpm.Platform.Contracts;

/// <summary>
/// 对外公开的授权范围（scope）常量。
/// </summary>
/// <remarks>
/// <para>
/// 规则一：scope 是<b>能力上限</b>，不是数据授权。数据级授权（实例归属、任务归属、租户边界）由
/// 服务端在 scope 之内再做一层校验，<b>两层都要过</b>。
/// </para>
/// <para>
/// 规则二：本类里带 <c>:admin</c> 的 scope 与 <see cref="TaskSubmitOnBehalf"/> <b>必须单独授予</b>，
/// 不得被 <c>workflow:*</c> 之类的通配覆盖（服务端实现见 <see cref="PlatformScopes.IsSatisfiedBy"/>）。
/// </para>
/// </remarks>
public static class PlatformScopes
{
    /// <summary>读流程目录、定义原文、版本列表、流程图。</summary>
    public const string ProcessRead = "workflow:process:read";

    /// <summary>读流程实例视图、参数快照、实例级流程图。</summary>
    public const string InstanceRead = "workflow:instance:read";

    /// <summary>发起流程。</summary>
    public const string InstanceStart = "workflow:instance:start";

    /// <summary>终止流程实例（高危，单独授予）。</summary>
    public const string InstanceTerminate = "workflow:instance:terminate";

    /// <summary>读自己的待办与待办详情。</summary>
    public const string TaskRead = "workflow:task:read";

    /// <summary>提交审批（审批结论必须落到一个真人主体）。</summary>
    public const string TaskSubmit = "workflow:task:submit";

    /// <summary>
    /// 代他人提交。仅在 token exchange（RFC 8693）带来的 <c>act.sub</c> 场景下有效，
    /// 用于风控拉清单；纯服务令牌即使持有本 scope 也不得提交。
    /// </summary>
    public const string TaskSubmitOnBehalf = "workflow:task:submit:onbehalf";

    /// <summary>认领、取消认领、转办。</summary>
    public const string TaskAssign = "workflow:task:assign";

    /// <summary>读与自己相关的审批记录与已办。</summary>
    public const string HistoryRead = "workflow:history:read";

    /// <summary>跨人查看全部审批历史（审计角色，单独授予）。</summary>
    public const string HistoryReadAll = "workflow:history:read:all";

    /// <summary>部署、挂起、激活流程定义。</summary>
    public const string AdminProcess = "workflow:admin:process";

    /// <summary>跳转节点、实例迁移、批量干预。</summary>
    public const string AdminInstance = "workflow:admin:instance";

    /// <summary>越过 schema 直接读写流程变量（默认不授予任何客户端）。</summary>
    public const string AdminVariables = "workflow:admin:variables";

    /// <summary>维护客户端注册、租户、流程授权与配额。</summary>
    public const string AdminClient = "workflow:admin:client";

    /// <summary>事件订阅管理。</summary>
    public const string EventSubscribe = "workflow:event:subscribe";

    /// <summary>全部公开 scope，供管理端展示与校验（顺序与文档一致）。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ProcessRead,
        InstanceRead,
        InstanceStart,
        InstanceTerminate,
        TaskRead,
        TaskSubmit,
        TaskSubmitOnBehalf,
        TaskAssign,
        HistoryRead,
        HistoryReadAll,
        AdminProcess,
        AdminInstance,
        AdminVariables,
        AdminClient,
        EventSubscribe,
    ];

    /// <summary>
    /// 判断一组已授予的 scope 是否满足所需 scope。
    /// </summary>
    /// <remarks>
    /// <para>匹配规则只有两条：完全相同即通过；<c>workflow:*</c> 覆盖全部<b>非</b> <c>:admin</c> 且非 <c>:onbehalf</c> 的 scope。</para>
    /// <para>
    /// 刻意<b>不</b>做前缀通配（<c>workflow:task:*</c> 不覆盖 <c>workflow:task:submit</c>）：
    /// 审批提交与代提交是两种风险等级，不能用一个通配把两者一起发出去。
    /// </para>
    /// </remarks>
    /// <param name="grantedScopes">令牌里已授予的 scope 集合。</param>
    /// <param name="requiredScope">本次操作需要的 scope。</param>
    public static bool IsSatisfiedBy(IReadOnlyCollection<string> grantedScopes, string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(grantedScopes);

        if (grantedScopes.Contains(requiredScope))
        {
            return true;
        }

        if (!grantedScopes.Contains("*") && !grantedScopes.Contains("workflow:*"))
        {
            return false;
        }

        return !requiredScope.Contains(":admin", StringComparison.Ordinal)
            && !requiredScope.EndsWith(":onbehalf", StringComparison.Ordinal);
    }
}
