namespace NetBpm.Runtime;

/// <summary>
/// 历史身份关系查询条件（参与者索引：我发起 / 我审批 / 我是候选人）。
/// <para>
/// 与 <see cref="HistoricTaskQuery"/> 同一口径：为避免无条件的全表扫描，<b>至少要给一个条件</b>
/// （用户 / 组 / 实例 id / 任务 id）。典型的「我参与过的单据」只给
/// <paramref name="UserId"/> 与 <paramref name="GroupIds"/>。
/// </para>
/// <para>
/// 用户与组之间按 <b>OR</b> 命中：本人是我的候选人、或我所在组是候选组，都算参与。
/// </para>
/// </summary>
/// <param name="UserId">按用户 id 过滤。</param>
/// <param name="GroupIds">按组 id 集合过滤（与 UserId 是 OR 关系）。</param>
/// <param name="ProcessInstanceId">限定在某个流程实例内。</param>
/// <param name="TaskId">限定在某个任务上。</param>
/// <param name="Type">限定关系类型（<c>candidate</c> / <c>participant</c> …）。</param>
/// <param name="FirstResult">跳过的条数，默认 0。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限。</param>
public sealed record HistoricIdentityLinkQuery(
    string? UserId = null,
    IReadOnlyList<string>? GroupIds = null,
    string? ProcessInstanceId = null,
    string? TaskId = null,
    string? Type = null,
    int FirstResult = 0,
    int? MaxResults = null)
{
    /// <summary>是否至少给了一个查询条件（防止无条件全表扫描）。</summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(UserId)
        || GroupIds is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(ProcessInstanceId)
        || !string.IsNullOrWhiteSpace(TaskId);
}
