using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

// 说明：Demo 的 HTTP 契约只写「请假业务自己的形状」，流程相关的通用形状（表单属性 / 待办 / 请求体）
// 直接复用 NetBpm.Workflow 的类型 —— 它们是同一套东西，再抄一份只会两边漂移。

/// <summary>流程实例视图（一次请求拿全：变量 + 当前待办 + 表单 + 审批结论 + 流程参数）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Running">是否仍在运行（结束后运行态行会被删除，只剩业务数据）。</param>
/// <param name="Variables">流程变量。</param>
/// <param name="Tasks">当前待办（含表单定义与审批选项）。</param>
/// <param name="Result">审批结论（业务数据）；未结束为 <see langword="null"/>。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="Base">业务基础模型（按模型解读过的视图）。</param>
/// <param name="Parameters">流程参数表内容（键 = 参数名，如 <c>days</c> / <c>reason</c>）。</param>
public sealed record InstanceView(
    string ProcessInstanceId,
    string? BusinessKey,
    string ProcessDefinitionId,
    bool Running,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<WorkflowTaskView> Tasks,
    LeaveResultRecord? Result,
    DateTime? StartedAt,
    BaseModel Base,
    IReadOnlyDictionary<string, string?> Parameters);

/// <summary>流程实例摘要（列表用）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="Applicant">发起人（域账号）。</param>
/// <param name="ApplicantName">发起人姓名（组织架构表联查；查不到为空）。</param>
/// <param name="Days">请假天数（从流程参数 <c>days</c> 取）。</param>
/// <param name="Reason">请假事由（从流程参数 <c>reason</c> 取）。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="Status">Running / Approved / Refused。</param>
/// <param name="Outcome">结论文本（如「审批通过」）。</param>
/// <param name="DeptName">申请部门名称（按 applyDeptId 查组织架构；查不到为空）。</param>
public sealed record InstanceSummary(
    string ProcessInstanceId,
    string? BusinessKey,
    string Applicant,
    string ApplicantName,
    int Days,
    string? Reason,
    DateTime? StartedAt,
    string Status,
    string? Outcome,
    string? DeptName);

/// <summary>
/// 业务数据：审批结论。由服务任务 <c>leave.ApproveComplete</c> / <c>leave.RefuseComplete</c> 写入。
/// </summary>
/// <remarks>
/// 应用层给的是通用的 <see cref="WorkflowOutcomeRecord"/>（参数放在 <c>Parameters</c> 里）；
/// 这里把 <c>days</c> / <c>reason</c> 摊成自己的两列 —— 请假单表本来就有这两列，
/// 落库时也应当是列而不是字典。
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Base">业务基础模型（含审批人提交的 <c>result</c> / <c>comment</c>）。</param>
/// <param name="Days">请假天数。</param>
/// <param name="Reason">请假事由。</param>
/// <param name="Outcome">Approved / Refused。</param>
/// <param name="ActivityId">产生该结论的节点 id。</param>
/// <param name="ActivityName">产生该结论的节点名。</param>
/// <param name="CompletedAt">写入时间。</param>
public sealed record LeaveResultRecord(
    string ProcessInstanceId,
    BaseModel Base,
    int Days,
    string? Reason,
    string Outcome,
    string ActivityId,
    string ActivityName,
    DateTime CompletedAt)
{
    /// <summary>把应用层的通用结论摊成请假单自己的结论。</summary>
    /// <param name="record">应用层结论；为 <see langword="null"/> 时返回 <see langword="null"/>。</param>
    public static LeaveResultRecord? From(WorkflowOutcomeRecord? record) => record is null
        ? null
        : new LeaveResultRecord(
            record.ProcessInstanceId,
            record.Base,
            Days: LeaveParameterNames.DaysOf(record.Parameters),
            Reason: LeaveParameterNames.ReasonOf(record.Parameters),
            Outcome: record.Outcome,
            ActivityId: record.ActivityId,
            ActivityName: record.ActivityName,
            CompletedAt: record.CompletedAt);
}

