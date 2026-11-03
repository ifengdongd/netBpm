using NetBpm.Engine.Tasks;

namespace NetBpm.Demo.Approval;

/// <summary>
/// 动态审批人解析器示例：节点<b>未静态指定</b>审批人时，按业务归属（部门）+ 节点（职位）
/// 查外部系统定位审批人。
/// </summary>
/// <remarks>
/// <para>
/// 这是「审批人从外部系统动态获取」的参考实现，对应需求：
/// <b>流程走到某个人工节点时，根据 businessKey / 部门 id 查所属部门、再定位该部门某个职位的人作为审批人</b>。
/// </para>
/// <para>
/// <b>怎么用</b>：
/// <list type="number">
///   <item><description>BPMN 里该 userTask <b>不写</b> <c>assignee</c> / <c>candidateUsers</c> / <c>candidateGroups</c>
///     （审批人不静态指定）；</description></item>
///   <item><description>发起实例时把部门 id 作为流程变量传入（如 <c>applyDeptId</c>）；</description></item>
///   <item><description>在组合根注册本解析器：
///     <c>services.AddSingleton&lt;IApproverResolver, DynamicApproverResolver&gt;()</c>；</description></item>
///   <item><description>引擎进入该节点时，会以<b>空办理人集合</b>调用 <see cref="PostProcessAsync"/>，
///     本实现据此查外部系统、返回审批人；引擎把返回的人写入候选用户（进「我的待办」）。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>节点 id 即「职位」约定</b>：本示例用一个字典把节点 id 映射到职位（如 <c>task_manager_approve</c> → 经理），
/// 真实系统里可以换成「查流程定义配置」「按节点名匹配」等任何规则 —— 关键是
/// <see cref="ApprovalChainContext.ActivityId"/> 已经告诉解析器「现在要定哪个职位的人」。
/// </para>
/// <para>
/// <b>返回空集合的语义</b>：查不到人（部门不存在 / 职位无人）时返回空集合 —— 引擎跳过该节点（自动通过）。
/// 若业务上「查不到人」必须卡住而不是放行，请抛异常让节点失败，而不是返回空集合。
/// </para>
/// </remarks>
public sealed class DynamicApproverResolver : IApproverResolver
{
    /// <summary>
    /// 节点 id → 职位的映射（演示约定）。真实场景换成流程定义里的配置或字典表。
    /// </summary>
    private static readonly Dictionary<string, string> PositionByActivityId =
        new(StringComparer.Ordinal)
        {
            ["task_leader_approve"] = "leader",   // 组长
            ["task_manager_approve"] = "manager", // 经理
            ["task_director_approve"] = "director", // 总监
        };

    /// <summary>外部系统查询端口（这里抽象成委托，真实场景是一次 HTTP 调用或 SQL）。</summary>
    private readonly Func<string, string, CancellationToken, ValueTask<IReadOnlyList<string>>> _finder;

    public DynamicApproverResolver(
        Func<string, string, CancellationToken, ValueTask<IReadOnlyList<string>>> finder)
    {
        ArgumentNullException.ThrowIfNull(finder);

        _finder = finder;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> PostProcessAsync(
        ApprovalChainContext context,
        IReadOnlyList<string> approvers,
        CancellationToken ct = default)
    {
        // 节点已静态指定审批人时，这里只做透传（本示例不做去重等整形；
        // 需要整形时把它委托给 ApproverDeduplicationResolver 再叠加动态逻辑）。
        if (approvers.Count > 0)
        {
            return approvers;
        }

        // 拿不到职位映射的节点：不动态定人（保持「无审批人」原状）。
        if (!PositionByActivityId.TryGetValue(context.ActivityId, out var position))
        {
            return [];
        }

        // 部门 id 优先从流程变量取（发起时传 applyDeptId）；
        // 缺省时退到 businessKey（业务单号），由外部系统自己按单号反查部门。
        var deptId = context.GetStringVariable("applyDeptId");

        if (string.IsNullOrWhiteSpace(deptId))
        {
            deptId = context.BusinessKey;
        }

        if (string.IsNullOrWhiteSpace(deptId))
        {
            // 既没有部门变量、也没有 businessKey，无法定位部门 → 查不到人。
            return [];
        }

        // 调外部系统：按「部门 + 职位」查审批人（去重、保序）。
        var found = await _finder(deptId, position, ct).ConfigureAwait(false);

        return found
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// 一个「假外部系统」：内置部门 → 职位 → 人 的固定映射，用来演示 / 单测。
/// 真实系统把它替换成 HTTP 调用（如 <c>GET /api/org/{deptId}/approver?position=manager</c>）。
/// </summary>
public static class DemoExternalApproverDirectory
{
    /// <summary>模拟外部系统的部门-职位-审批人表。</summary>
    private static readonly Dictionary<(string DeptId, string Position), string[]> Approvers =
        new()
        {
            [("102", "manager")] = ["manager1"],            // 成本管理部的经理
            [("101", "leader")] = ["leader1"],              // 信息技术部的组长
            [("103", "manager")] = ["manager1"],            // 综合管理部的经理
            [("101", "manager")] = ["manager1", "manager2"],// 多审批人（会签示例）
        };

    /// <summary>按部门 + 职位查审批人。</summary>
    public static ValueTask<IReadOnlyList<string>> FindAsync(
        string deptId,
        string position,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>(
            Approvers.TryGetValue((deptId, position), out var users) ? users : []);
}
