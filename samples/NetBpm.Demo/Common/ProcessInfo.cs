namespace NetBpm.Demo.Common;

/// <summary>
/// 流程信息 —— 一条流程在宿主里的「名片」。
/// </summary>
/// <param name="Id">流程类型 id（设计器的 modelId，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）。</param>
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
    public static string Fill(string template, System.Collections.Generic.Dictionary<string, string> values) =>
        System.Text.RegularExpressions.Regex.Replace(
            template,
            @"\{(\w+)\}",
            match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
}

/// <summary>
/// 流程信息枚举 —— 本宿主承载的全部流程（<b>数据源：workflow_type 表</b>）。
/// </summary>
/// <remarks>
/// <para>
/// C# 的 enum 装不下字符串字段（id / describe / url），所以用「记录 + 注册表」的形状当枚举用：
/// 数据在<b>系统启动时从 workflow_type 表加载</b>（见 <see cref="Load"/>，新增流程 = 表里插一行）；
/// 表不存在或加载失败时回退 <see cref="Defaults"/>（内置两条演示流程）。
/// </para>
/// <para>
/// 页面不写死任何流程：首页的流程卡片、待办列表的所属流程列、审批/查看页的跳转地址，
/// 全部从 <c>GET /api/processes</c> 拿这份清单。
/// </para>
/// </remarks>
public static class ProcessInfoEnum
{
    /// <summary>内置默认（workflow_type 表不存在 / 加载失败 / JSON 演示模式时的兜底）。</summary>
    public static readonly ProcessInfo Order = new(
        Id: "02830358-46ef-40e5-b3b9-4d5c4eb23f9f",
        Name: NetBpm.Demo.Order.OrderProcessBootstrapper.ProcessKey,
        Describe: "订单审批流程",
        ViewUrl: "/bill.html?process=order_process&businessId={businessId}",
        ApproveUrl: "/approve.html?process=order_process&taskId={taskId}");

    /// <summary>内置默认：请假申请流程。</summary>
    public static readonly ProcessInfo Leave = new(
        Id: "3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7",
        Name: NetBpm.Demo.Leave.LeaveProcessBootstrapper.ProcessKey,
        Describe: "请假申请流程",
        ViewUrl: "/bill.html?process=leave_process&businessId={businessId}",
        ApproveUrl: "/approve.html?process=leave_process&taskId={taskId}");

    /// <summary>内置默认清单。</summary>
    public static IReadOnlyList<ProcessInfo> Defaults { get; } = [Order, Leave];

    /// <summary>
    /// 当前清单（⚠️ 声明必须在 Defaults 之后：静态字段按声明顺序初始化）。
    /// </summary>
    private static IReadOnlyList<ProcessInfo> _all = Defaults;

    /// <summary>
    /// 全部流程（清单顺序即页面展示顺序）。数据来自 workflow_type 表（启动时加载）。
    /// </summary>
    public static IReadOnlyList<ProcessInfo> All => _all;

    /// <summary>
    /// <b>系统启动时</b>用 workflow_type 表的内容替换枚举数据。
    /// </summary>
    /// <param name="items">表里的流程信息（按 sort_order 排好序）。</param>
    public static void Load(IReadOnlyList<ProcessInfo> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            throw new InvalidOperationException("workflow_type 表里没有启用的流程信息，请先插入数据。");
        }

        // Name 是流程 key，必须唯一 —— 页面与端点都靠它定位
        var duplicated = items.GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicated.Length > 0)
        {
            throw new InvalidOperationException($"workflow_type 表的流程名称重复：{string.Join(" / ", duplicated)}。");
        }

        _all = [.. items];
    }

    /// <summary>按流程名称（key）查一条；查不到返回 <see langword="null"/>。</summary>
    public static ProcessInfo? Of(string? name) =>
        _all.FirstOrDefault(process => string.Equals(process.Name, name, StringComparison.Ordinal));

    /// <summary>按流程类型 id 查一条；查不到返回 <see langword="null"/>。</summary>
    public static ProcessInfo? ById(string? id) =>
        _all.FirstOrDefault(process => string.Equals(process.Id, id, StringComparison.Ordinal));

    /// <summary>按流程名称（key）取一条；查不到抛异常（宿主配置与代码不一致时尽早暴露）。</summary>
    public static ProcessInfo Require(string name) =>
        Of(name) ?? throw new InvalidOperationException(
            $"流程 '{name}' 不在流程信息枚举里（检查 workflow_type 表数据是否已插入）。");
}
