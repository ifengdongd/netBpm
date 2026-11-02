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
    /// <summary>
    /// 实例 id 白名单（<c>in</c> 语义，落在 <c>ACT_HI_PROCINST.PROC_INST_ID_</c> 上）。
    /// </summary>
    /// <remarks>与运行态的 <c>ProcessInstanceFilter.ProcessInstanceIds</c> 同一用途，两者口径必须一致。</remarks>
    IReadOnlyCollection<string>? ProcessInstanceIds = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    bool? Finished = null,
    bool? Deleted = null,
    DateTime? StartedAfter = null,
    DateTime? StartedBefore = null,
    DateTime? EndedAfter = null,
    DateTime? EndedBefore = null,
    string? TenantId = null,
    /// <summary>
    /// 租户白名单（<c>in</c> 语义）；与 <paramref name="TenantId"/> 同时给出时两个条件都生效。
    /// </summary>
    /// <remarks>
    /// 平台面要「本租户 + 全局共享（空租户）」一起看，等值条件表达不了这件事；
    /// 用列表而不是「等值或空」的开关，是为了让 SQL 保持 <c>in</c>（索引可用）而不是 <c>or</c>（多半不走索引）。
    /// </remarks>
    IReadOnlyCollection<string>? TenantIds = null,
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

/// <summary>分页查询结果：当前页 + 同口径总数（总数不受分页影响）。</summary>
/// <param name="Items">当前页行。</param>
/// <param name="Total">满足过滤条件的总行数。</param>
public sealed record PagedQueryResult<T>(IReadOnlyList<T> Items, int Total);

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

    /// <summary>
    /// 分页取页 + 同口径总数，在<b>一次会话租借</b>内完成。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认实现退化为「两次独立调用」，语义与 <see cref="QueryProcessInstancesAsync"/> +
    /// <see cref="CountProcessInstancesAsync"/> 完全一致；数据库宿主覆盖它以复用同一条连接。
    /// </para>
    /// <para>
    /// 为什么值得单独一个方法：分页接口天然要「页 + 总数」，分两次调用在命令外就是两次连接租借——
    /// 多一次往返，且两次结果来自两个不同时刻，并发写入时会产出「Total=21、当页却只有 20 行」这种
    /// 对不上的响应。
    /// </para>
    /// </remarks>
    async ValueTask<PagedQueryResult<HistoricProcessInstance>> QueryProcessInstancesWithCountAsync(
        HistoricProcessInstanceFilter filter,
        CancellationToken ct = default)
    {
        var items = await QueryProcessInstancesAsync(filter, ct).ConfigureAwait(false);
        var total = await CountProcessInstancesAsync(filter with { MaxResults = null }, ct).ConfigureAwait(false);

        return new PagedQueryResult<HistoricProcessInstance>(items, total);
    }

    /// <summary>分页取历史任务 + 总数，一次会话租借内完成（同 <see cref="QueryProcessInstancesWithCountAsync"/>）。</summary>
    async ValueTask<PagedQueryResult<HistoricTaskInstance>> QueryTaskInstancesWithCountAsync(
        HistoricTaskFilter filter,
        CancellationToken ct = default)
    {
        var items = await QueryTaskInstancesAsync(filter, ct).ConfigureAwait(false);
        var total = await CountTaskInstancesAsync(filter with { MaxResults = null }, ct).ConfigureAwait(false);

        return new PagedQueryResult<HistoricTaskInstance>(items, total);
    }
}
