namespace NetBpm.Runtime;

/// <summary>
/// 历史任务查询条件（已办列表 / 审计）。
/// <para>
/// 与旧版不同，<paramref name="ProcessInstanceId"/> <b>不再必填</b>：跨实例的「我的已办」是待办中心的对称能力，
/// 必须能只按办理人 + 时间范围查询。为避免无条件的全表扫描，<b>至少要给一个条件</b>
/// （实例 id / 办理人 / 流程定义 / 节点 id）。
/// </para>
/// <para>
/// 排序口径与待办一致：按 (开始时间, id) 升序再切片，保证跨页不重不漏。
/// </para>
/// </summary>
/// <param name="ProcessInstanceId">流程实例 id（可选；给出时只看该实例）。</param>
/// <param name="Assignee">
/// 按办理人过滤（对应 <c>HistoricTaskInstanceQuery.taskAssignee</c>）；
/// 未签收的任务该列为空，给出此条件时会被自然排除。
/// </param>
/// <param name="OnlyFinished">
/// 只看已结束的历史任务（<c>EndTime</c> 非空）。已办列表通常用它排除"还在办"的那条。
/// </param>
/// <param name="FirstResult">跳过的条数，默认 0。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限。</param>
/// <param name="ProcessDefinitionId">按流程定义 id 过滤。</param>
/// <param name="TaskDefinitionKey">按节点 id 过滤（BPMN 中 <c>userTask/@id</c>）。</param>
/// <param name="FinishedAfter">结束时间下界（含），用于「最近已办」。</param>
/// <param name="FinishedBefore">结束时间上界（含）。</param>
/// <param name="TenantId">按租户过滤；<see langword="null"/> 表示不限制（B2.3）。</param>
public sealed record HistoricTaskQuery(
    string? ProcessInstanceId = null,
    string? Assignee = null,
    bool OnlyFinished = false,
    int FirstResult = 0,
    int? MaxResults = null,
    string? ProcessDefinitionId = null,
    string? TaskDefinitionKey = null,
    DateTime? FinishedAfter = null,
    DateTime? FinishedBefore = null,
    string? TenantId = null)
{
    /// <summary>是否至少给了一个查询条件（防止无条件全表扫描）。</summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(ProcessInstanceId)
        || !string.IsNullOrWhiteSpace(Assignee)
        || !string.IsNullOrWhiteSpace(ProcessDefinitionId)
        || !string.IsNullOrWhiteSpace(TaskDefinitionKey);
}
