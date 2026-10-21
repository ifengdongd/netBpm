namespace NetBpm.Runtime;

/// <summary>
/// 任务查询条件。
/// <para>
/// ⚠️ <b>必须至少给一个条件</b>：本子集不提供「查全部任务」——
/// 全表扫描在真实部署里是事故源，因此返回空/报错而不是全量。
/// 例外见 <paramref name="AllUsers"/>：管理视图（实例监控的全量待办统计）显式声明后才放行。
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
/// <param name="FirstResult">
/// 跳过的条数（对应 Activiti <c>TaskQuery.listPage(firstResult, maxResults)</c> 的 <c>firstResult</c>），默认 0。
/// </param>
/// <param name="MaxResults">
/// 最多返回的条数；<see langword="null"/> 表示不限（对应 Activiti 的 <c>maxResults = 0</c> 语义）。
/// </param>
/// <param name="ProcessDefinitionId">
/// 按流程定义过滤（对应 <c>TaskQuery.processDefinitionId</c>）。
/// <para>⚠️ <b>附加过滤条件，不能单独使用</b>：单独的「某定义下全部待办」等于全表扫描，仍要求
/// <c>assignee</c> / <c>candidateUser</c> / <c>processInstanceId</c> 至少给一个。</para>
/// </param>
/// <param name="TaskDefinitionKey">
/// 按节点 id 过滤（对应 <c>TaskQuery.taskDefinitionKey</c>，即 BPMN 里 <c>userTask/@id</c>）。
/// <para>与 <paramref name="ProcessDefinitionId"/> 同样只能作为附加过滤。常用于"我的待办里只看审批节点"。</para>
/// </param>
/// <param name="CandidateGroup">
/// 按候选组查（对应 <c>TaskQuery.taskCandidateGroup</c>）：未签收且任务级身份关系里有该组。
/// 与 <paramref name="CandidateUser"/> 同时给出时取交集（用户可代办、且任务对组可见）。
/// </param>
/// <param name="Suspended">
/// 按挂起状态过滤（<see langword="null"/> 不限制）。挂起的任务仍会出现在待办里，但不能完成 / 认领。
/// </param>
/// <param name="DueBefore">到期时间上界（含），用于逾期 / 临近超时筛选。</param>
/// <param name="SortBy">
/// 排序字段：<c>createTime</c>（默认）/ <c>dueDate</c> / <c>priority</c>。
/// 非法值显式报错（400），不静默回退。
/// </param>
/// <param name="Descending">是否倒序（默认正序，与 Activiti 的 <c>orderBy...asc()</c> 同义）。</param>
/// <remarks>
/// <para>
/// <b>分页口径</b>：先按 (CREATE_TIME_ 升序, ID_ 升序) 排序再切片 —— 排序稳定才能保证
/// 「第 2 页不会重复第 1 页的待办」。Activiti 由 SQL 的 <c>order by</c> + <c>limit</c> 完成，
/// 本子集在内存里做（与交集同口径，见 <paramref name="ProcessInstanceId"/>），
/// 因此分页<b>不降低</b>查询代价，只是降低返回量。总数用 <c>IProcessEngine.CountTasksAsync</c>。
/// </para>
/// <para>非法取值（负数 / 0 条）由门面显式报错，不静默忽略。</para>
/// </remarks>
public sealed record TaskQuery(
    string? Assignee = null,
    string? CandidateUser = null,
    string? ProcessInstanceId = null,
    int FirstResult = 0,
    int? MaxResults = null,
    string? ProcessDefinitionId = null,
    string? TaskDefinitionKey = null,
    string? CandidateGroup = null,
    bool? Suspended = null,
    DateTime? DueBefore = null,
    string? TenantId = null,
    bool AllUsers = false,
    string? SortBy = null,
    bool Descending = false)
{
    /// <summary>允许的排序字段。</summary>
    public static readonly string[] AllowedSortFields = ["createTime", "dueDate", "priority"];

    /// <summary>是否至少给了一个查询条件。</summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Assignee)
        || !string.IsNullOrWhiteSpace(CandidateUser)
        || !string.IsNullOrWhiteSpace(CandidateGroup)
        || !string.IsNullOrWhiteSpace(ProcessInstanceId);

    /// <summary>
    /// 是否放行「无条件的全量待办」。只应由 REST 层在确认调用方具备管理能力
    /// （开发模式 / API Key / 持有 <c>netbpm.admin</c>）后设置 ——
    /// 实例监控的全量待办统计是唯一合法用途；防呆默认仍然生效。
    /// </summary>
    public bool AllowsUnfiltered => AllUsers;
}
