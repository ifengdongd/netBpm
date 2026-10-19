namespace NetBpm.Workflow;

/// <summary>
/// 业务侧的流程实例登记（宿主自己那张「单据表」）。
/// </summary>
/// <remarks>
/// 引擎的运行态行在流程结束后会被删除，而业务单子要长期可查；此外服务任务跑在子执行上，
/// 读不到流程实例那一列的 <c>BUSINESS_KEY_</c> —— 所以业务上下文记在宿主自己的登记里最可靠。
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Base">业务基础模型（发起时写入，审批过程可继续补 <c>result</c> / <c>comment</c>）。</param>
public sealed record WorkflowInstanceRecord(
    string ProcessInstanceId,
    BaseModel Base);

/// <summary>
/// 业务侧的审批结论（流程走完后产生）。
/// </summary>
/// <remarks>
/// <para>
/// 谁产生它：宿主在流程结束的那个服务任务里调用 <c>IWorkflowService.CompleteAsync</c>。
/// 引擎只知道「走到了哪个节点」，是宿主把「走到这个节点意味着审批通过」翻译成业务结论。
/// </para>
/// <para>
/// 为什么带 <see cref="Parameters"/>：结论一旦落盘，那些参数（请假天数之类）在流程结束后
/// 就再也读不到了（运行态变量已清），快照在这里比事后去猜可靠。
/// </para>
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Base">业务基础模型（<c>result</c> / <c>comment</c> 就是审批人提交的那两个值）。</param>
/// <param name="Outcome">结论机器名，见 <see cref="WorkflowOutcomes"/>。</param>
/// <param name="ActivityId">产生该结论的节点 id。</param>
/// <param name="ActivityName">产生该结论的节点名。</param>
/// <param name="CompletedAt">产生时间。</param>
/// <param name="Parameters">完成时的流程参数快照。</param>
public sealed record WorkflowOutcomeRecord(
    string ProcessInstanceId,
    BaseModel Base,
    string Outcome,
    string ActivityId,
    string ActivityName,
    DateTime CompletedAt,
    IReadOnlyDictionary<string, string?> Parameters);

/// <summary>
/// 一条审批记录 —— 「谁在哪个环节、提交了什么、流转到哪儿」。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>IWorkflowService.SubmitAsync</c> 在每次提交审批后写入。它<b>不依赖引擎历史</b>，
/// 因此内存宿主照样有完整的审批轨迹（见 <see cref="WorkflowStepSource.BusinessApprovals"/>）。
/// </para>
/// <para>
/// ⚠️ <b>有几个字段是「宿主能给就给」的</b>，真实业务表未必存得下，缺了不影响功能：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <see cref="TaskId"/> / <see cref="ActivityId"/> —— 引擎任务与节点 id。
///   业务表常常只记「环节名」（如 <c>deal_history.task_node</c>），那时应用层会退化为
///   <b>按环节名 + 先后顺序</b>把审批记录认领回引擎环节（见 <c>WorkflowHistoryBuilder</c>）；
///   </description></item>
///   <item><description>
///   <see cref="Values"/> —— 本次提交的表单值快照。<c>deal_history</c> 这类表没有对应列，
///   此时只还原得出 <c>result</c> / <c>comment</c>（它们本来就是列）。
///   </description></item>
/// </list>
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="TaskId">被提交的任务 id；宿主没存时为空串。</param>
/// <param name="ActivityId">环节 id（<c>taskDefinitionKey</c>）。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="FormKey">环节上的表单标识（BPMN 的 <c>activiti:formKey</c>）。</param>
/// <param name="Assignee">办理人（请求里的 <c>operator</c> 优先，其次任务上的 <c>assignee</c>）。</param>
/// <param name="Decision">审批结果选项 id（如 <c>"1"</c>）；非审批环节为 <see langword="null"/>。</param>
/// <param name="DecisionName">审批结果中文名（如 <c>通过</c>）。</param>
/// <param name="Comment">审批说明（表单字段 <c>comment</c>）。</param>
/// <param name="Values">本次提交的表单值快照（键 = 变量名，值按文本收）。</param>
/// <param name="NextSteps">提交后流程停到了哪些环节（实例结束则为空）。</param>
/// <param name="Outcome">若这次提交让实例结束，记下结论机器名；否则为 <see langword="null"/>。</param>
/// <param name="SubmittedAt">提交时间。</param>
public sealed record WorkflowApprovalRecord(
    string ProcessInstanceId,
    string TaskId,
    string? ActivityId,
    string? ActivityName,
    string? FormKey,
    string? Assignee,
    string? Decision,
    string? DecisionName,
    string? Comment,
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyList<WorkflowActivityRef> NextSteps,
    string? Outcome,
    DateTime SubmittedAt);

