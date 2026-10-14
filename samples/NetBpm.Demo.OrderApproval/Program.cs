using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Demo.OrderApproval.Order;
using NetBpm.Identity;
using NetBpm.Runtime;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 组合根：引擎与业务数据各有一套「内存 / 真库」的组合，由配置决定用哪套。
//
//   NetBpm:UseInMemoryExecutionTree = true   → 引擎走内存执行树（零依赖，重启即清）
//                                  = false   → 引擎落 MySQL（ACT_* 表）
//   Workflow:JsonDataFile / Order:BillDataFile → 业务数据落 JSON 文件
//
// ⚠️ 引擎切真库时业务数据要一起换实现（见 LeaveApproval Demo 的 MySqlWorkflowDataStore）：
// 内存宿主的流程实例 id 每次启动都从 1 重新开始，而真库里的记录是累积的 —— 混用会撞号。
builder.Services.AddNetBpm(builder.Configuration);

// 应用层：发起 / 环节信息 / 提交 / 审批历史四件事都由它提供。
// 业务数据端口用公共的 JSON 实现（落 data/order-workflow.json）；订单行另落
// data/order-bill.json（见 OrderBillStore），换真库时两处一起换。
builder.Services.AddNetBpmWorkflow<JsonWorkflowDataStore>();

// 身份与组织架构：InMemoryOrgDirectory 同时实现 IOrgDirectory（业务查询）
// 与 IUserGroupManager（引擎查候选组待办要用的「用户 ↔ 组」展开）—— 同一份数据，两个视角。
// 审批人就是它给的：candidateGroups="managers" → manager1（孙建国）。
builder.Services.AddSingleton<IOrgDirectory, InMemoryOrgDirectory>();
builder.Services.Replace(ServiceDescriptor.Singleton<IUserGroupManager>(
    provider => (InMemoryOrgDirectory)provider.GetRequiredService<IOrgDirectory>()));

// 订单业务数据端口（order_bill 的落点）
builder.Services.AddSingleton<IOrderBillStore, OrderBillStore>();

// 两个服务任务委托 —— 与 BPMN 里的 activiti:delegateExpression 一一对应：
//   order.ApproveComplete → 订单状态 100（审批通过）
//   order.RefuseComplete  → 订单状态 -1（审批拒绝）
// 名字写在委托类上（[ServiceTaskName]，见 Order/OrderDelegates.cs），
// 这里只负责扫一遍程序集把它登记成 keyed 服务。新增委托时只需在类上加特性。
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

// 演示页（wwwroot/index.html）：提交申请 / 审批通过拒绝驳回 / 状态回写一览
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    demo = "order-approval",
    processKey = OrderProcessBootstrapper.ProcessKey,
}));

app.MapOrderEndpoints();

// 打开即用：启动时部署订单审批流程（定义来自 web/designer/examples/order-process.bpmn20.xml；
// 设计器画的订单流程导出 XML 后替换该文件即可，委托契约不变）。
// ⚠️ 必须在 app.Run() 之前：运行期上下文（流程定义 id + 环节名对照表）要在接受请求前装好。
await OrderProcessBootstrapper.DeployAsync(app.Services, app.Logger);

var billStore = app.Services.GetRequiredService<IOrderBillStore>();
app.Logger.LogInformation("订单数据落点：{Location}", billStore.Location);

app.Logger.LogInformation(
    "订单审批 Demo 已就绪：{Urls}（OpenAPI 见 /openapi/v1.json）",
    builder.Configuration["urls"] ?? "见下方 Kestrel 监听地址");

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
