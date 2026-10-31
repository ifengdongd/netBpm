namespace NetBpm.Engine.History;

/// <summary>
/// 历史身份关系行（<c>ACT_HI_IDENTITYLINK</c>）——「谁与这条单据有关」的<b>引擎侧索引</b>。
/// </summary>
/// <remarks>
/// <para>
/// 这张表此前<b>只写不读</b>（<see cref="IHistoryManager.RecordIdentityLinkCreatedAsync"/>），
/// 因此平台侧完全没有「我参与过哪些单据」的能力。运行态的 <c>ACT_RU_IDENTITYLINK</c> 补不上这个缺口：
/// 任务完成、实例结束时它的行会被级联清理，而「我的已办」恰恰全是已结束的任务。
/// </para>
/// <para>
/// 列语义与 Activiti 一致，写入时机见 <see cref="NetBpm.Engine.Tasks.IdentityLinkRecord"/> 的说明：
/// <list type="bullet">
///   <item><description>候选用户 / 候选组 → 任务级 <c>TYPE_ = 'candidate'</c> 行；</description></item>
///   <item><description>办理人 / 所有者 / 候选用户 → 流程实例级 <c>TYPE_ = 'participant'</c> 行；</description></item>
///   <item><description><b>只增不删</b>（Activiti 删除身份关系时传 <c>cascadeHistory = false</c>）。</description></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="Id"><c>ID_</c>（与 <c>ACT_RU_IDENTITYLINK.ID_</c> 相同）。</param>
/// <param name="Type"><c>TYPE_</c>（<c>candidate</c> / <c>participant</c> / <c>assignee</c> / <c>owner</c>）。</param>
/// <param name="UserId"><c>USER_ID_</c>。</param>
/// <param name="GroupId"><c>GROUP_ID_</c>。</param>
/// <param name="TaskId"><c>TASK_ID_</c>；流程实例级关系为空。</param>
/// <param name="ProcessInstanceId">
/// <c>PROC_INST_ID_</c>；<b>已按 <c>TASK_ID_</c> 解析过</b>。
/// 任务级候选行的 <c>PROC_INST_ID_</c> 本来是空的（候选组更是只有任务级行），
/// 读端口统一 left join <c>ACT_HI_TASKINST</c> 补齐——否则「我所在组待批的单据」会被整类漏掉。
/// </param>
public sealed record HistoricIdentityLink(
    string Id,
    string Type,
    string? UserId,
    string? GroupId,
    string? TaskId,
    string? ProcessInstanceId);

/// <summary>
/// 历史身份关系的<b>服务端下推</b>过滤条件。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>HistoricTaskFilter</c> 同一口径：只暴露「能触发索引」的条件，不给
/// <c>ACT_HI_IDENTITYLINK</c> 做任意组合 SQL。
/// </para>
/// <para>
/// <paramref name="UserId"/> 与 <paramref name="GroupIds"/> 之间是 <b>OR</b> 关系
/// （「我本人」或「我所属的组」命中即算参与）；其余条件是 AND。
/// </para>
/// </remarks>
/// <param name="UserId">按用户 id 过滤（可为 <see langword="null"/>）。</param>
/// <param name="GroupIds">按组 id 集合过滤（可为空；为空时不参与 OR）。</param>
/// <param name="ProcessInstanceId">按流程实例 id 过滤。</param>
/// <param name="TaskId">按任务 id 过滤。</param>
/// <param name="Type">按关系类型过滤（<c>candidate</c> / <c>participant</c> …）。</param>
/// <param name="FirstResult">跳过的条数。</param>
/// <param name="MaxResults">最多返回的条数；<see langword="null"/> 表示不限。</param>
public sealed record HistoricIdentityLinkFilter(
    string? UserId = null,
    IReadOnlyList<string>? GroupIds = null,
    string? ProcessInstanceId = null,
    string? TaskId = null,
    string? Type = null,
    int FirstResult = 0,
    int? MaxResults = null);