/// <summary>
/// 业务数据：<b>流程参数表</b>的一行。
/// </summary>
/// <remarks>
/// <para>
/// 参数表按「实例 × 参数名」展开成行，而不是把整个字典塞进一个列里 ——
/// 这样既能按参数名查到单个值，也方便后续换真库时直接建唯一索引
/// （<c>(PROCESS_INSTANCE_ID, NAME)</c>）。同一个实例同一个参数名只有一行：更新即覆盖。
/// </para>
/// <para>
/// ⚠️ 它<b>不是</b>引擎的 <c>ACT_RU_VARIABLE</c>：后者是运行期变量、流程一结束就清；
/// 参数表是业务侧留痕，流程结束后仍要能查到。
/// </para>
/// </remarks>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Name">参数名（如 <c>days</c>）。</param>
/// <param name="Value">参数值（字符串形式；<see langword="null"/> 表示显式置空）。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
public sealed record ProcessParameterRecord(
    string ProcessInstanceId,
    string Name,
    string? Value,
    DateTime UpdatedAt);

/// <summary>
/// 业务数据文件（Demo 直接以 JSON 落盘）。
/// <para>
/// 真实项目里这是「请假单表 + 审批结论表 + 流程参数表 + 审批记录表 + 当前待办表」，
/// Demo 用一个 JSON 文件替代，好处是零依赖、且业务数据本身可读可核对
/// （见 <see cref="LeaveBusinessStore.FilePath"/>）。
/// </para>
/// <para>
/// 🔑 五个区块与 MySQL 实现落的表是<b>一一对应</b>的：<c>Instances</c> / <c>Results</c> → <c>instances</c>，
/// <c>Parameters</c> → <c>leave_bill</c> 的 <c>days</c> / <c>reason</c> / <c>ext_params</c>，
/// <c>Approvals</c> → <c>deal_history</c>，<c>CurrentTasks</c> → <c>instance_current_task</c>。
/// 换 <c>Leave:Store</c> 时数据形状不变，变的只是落点。
/// </para>
/// </summary>
public sealed record LeaveBusinessDocument
{
    /// <summary>已发起的请假单。</summary>
    public IReadOnlyList<LeaveInstanceRecord> Instances { get; init; } = [];

    /// <summary>审批结论（流程走完后产生）。</summary>
    public IReadOnlyList<LeaveResultRecord> Results { get; init; } = [];

    /// <summary>流程参数表（实例 × 参数名，每组合一行）。</summary>
    public IReadOnlyList<ProcessParameterRecord> Parameters { get; init; } = [];

    /// <summary>审批记录（每次提交一条，最旧的在前）—— 审批历史的数据源。</summary>
    public IReadOnlyList<WorkflowApprovalRecord> Approvals { get; init; } = [];

    /// <summary>
    /// 当前待办（实例 × 环节）—— <b>流程每前进一步整体替换一次</b>，实例结束时清空。
    /// </summary>
    /// <remarks>
    /// 真实系统里这是 <c>instance_current_task</c> 表；JSON 实现直接把它当一个区块存下来，
    /// 语义与落库完全一致（见 <see cref="IWorkflowDataStore.ReplaceCurrentTasksAsync"/>）。
    /// </remarks>
    public IReadOnlyList<WorkflowCurrentTaskRecord> CurrentTasks { get; init; } = [];
}

/// <summary>业务数据：已发起的请假单（Demo 侧登记，便于列表展示）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="Base">业务基础模型（发起时写入的五个字段）。</param>
/// <param name="Days">请假天数。</param>
/// <param name="Reason">请假事由。</param>
public sealed record LeaveInstanceRecord(
    string ProcessInstanceId,
    BaseModel Base,
    int Days,
    string? Reason);

/// <summary>统一错误响应。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">明细（如表单字段校验错误）。</param>
public sealed record LeaveErrorResponse(string Message, IReadOnlyList<string>? Errors = null);
