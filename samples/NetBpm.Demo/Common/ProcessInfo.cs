namespace NetBpm.Demo.Common;

/// <summary>
/// 流程信息 —— 一条流程在宿主里的「名片」。
/// </summary>
/// <param name="Id">流程 id（设计器的 modelId，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）。</param>
/// <param name="Name">流程名称（= BPMN 里 <c>process/@id</c>，即流程 key，如 order_process）。</param>
/// <param name="Describe">流程描述（如「订单审批流程」）。</param>
/// <param name="ViewUrl">
/// 业务单查看页地址（<b>审批结束后查看业务单</b>）。占位符：
/// <c>{businessId}</c>（业务单号）与 <c>{processInstanceId}</c>（流程实例 id），由前端按需替换。
/// </param>
/// <param name="ApproveUrl">
/// 流程审批页地址（<b>审批过程中的业务单审批页面</b>）。占位符：
/// <c>{taskId}</c>（待办任务 id）与 <c>{processInstanceId}</c>（流程实例 id），由前端按需替换。
/// </param>
public sealed record ProcessInfo(
    string Id,
    string Name,
    string Describe,
    string ViewUrl,
    string ApproveUrl)
{
    /// <summary>用业务单号渲染查看地址。</summary>
    public string ResolveViewUrl(string businessId) => Fill(ViewUrl, new()
    {
        ["businessId"] = businessId,
    });

    /// <summary>用待办 id 渲染审批地址。</summary>
    public string ResolveApproveUrl(string taskId) => Fill(ApproveUrl, new()
    {
        ["taskId"] = taskId,
    });

    /// <summary>替换模板里的 <c>{占位符}</c>；没给的保持原样。</summary>
    public static string Fill(string template, Dictionary<string, string> values) =>
        System.Text.RegularExpressions.Regex.Replace(
            template,
            @"\{(\w+)\}",
            match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
}

/// <summary>
/// 流程信息枚举 —— 本宿主承载的全部流程。
/// </summary>
/// <remarks>
/// <para>
/// C# 的 enum 装不下字符串字段（id / describe / url），所以用「静态只读实例 + All 清单」的
/// 形状当枚举用：新增一条流程 = 在 <see cref="All"/> 里加一行，端点与页面全部自动跟着走。
/// </para>
/// <para>
/// 页面不写死任何流程：首页的流程卡片、待办列表的所属流程列、审批/查看页的跳转地址，
/// 全部从 <c>GET /api/processes</c> 拿这份清单。
/// </para>
/// </remarks>
public static class ProcessInfoEnum
{
    /// <summary>订单审批流程。</summary>
    public static readonly ProcessInfo Order = new(
        Id: "02830358-46ef-40e5-b3b9-4d5c4eb23f9f",
        Name: NetBpm.Demo.Order.OrderProcessBootstrapper.ProcessKey,
        Describe: "订单审批流程",
        ViewUrl: "/bill.html?process=order_process&businessId={businessId}",
        ApproveUrl: "/approve.html?process=order_process&taskId={taskId}");

    /// <summary>请假申请流程。</summary>
    public static readonly ProcessInfo Leave = new(
        Id: "3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7",
        Name: NetBpm.Demo.Leave.LeaveProcessBootstrapper.ProcessKey,
        Describe: "请假申请流程",
        ViewUrl: "/bill.html?process=leave_process&businessId={businessId}",
        ApproveUrl: "/approve.html?process=leave_process&taskId={taskId}");

    /// <summary>全部流程（清单顺序即页面展示顺序）。</summary>
    public static IReadOnlyList<ProcessInfo> All { get; } = [Order, Leave];

    /// <summary>按流程名称（key）查一条；查不到返回 <see langword="null"/>。</summary>
    public static ProcessInfo? Of(string? name) =>
        All.FirstOrDefault(process => string.Equals(process.Name, name, StringComparison.Ordinal));
}
