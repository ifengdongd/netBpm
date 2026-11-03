using System.Text.Json.Serialization;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 业务单据（请假单）。
/// </summary>
/// <remarks>
/// 字段形状刻意贴近真实表 <c>leave_bill</c>：主键是<b>业务单号</b>（流程的 <c>businessKey</c>），
/// <c>process_instance_id</c> 是反向查流程的关联列，<c>status</c> 是幂等的落点。
/// </remarks>
public sealed class LeaveBill
{
    /// <summary>单据号（= 流程实例的 <c>businessKey</c>）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>流程实例 id。</summary>
    [JsonPropertyName("processInstanceId")]
    public string ProcessInstanceId { get; set; } = string.Empty;

    /// <summary>状态：<c>PENDING</c> / <c>APPROVED</c> / <c>REJECTED</c>。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = LeaveBillStatus.Pending;

    /// <summary>申请人。</summary>
    [JsonPropertyName("applicant")]
    public string? Applicant { get; set; }

    /// <summary>请假天数。</summary>
    [JsonPropertyName("days")]
    public int? Days { get; set; }

    /// <summary>事由。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>审批意见。</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>通过时走的通道。</summary>
    [JsonPropertyName("approvedVia")]
    public string? ApprovedVia { get; set; }

    /// <summary>拒绝时走的通道。</summary>
    [JsonPropertyName("rejectedVia")]
    public string? RejectedVia { get; set; }

    /// <summary>创建时间（UTC，ISO 8601）。</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>处理时间（UTC，ISO 8601）。</summary>
    [JsonPropertyName("handledAt")]
    public string? HandledAt { get; set; }
}

/// <summary>单据状态常量。</summary>
public static class LeaveBillStatus
{
    /// <summary>待处理。</summary>
    public const string Pending = "PENDING";

    /// <summary>已通过。</summary>
    public const string Approved = "APPROVED";

    /// <summary>已拒绝。</summary>
    public const string Rejected = "REJECTED";
}

/// <summary>幂等落账的结果。</summary>
/// <param name="Bill">落账后的单据（重复投递时是已有那条）。</param>
/// <param name="Duplicated">是否是重复投递（状态已经是目标值）。</param>
/// <param name="Conflict">是否与既有终态冲突（没覆盖，钉住原状态）。</param>
public sealed record BillUpsert(LeaveBill Bill, bool Duplicated, bool Conflict);

/// <summary>落账时要补写到单据上的业务字段。</summary>
/// <param name="Applicant">申请人。</param>
/// <param name="Days">天数。</param>
/// <param name="Reason">事由。</param>
/// <param name="Comment">审批意见。</param>
/// <param name="Via">处理通道。</param>
public sealed record LeaveBillPatch(
    string? Applicant,
    int? Days,
    string? Reason,
    string? Comment,
    string? Via);

/// <summary>
/// 单据仓储。
/// </summary>
/// <remarks>
/// 真实项目里这就是一段 SQL。这个接口只有「落账」一个动作，
/// 是为了让幂等口径集中在实现里，而不是散落在两个处理器里各写一遍。
/// </remarks>
public interface ILeaveBillStore
{
    /// <summary>
    /// 登记 / 更新单据状态。
    /// </summary>
    /// <param name="businessId">
    /// 业务主键（来自流程实例的 <c>businessKey</c>）—— 单据表的唯一键。
    /// </param>
    /// <param name="processInstanceId">流程实例 id（回写到单据上，便于从单据反查流程）。</param>
    /// <param name="status">目标终态。</param>
    /// <param name="patch">业务字段。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落账结果。</returns>
    Task<BillUpsert> MarkProcessedAsync(
        string businessId,
        string processInstanceId,
        string status,
        LeaveBillPatch patch,
        CancellationToken ct);
}
