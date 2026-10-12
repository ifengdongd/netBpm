namespace NetBpm.Runtime;

/// <summary>
/// 任务查询条件。
/// <para>
/// ⚠️ <b>必须至少给一个条件</b>：本子集不提供「查全部任务」——
/// 全表扫描在真实部署里是事故源，因此返回空/报错而不是全量。
/// </para>
/// </summary>
/// <param name="Assignee">按办理人查（对应 <c>TaskQuery.taskAssignee</c>）。</param>
/// <param name="CandidateUser">按候选人查（对应 <c>TaskQuery.taskCandidateUser</c>，会展开用户所属组）。</param>
/// <param name="ProcessInstanceId">
/// 按流程实例查（对应 <c>TaskQuery.processInstanceId</c>）——「本实例当前停留在哪些待办上」。
/// <para>
/// 与 <paramref name="Assignee"/> / <paramref name="CandidateUser"/> 同时给出时为<b>交集</b>；
/// 交集在内存里求（本子集不给任务表做任意组合 SQL），因此按实例查的代价是"该实例的待办数"。
/// </para>
/// </param>
public sealed record TaskQuery(
    string? Assignee = null,
    string? CandidateUser = null,
    string? ProcessInstanceId = null)
{
    /// <summary>是否至少给了一个查询条件。</summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Assignee)
        || !string.IsNullOrWhiteSpace(CandidateUser)
        || !string.IsNullOrWhiteSpace(ProcessInstanceId);
}
