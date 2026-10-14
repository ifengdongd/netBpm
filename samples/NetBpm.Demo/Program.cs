using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Demo;
using NetBpm.Demo.Common;
using NetBpm.Demo.Leave;
using NetBpm.Demo.Order;
using NetBpm.Identity;
using NetBpm.Runtime;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// JSON 序列化保持 C# 属性原名（PascalCase）：wwwroot 页面脚本按 PascalCase 取值，
// 请求反序列化默认大小写不敏感，前端传 camelCase 的键（userId / targetActivityId…）照样能绑上。
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = null);

// 组合根：一个宿主同时承载两条流程（订单审批 + 请假审批）。
//
//   Workflow:Storage = "MySql" → 引擎落 MySQL（ACT_* 表）+ 业务数据落 MySQL（instances 等）
//                    = "Json"  → 引擎走内存执行树 + 业务数据落 data/workflow.json
//   ⚠️ 引擎与业务数据必须一起切：内存宿主的流程实例 id 每次启动都从 1 重新开始，
//   而真库里的 instances 是累积的 —— 混用会撞号覆盖。
builder.Services.AddNetBpm(builder.Configuration);

// 应用层：发起 / 环节信息 / 提交 / 审批历史四件事都由它提供（两条流程共用一个存储实现）。
// 业务数据核对端点（/api/leave/business）依赖 IWorkflowStoreInspector，与具体实现指向同一份。
var storage = builder.Configuration["Workflow:Storage"] ?? "MySql";

if (string.Equals(storage, "MySql", StringComparison.OrdinalIgnoreCase))
{
    // MySQL 真库模式：instances / instance_current_task / deal_history / workflow_parameters + order_bill
    builder.Services.AddNetBpmWorkflow<NetBpm.Demo.Storage.MySqlWorkflowDataStore>();
    builder.Services.AddSingleton<IWorkflowStoreInspector>(provider =>
        provider.GetRequiredService<NetBpm.Demo.Storage.MySqlWorkflowDataStore>());
    builder.Services.AddSingleton<IOrderBillStore, NetBpm.Demo.Storage.MySqlOrderBillStore>();
}
else
{
    // JSON 演示模式
    builder.Services.AddNetBpmWorkflow<JsonWorkflowDataStore>();
    builder.Services.AddSingleton<IWorkflowStoreInspector>(provider =>
        provider.GetRequiredService<JsonWorkflowDataStore>());
    builder.Services.AddSingleton<IOrderBillStore, OrderBillStore>();
}

// 身份与组织架构：InMemoryOrgDirectory 同时实现 IOrgDirectory（业务查询）
// 与 IUserGroupManager（引擎查候选组待办要用的「用户 ↔ 组」展开）—— 同一份数据，两个视角。
// 请假审批人 = candidateGroups="leaders" → leader1（李白）；订单审批人 = managers → manager1（孙建国）。
builder.Services.AddSingleton<InMemoryOrgDirectory>();
builder.Services.AddSingleton<IOrgDirectory>(provider => provider.GetRequiredService<InMemoryOrgDirectory>());
builder.Services.Replace(ServiceDescriptor.Singleton<IUserGroupManager>(
    provider => provider.GetRequiredService<InMemoryOrgDirectory>()));

// 监听器触发计数（观测用单例）：请假流程的监听器往里计数
builder.Services.AddSingleton<ListenerStats>();

// 四个服务任务委托 —— 与两条 BPMN 里的 activiti:delegateExpression 一一对应：
//   order.ApproveComplete → 订单状态 100（审批通过）
//   order.RefuseComplete  → 订单状态 -1（审批拒绝）
//   leave.ApproveComplete / leave.RefuseComplete → 请假审批结论落盘
// 名字写在委托类上（[ServiceTaskName]），AddServiceTaskDelegates 扫一遍程序集登记成 keyed 服务。
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

// 静态页面（wwwroot）：
//   index.html    首页：流程信息 + 流程发起 + 我的申请
//   tasks.html    待办任务列表页
//   approve.html  流程审批页
//   bill.html     业务单查看页
//   transfer.html 待办任务转移页
//   jump.html     流程跳转页
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    demo = "merged-approval",
    processes = new[] { OrderProcessBootstrapper.ProcessKey, LeaveProcessBootstrapper.ProcessKey },
}));

// 公共端点：流程信息枚举、跨流程的待办中心 / 申请单列表 / 组织架构
app.MapProcessEndpoints();
app.MapTaskCenterEndpoints();

// 两条流程各自的业务端点（/api/order/*、/api/leave/*）
app.MapOrderEndpoints();
app.MapLeaveEndpoints();

// 打开即用：启动时部署两条流程
//（定义来自 web/designer/examples/*.bpmn20.xml；设计器画的流程导出 XML 后替换即可，委托契约不变）。
// ⚠️ 必须在 app.Run() 之前：运行期上下文（流程定义 id + 环节名对照表）要在接受请求前装好。
await OrderProcessBootstrapper.DeployAsync(app.Services, app.Logger);
await LeaveProcessBootstrapper.DeployAsync(app.Services, app.Logger);

var billStore = app.Services.GetRequiredService<IOrderBillStore>();
app.Logger.LogInformation("订单数据落点：{Location}", billStore.Location);
app.Logger.LogInformation(
    "合并审批 Demo 已就绪：{Urls}（OpenAPI 见 /openapi/v1.json）",
    builder.Configuration["urls"] ?? "见下方 Kestrel 监听地址");

app.Run();

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
