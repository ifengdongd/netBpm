using NetBpm.Demo.Approval;
using NetBpm.Demo.Common;
using NetBpm.Demo.Dept;
using NetBpm.Demo.ExternalTasks;
using NetBpm.Demo.Leave;
using NetBpm.Demo.Order;
using NetBpm.Demo.Storage;
using NetBpm.Demo.Workflow;
using NetBpm.Runtime;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

// ─────────────────────────── 组合根 ───────────────────────────
// 职责只有三件事：注册框架与存储、映射端点、执行启动序列。
// 细节都在各自的文件夹里（见 samples/NetBpm.Demo/README.md 的目录结构）。

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// JSON 序列化保持 C# 属性原名（PascalCase）：wwwroot 页面脚本按 PascalCase 取值，
// 请求反序列化默认大小写不敏感，前端传 camelCase 的键（userId / targetActivityId…）照样能绑上。
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);

// 引擎：落真库（ACT_* 表，方言随 NetBpm:DatabaseType）—— 必须与业务数据一致（内存执行树会与真库 instances 撞号）
builder.Services.AddNetBpm(builder.Configuration);

// 定时边界事件：后台轮询 ACT_RU_TIMER_JOB（NetBpm:Timer 可调间隔 / 关闭）
builder.Services.AddNetBpmTimerScheduler(builder.Configuration);

// 业务数据存储：流程数据（四张视图表）方言随 NetBpm:DatabaseType；订单行与组织架构用示例自带内存实现
//（平台化之后业务单表 / 组织主数据都归接入方自己的库，平台脚本不再建）
builder.Services.AddDemoStores(builder.Configuration);

// 业务流程接入：新建业务流程只需实现 IBusinessProcess（+ 两个收口委托 + BPMN + workflow_type 行）
builder.Services.AddSingleton<IBusinessProcess, OrderBusinessProcess>();
builder.Services.AddSingleton<IBusinessProcess, LeaveBusinessProcess>();
builder.Services.AddSingleton<IBusinessProcess, DeptApprovalBusinessProcess>();

// 动态审批人解析器：节点「未静态指定审批人」时，按 applyDeptId（申请部门）+ 节点 id（职位）
// 查外部系统定位审批人（见 Approval/DynamicApproverResolver.cs）。
// 这里用 DemoExternalApproverDirectory 当「假外部系统」；真实场景换成 HTTP/SQL 查询。
builder.Services.AddNetBpmApproverResolver(new DynamicApproverResolver(DemoExternalApproverDirectory.FindAsync));

// 身份与组织架构：InMemoryOrgDirectory 内置演示数据（两种方言一致 —— 平台不持有组织主数据，
// 原先读 sys_* 四张表的 MySqlOrgDirectory 已随这些表下线删除）。
// 它同时实现 IOrgDirectory（业务查询）与 IUserGroupManager（引擎查候选组待办的「用户 ↔ 组」展开），
// 数据语义：请假审批人 = candidateGroups="leaders" → leader1（赵志强）；订单审批人 = managers → manager1（孙建国）。

// 监听器触发计数（观测用单例）：请假流程的监听器往里计数
builder.Services.AddSingleton<ListenerStats>();

// 服务任务委托 —— 与两条 BPMN 里的 activiti:delegateExpression 一一对应（见 Delegates/ 文件夹）：
//   order.ApproveComplete → 订单状态 100（审批通过）
//   order.RefuseComplete  → 订单状态 -1（审批拒绝）
//   leave.ApproveComplete / leave.RefuseComplete → 请假审批结论落盘
// 名字写在委托类上（[ServiceTaskName]），AddServiceTaskDelegates 扫一遍程序集登记成 keyed 服务。
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);

// 外部任务（activiti:topic）—— 「注册」在这里，与委托注册互相独立：
//   PriceAuditHandler        → demo.priceAudit        审计：写回 auditPassed / taxAmount
//   PriceArchiveHandler      → demo.priceAuditArchive 归档：挂在「审计通过」分支上
// 内置 worker 在进程内领取并回执（真实部署应换成 samples/NetBpm.ExternalWorker 那样的独立 HTTP worker）。
builder.Services.AddSingleton<IExternalTaskHandler, PriceAuditHandler>();
builder.Services.AddSingleton<IExternalTaskHandler, PriceArchiveHandler>();
builder.Services.AddSingleton<DemoExternalTaskWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DemoExternalTaskWorker>());
builder.Services.AddScoped<ExternalTaskCaseRunner>();

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    demo = "merged-approval",
    processes = new[] { ConstField.OrderProcessKey, ConstField.LeaveProcessKey, ConstField.DeptProcessKey },
}));

// 公共端点：流程信息枚举、跨流程的待办中心 / 申请单列表 / 组织架构
app.MapProcessEndpoints();
app.MapTaskCenterEndpoints();

// 两条流程各自的业务端点（/api/order/*、/api/leave/*）
app.MapOrderEndpoints();
app.MapLeaveEndpoints();

// 部门审批流程的业务端点（/api/dept/*，演示「审批人按部门从外部系统动态定位」）
app.MapDeptEndpoints();

// 外部任务 Demo：GET /api/external-task/handlers（注册信息）、
// POST /api/external-task/case/run（跑一遍「注册 + 调用」闭环用例并返回分步断言）
app.MapExternalTaskCaseEndpoints();

// 每个业务流程的统一业务入口：POST {ApiPrefix}/business/start
// （PrepareStart 业务处理 → 发起流程 → OnStartedAsync 收尾）
foreach (var process in app.Services.GetServices<IBusinessProcess>())
{
    app.MapBusinessProcess(process);
}

// 启动序列：部署两条流程 → 从 workflow_type 表加载流程信息 → 发布流程图图片
//（⚠️ 必须在 app.Run() 之前：运行期上下文与流程信息枚举要在接受请求前装好）
await StartupInitializer.InitializeAsync(app);

app.Logger.LogInformation(
    "合并审批 Demo 已就绪：{Urls}（OpenAPI 见 /openapi/v1.json）",
    builder.Configuration["urls"] ?? "见下方 Kestrel 监听地址");

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
