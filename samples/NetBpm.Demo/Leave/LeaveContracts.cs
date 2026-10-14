using NetBpm.Workflow;

namespace NetBpm.Demo.Leave;

/// <summary>流程实例视图（一次请求拿全：变量 + 当前待办 + 表单 + 审批结论 + 流程参数）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务主键。</param>
/// <param name="ProcessDefinitionId">流程定义 id。</param>
/// <param name="Running">是否仍在运行。</param>
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
/// <param name="CurrentStep">当前环节名（进行中才有）。</param>
/// <param name="Approvers">当前审批人（候选组已展开；进行中才有，来自当前待办登记）。</param>
/// <param name="ApproverNames">当前审批人的「姓名(账号)」显示形式。</param>
/// <param name="TaskId">首个当前待办的任务 id（「改派」的入参；进行中才有）。</param>
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
    string? DeptName,
    string? CurrentStep,
    IReadOnlyList<string> Approvers,
    IReadOnlyList<string> ApproverNames,
    string? TaskId);

/// <summary>
/// 业务数据：审批结论。由服务任务 <c>leave.ApproveComplete</c> / <c>leave.RefuseComplete</c> 写入。
/// </summary>
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

/// <summary>统一错误响应。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">明细（如表单字段校验错误）。</param>
public sealed record LeaveErrorResponse(string Message, IReadOnlyList<string>? Errors = null);
