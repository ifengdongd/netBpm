namespace NetBpm.Engine.History;

/// <summary>
/// 历史流程实例查询条件（数据库宿主用它把过滤 / 排序 / 分页下推到 SQL）。
/// <para>
/// 与门面层的 <c>HistoricProcessInstanceQuery</c> 分开：这里是<b>持久化契约</b>，
/// 不含「按 key 解析定义 id」这类应用层逻辑（由门面把 key 展开成 <see cref="ProcessDefinitionIds"/>）。
/// </para>
/// </summary>
public sealed record HistoricProcessInstanceFilter(
    string? ProcessInstanceId = null,
    string? ProcessDefinitionId = null,
    IReadOnlyCollection<string>? ProcessDefinitionIds = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    bool? Finished = null,
    bool? Deleted = null,
    DateTime? StartedAfter = null,
    DateTime? StartedBefore = null,
    DateTime? EndedAfter = null,
    DateTime? EndedBefore = null,
    string? TenantId = null,
    int FirstResult = 0,
    int? MaxResults = 20,
    string SortBy = "startTime",
    bool Descending = true);

/// <summary>历史任务查询条件（持久化契约）。</summary>
public sealed record HistoricTaskFilter(
    string? ProcessInstanceId = null,
    string? Assignee = null,
    bool OnlyFinished = false,
    string? ProcessDefinitionId = null,
    string? TaskDefinitionKey = null,
    DateTime? FinishedAfter = null,
    DateTime? FinishedBefore = null,
    string? TenantId = null,
    int FirstResult = 0,
    int? MaxResults = 20,
    string SortBy = "startTime",
    bool Descending = false);

/// <summary>
/// 历史查询的服务端下推端口（<b>可选能力</b>）。
/// <para>
/// 历史管理器实现它以后，门面不再「取全表 + 内存 Where/Skip/Take」：
/// 过滤、排序、分页与 <c>count(*)</c> 全部在 SQL 里完成。
/// 未实现的宿主（内存 / 测试替身）继续走门面的内存回退路径，行为保持一致。
/// </para>
/// </summary>
public interface IHistoricQueryManager
{
    /// <summary>分页查历史流程实例（按 <c>START_TIME_</c> 排序，默认倒序）。</summary>
    ValueTask<IReadOnlyList<HistoricProcessInstance>> QueryProcessInstancesAsync(
        HistoricProcessInstanceFilter filter,
        CancellationToken ct = default);

    /// <summary>数历史流程实例（与 <see cref="QueryProcessInstancesAsync"/> 同口径，不取行）。</summary>
    ValueTask<int> CountProcessInstancesAsync(
        HistoricProcessInstanceFilter filter,
        CancellationToken ct = default);

    /// <summary>分页查历史任务（按 <c>START_TIME_</c> 排序，默认正序）。</summary>
    ValueTask<IReadOnlyList<HistoricTaskInstance>> QueryTaskInstancesAsync(
        HistoricTaskFilter filter,
        CancellationToken ct = default);

    /// <summary>数历史任务（与 <see cref="QueryTaskInstancesAsync"/> 同口径）。</summary>
    ValueTask<int> CountTaskInstancesAsync(
        HistoricTaskFilter filter,
        CancellationToken ct = default);

    /// <summary>
    /// 分页查历史身份关系（<c>ACT_HI_IDENTITYLINK</c>）——<b>参与者索引</b>的数据源。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 有了它，「我参与过的单据」才能交给数据库回答：候选命中（<c>USER_ID_</c> 或
    /// <c>GROUP_ID_</c>）的行直接索引命中，不用把整张关系表读进内存去比对。
    /// </para>
    /// <para>
    /// 排序按 <c>ID_</c> 升序：调用方只用它的<b>实例 id 投影</b>（会去重），
    /// 行之间的先后没有业务含义。
    /// </para>
    /// </remarks>
    ValueTask<IReadOnlyList<HistoricIdentityLink>> QueryIdentityLinksAsync(
        HistoricIdentityLinkFilter filter,
        CancellationToken ct = default);
}
