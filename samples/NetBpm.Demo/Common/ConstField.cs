namespace NetBpm.Demo.Common;

/// <summary>
/// 流程相关的统一常量 —— ProcessKey、BPMN 资源、部署名、API 前缀、流程图目录与配置键。
/// </summary>
/// <remarks>
/// <para>
/// 这些值都是「多处引用、改一处要跟着动」的契约：流程 key 同时是 BPMN 的
/// <c>process/@id</c>、workflow_type 表的 <c>name</c> 列与端点前缀的取值来源；
/// BPMN 资源名同时是设计器导出文件名与 csproj 的链接目标。集中到这一个类，
/// 新增流程时从这里抄，改名时编译器替我们找出所有遗漏点。
/// </para>
/// </remarks>
public static class ConstField
{
    // ───────────────── 流程 key（= BPMN process/@id；workflow_type.name 同值） ─────────────────

    /// <summary>订单审批流程 key。</summary>
    public const string OrderProcessKey = "order_process";

    /// <summary>请假审批流程 key。</summary>
    public const string LeaveProcessKey = "leave_process";

    // ───────────────── BPMN 资源（设计器导出，csproj 链接进输出目录） ─────────────────

    /// <summary>BPMN 资源在输出目录下的子目录。</summary>
    public const string ProcessResourceDirectory = "processes";

    /// <summary>订单审批流程定义资源名。</summary>
    public const string OrderResourceName = "order-process.bpmn20.xml";

    /// <summary>请假审批流程定义资源名。</summary>
    public const string LeaveResourceName = "leave-process.bpmn20.xml";

    // ───────────────── 部署名（ACT_RE_DEPLOYMENT.NAME_） ─────────────────

    /// <summary>订单审批流程的部署名。</summary>
    public const string OrderDeploymentName = "订单审批 Demo";

    /// <summary>请假审批流程的部署名。</summary>
    public const string LeaveDeploymentName = "请假审批 Demo";

    // ───────────────── HTTP 前缀（流程端点组） ─────────────────

    /// <summary>订单流程的 HTTP 端点前缀。</summary>
    public const string OrderApiPrefix = "/api/order";

    /// <summary>请假流程的 HTTP 端点前缀。</summary>
    public const string LeaveApiPrefix = "/api/leave";

    // ───────────────── 流程图图片（发布即生成，落 wwwroot 下） ─────────────────

    /// <summary>流程图图片在 wwwroot 下的子目录（{流程key}.svg）。</summary>
    public const string DiagramDirectory = "diagrams";

    // ───────────────── 配置键 ─────────────────

    /// <summary>流程参数 / 流程类型表共用的租户 id 配置键。</summary>
    public const string WorkflowTenantConfigKey = "Workflow:TenantId";

    /// <summary>订单行的租户 id 配置键。</summary>
    public const string OrderTenantConfigKey = "Order:TenantId";
}
