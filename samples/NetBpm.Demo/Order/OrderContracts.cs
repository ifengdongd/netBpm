using System.Globalization;
using System.Text.Json;
using NetBpm.Workflow;

namespace NetBpm.Demo.Order;

// ─────────────────────────── 订单状态 ───────────────────────────

/// <summary>
/// 订单状态（<c>order_bill.status</c> 列，与 db/*/0006_create_order_bill.sql 的注释一一对应）。
/// </summary>
public static class OrderBillStatus
{
    /// <summary>审批中 —— 提交申请（发起流程）时写入。</summary>
    public const short UnderReview = 1;

    /// <summary>审批拒绝 —— 流程走到 <c>order.RefuseComplete</c> 时写回。</summary>
    public const short Refused = -1;

    /// <summary>审批通过 —— 流程走到 <c>order.ApproveComplete</c> 时写回。</summary>
    public const short Approved = 100;

    /// <summary>状态码的人话形式（列表与详情展示用）。</summary>
    public static string DisplayName(short status) => status switch
    {
        UnderReview => "审批中",
        Refused => "审批拒绝",
        Approved => "审批通过",
        _ => $"未知({status})",
    };
}

// ─────────────────────────── 流程参数名 ───────────────────────────

/// <summary>
/// 订单流程的业务参数名 —— BPMN 表单字段、流程参数表与 <c>order_bill</c> 真列三处共用这份常量。
/// </summary>
public static class OrderParameterNames
{
    /// <summary>订购产品（<c>order_bill.product_name</c>）。</summary>
    public const string ProductName = "product_name";

    /// <summary>订单金额（<c>order_bill.amount</c>；申请人修改环节的表单字段同名）。</summary>
    public const string Amount = "amount";

    /// <summary>备注（<c>order_bill.remark</c>）。</summary>
    public const string Remark = "remark";
}

// ─────────────────────────── 订单单号 ───────────────────────────

/// <summary>
/// 订单单号（业务主键）—— 校验与发号：「秒级时间戳 &lt;&lt; 20 | 进程内序号」，
/// 单进程内不重复、单调递增。
/// </summary>
/// <remarks>
/// ⚠️ 业务表里 <c>order_bill.business_id</c> 是 <c>bigint</c>，所以单号必须是正整数；
/// 用秒级而不是毫秒级时间戳，是为了把单号压在 JS 的安全整数范围内（&lt; 2^53）。
/// 多实例部署要换成统一发号服务。
/// </remarks>
public static class OrderBusinessKey
{
    private static long _sequence;

    /// <summary>单号是否合法（正整数，且能放进 <c>bigint</c>）。</summary>
    public static bool IsValid(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0;

    /// <summary>发一个新单号（单进程内唯一，单调递增）。</summary>
    public static string Next()
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return ((seconds << 20) | (Interlocked.Increment(ref _sequence) & 0xFFFFF))
            .ToString(CultureInfo.InvariantCulture);
    }
}

// ─────────────────────────── 行与契约 ───────────────────────────

/// <summary>
/// 订单表的一行（与 <c>order_bill</c> 的列一一对应；JSON 演示模式下落
/// <c>data/order-bill.json</c>，换真库时形状不变、变的只是落点）。
/// </summary>
/// <param name="TenantId">多租户 id。</param>
/// <param name="BusinessId">业务 id（订单单号，由业务侧生成，主键）。</param>
/// <param name="ProductName">订购产品（流程参数 <c>product_name</c>）。</param>
/// <param name="Amount">订单金额（流程参数 <c>amount</c>）。</param>
/// <param name="Remark">备注（流程参数 <c>remark</c>）。</param>
/// <param name="Status">状态：1=审批中，-1=审批拒绝，100=审批通过。</param>
/// <param name="ApproveComment">最终审批意见（审批结束时随状态一起写回）。</param>
/// <param name="ApplyUser">申请人。</param>
/// <param name="ApplyDeptId">申请部门。</param>
/// <param name="BusinessDeptId">业务部门。</param>
/// <param name="ProcessInstanceId">流程实例 id（提交申请后回填，用于反查审批进度）。</param>
/// <param name="CreateTime">创建时间。</param>
/// <param name="ModifyTime">最后修改时间。</param>
public sealed record OrderBillRow(
    long TenantId,
    long BusinessId,
    string? ProductName,
    decimal? Amount,
    string? Remark,
    short Status,
    string? ApproveComment,
    string ApplyUser,
    string ApplyDeptId,
    string? BusinessDeptId,
    string? ProcessInstanceId,
    DateTime CreateTime,
    DateTime? ModifyTime)
{
    /// <summary>状态的人话形式。</summary>
    public string StatusName => OrderBillStatus.DisplayName(Status);
}

/// <summary>提交订单申请（创建订单并发起审批流程）。</summary>
/// <param name="Applicant">申请人（域账号，与组织架构演示数据对齐，如 alice）。</param>
/// <param name="ProductName">订购产品。</param>
/// <param name="Amount">订单金额。</param>
/// <param name="Remark">备注。</param>
/// <param name="ApplyDeptId">申请部门 id（默认 101 信息技术部）。</param>
/// <param name="BusinessDeptId">业务部门 id（可选）。</param>
public sealed record CreateOrderBillRequest(
    string Applicant,
    string? ProductName,
    decimal? Amount,
    string? Remark,
    string ApplyDeptId = "101",
    string? BusinessDeptId = null);

/// <summary>订单视图：订单行 + 审批进度（进行中才有当前环节与待办 id）。</summary>
/// <param name="Bill">订单行。</param>
/// <param name="ProcessInstanceId">流程实例 id（尚未发起为 <see langword="null"/>）。</param>
/// <param name="Running">流程是否仍在运行。</param>
/// <param name="CurrentStep">当前环节名（进行中才有）。</param>
/// <param name="TaskId">首个当前待办的任务 id（进行中才有）。</param>
/// <param name="Outcome">审批结论（Approved / Refused；未结束为 <see langword="null"/>）。</param>
public sealed record OrderBillView(
    OrderBillRow Bill,
    string? ProcessInstanceId,
    bool Running,
    string? CurrentStep,
    string? TaskId,
    string? Outcome);

/// <summary>统一错误响应。</summary>
/// <param name="Message">错误说明。</param>
/// <param name="Errors">明细。</param>
public sealed record OrderErrorResponse(string Message, IReadOnlyList<string>? Errors = null);