/// <summary>
/// 一条「当前待办」—— 实例此刻停在哪个环节、这一步该谁办。
/// </summary>
/// <remarks>
/// <para>
/// 「该谁办」有两个层次，别混：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <see cref="CandidateUsers"/> / <see cref="CandidateGroups"/> 是<b>流程定义里写的候选关系</b>
///   （引擎的行数据，任务被认领后也不变）；
///   </description></item>
///   <item><description>
///   <see cref="Approvers"/> 是<b>解析后的审批人</b>：任务已经指派给人就是那个人，
///   否则由候选用户 + 候选组展开出成员。这一步要查身份模块，所以由应用层解析好再落下来。
///   </description></item>
/// </list>
/// <para>
/// ⚠️ 候选组展开<b>可能得到空列表</b>（权限体系不给成员，或组里确实没人）——
/// 那表示「审批人未知」，不是错误，宿主不要因此拒绝写入。
/// </para>
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="TaskId">任务 id（引擎 <c>ACT_RU_TASK.ID_</c>）。</param>
/// <param name="ActivityId">环节 id（<c>taskDefinitionKey</c>）。</param>
/// <param name="ActivityName">环节名。</param>
/// <param name="Approvers">解析出的审批人（已去重、已排序，便于稳定落库）。</param>
/// <param name="CandidateUsers">流程定义里的候选用户。</param>
/// <param name="CandidateGroups">流程定义里的候选组。</param>
/// <param name="FormKey">环节对应的表单标识。</param>
/// <param name="CreatedAt">任务创建时间。</param>
public sealed record WorkflowCurrentTaskRecord(
    string ProcessInstanceId,
    string TaskId,
    string? ActivityId,
    string? ActivityName,
    IReadOnlyList<string> Approvers,
    IReadOnlyList<string> CandidateUsers,
    IReadOnlyList<string> CandidateGroups,
    string? FormKey,
    DateTime? CreatedAt);

/// <summary>
/// 业务数据端口 —— 应用层不假设宿主用什么存业务数据（JSON 文件 / 关系库 / 内存都行）。
/// </summary>
/// <remarks>
/// <para>
/// 三块数据，与「引擎侧」泾渭分明：
/// </para>
/// <list type="bullet">
///   <item><description><b>实例登记</b>：业务单据本身（<see cref="WorkflowInstanceRecord"/>）；</description></item>
///   <item><description><b>审批结论</b>：流程结束后的业务结论（<see cref="WorkflowOutcomeRecord"/>）；</description></item>
///   <item><description><b>流程参数表</b>：按「实例 × 参数名」展开的业务参数（<c>days</c> / <c>city</c> …）。</description></item>
///   <item><description><b>审批记录</b>：每次提交一条（<see cref="WorkflowApprovalRecord"/>）。</description></item>
/// </list>
/// <para>
/// ⚠️ 流程参数表<b>不是</b> <c>ACT_RU_VARIABLE</c>：后者是引擎运行期变量、流程一结束就清；
/// 参数表是业务侧留痕，流程结束后仍要能查到。
/// </para>
/// </remarks>
public interface IWorkflowDataStore
{
    /// <summary>按流程实例 id 查业务登记；没有返回 <see langword="null"/>。</summary>
    ValueTask<WorkflowInstanceRecord?> FindInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>查全部业务登记（新的在前，实现方自行决定排序）。</summary>
    ValueTask<IReadOnlyList<WorkflowInstanceRecord>> FindInstancesAsync(CancellationToken ct = default);

    /// <summary>写入 / 覆盖业务登记（按流程实例 id）。</summary>
    ValueTask SaveInstanceAsync(WorkflowInstanceRecord record, CancellationToken ct = default);

    /// <summary>按流程实例 id 查审批结论；没有返回 <see langword="null"/>。</summary>
    ValueTask<WorkflowOutcomeRecord?> FindResultAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>写入 / 覆盖审批结论（按流程实例 id，重复写入取最后一次）。</summary>
    ValueTask SaveResultAsync(WorkflowOutcomeRecord record, CancellationToken ct = default);

    /// <summary>读某实例的流程参数（键 = 参数名）。</summary>
    ValueTask<IReadOnlyDictionary<string, string?>> FindParametersAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 按 key 覆盖写入流程参数。
    /// <para>
    /// 只动 <paramref name="values"/> 里提到的参数名，其余行保持原样 ——
    /// 这正是「有值就按照 key 更新 value」的落点。
    /// 值为 <see langword="null"/> 表示显式置空（写成 null，而不是删行）。
    /// <paramref name="values"/> 为空时应当直接返回，不做任何写入。
    /// </para>
    /// </summary>
    ValueTask SaveParametersAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken ct = default);

    /// <summary>读某实例的审批记录（<b>按提交先后</b>返回，最旧的在前）。</summary>
    ValueTask<IReadOnlyList<WorkflowApprovalRecord>> FindApprovalsAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>追加一条审批记录。</summary>
    ValueTask SaveApprovalAsync(WorkflowApprovalRecord record, CancellationToken ct = default);

    /// <summary>读某实例的当前待办（实例已结束或没有待办时返回空列表）。</summary>
    ValueTask<IReadOnlyList<WorkflowCurrentTaskRecord>> FindCurrentTasksAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 用 <paramref name="tasks"/> <b>整体替换</b>某实例的当前待办。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么是「替换」而不是「增删」：流程每前进一步，待办都会被上一组作废、换上新的一组 ——
    /// 让调用方一条条去 diff 既啰嗦又容易漏（走到结束时要删干净，那是 <paramref name="tasks"/> 为空的情形）。
    /// </para>
    /// <para>
    /// ⚠️ 传空列表是<b>正常操作</b>，表示实例已结束、当前没有待办，实现方应当把该实例的行全部清掉。
    /// </para>
    /// </remarks>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="tasks">该实例新的当前待办全集（可以为空）。</param>
    /// <param name="ct">取消标记。</param>
    ValueTask ReplaceCurrentTasksAsync(
        string processInstanceId,
        IReadOnlyList<WorkflowCurrentTaskRecord> tasks,
        CancellationToken ct = default);
}
