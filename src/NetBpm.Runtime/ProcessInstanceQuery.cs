namespace NetBpm.Runtime;

/// <summary>
/// 运行中流程实例的查询条件（在办监控 / 按业务单号反查）。
/// <para>
/// 过滤在门面内完成（本子集不给 <c>ACT_RU_EXECUTION</c> 做任意组合 SQL，与待办查询同一口径）；
/// 排序固定为 <b>开始时间倒序</b>，保证跨页不重不漏。
/// </para>
/// </summary>
/// <param name="ProcessDefinitionId">按流程定义 id 精确过滤。</param>
/// <param name="ProcessDefinitionKey">
/// 按流程 key 过滤（同 key 的<b>全部版本</b>）。与 <paramref name="ProcessDefinitionId"/> 同时给出时为交集。
/// </param>
/// <param name="BusinessKey">按业务主键精确过滤。</param>
/// <param name="StartUserId">按发起人过滤。</param>
/// <param name="TenantId">按租户过滤（空串表示无租户）。</param>
/// <param name="IsActive">按根执行的活动状态过滤（挂起/结束的判定不由它承担）。</param>
/// <param name="StartedAfter">开始时间下界（含）。</param>
/// <param name="StartedBefore">开始时间上界（含）。</param>
/// <param name="FirstResult">跳过的条数，默认 0。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限（REST 层会给默认页大小）。</param>
public sealed record ProcessInstanceQuery(
    string? ProcessDefinitionId = null,
    string? ProcessDefinitionKey = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    string? TenantId = null,
    bool? IsActive = null,
    DateTime? StartedAfter = null,
    DateTime? StartedBefore = null,
    int FirstResult = 0,
    int? MaxResults = null);

/// <summary>
/// 历史流程实例的查询条件（已办结检索 / 审计 / 报表）。
/// <para>
/// 与 <see cref="ProcessInstanceQuery"/> 分开：运行态行在实例结束后已被删除，
/// 两者数据源不同，混在一个查询里会出现「刚结束的实例查不到」。
/// </para>
/// </summary>
/// <param name="ProcessDefinitionId">按流程定义 id 精确过滤。</param>
/// <param name="ProcessDefinitionKey">按流程 key 过滤（全部版本）。</param>
/// <param name="BusinessKey">按业务主键精确过滤。</param>
/// <param name="StartUserId">按发起人过滤。</param>
/// <param name="Finished">
/// <see langword="true"/> 只看已结束；<see langword="false"/> 只看仍在运行；<see langword="null"/> 不限制。
/// </param>
/// <param name="Deleted">
/// <see langword="true"/> 只看被删除（<c>DELETE_REASON_</c> 非空）；<see langword="null"/> 不限制。
/// </param>
/// <param name="StartedAfter">开始时间下界（含）。</param>
/// <param name="StartedBefore">开始时间上界（含）。</param>
/// <param name="EndedAfter">结束时间下界（含）。</param>
/// <param name="EndedBefore">结束时间上界（含）。</param>
/// <param name="FirstResult">跳过的条数，默认 0。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限。</param>
/// <param name="TenantId">按租户过滤；<see langword="null"/> 表示不限制（B2.3）。</param>
public sealed record HistoricProcessInstanceQuery(
    string? ProcessDefinitionId = null,
    string? ProcessDefinitionKey = null,
    string? BusinessKey = null,
    string? StartUserId = null,
    bool? Finished = null,
    bool? Deleted = null,
    DateTime? StartedAfter = null,
    DateTime? StartedBefore = null,
    DateTime? EndedAfter = null,
    DateTime? EndedBefore = null,
    int FirstResult = 0,
    int? MaxResults = null,
    string? TenantId = null);

/// <summary>
/// 流程定义的查询条件（定义列表 / 按 key 查全部版本）。
/// </summary>
/// <param name="Key">按流程 key 过滤。</param>
/// <param name="TenantId">按租户过滤；<see langword="null"/> 表示不限制。</param>
/// <param name="FirstResult">跳过的条数，默认 0。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限。</param>
public sealed record ProcessDefinitionQuery(
    string? Key = null,
    string? TenantId = null,
    int FirstResult = 0,
    int? MaxResults = null);
