using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Demo.LeaveApproval.Leave;
using NetBpm.Identity;
using NetBpm.Runtime;
using NetBpm.Workflow;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 组合根：引擎与业务数据各有一套「内存 / 真库」的组合，由配置决定用哪套。
//
//   NetBpm:UseInMemoryExecutionTree = true   → 引擎走内存执行树（零依赖，重启即清）
//                                  = false   → 引擎落 MySQL（ACT_* 表）
//   Leave:Store = Json | MySql                → 业务数据落 JSON 文件 / 落四张业务表
//
// ⚠️ 两者<b>要一起切</b>：内存宿主的流程实例 id 每次启动都从 1 重新开始，
// 而真库里的 instances 是持久化的 —— 混用会让新实例把同 id 的旧记录覆盖掉，看上去像「流程串了」。
builder.Services.AddNetBpm(builder.Configuration);
var useMySqlStore = UseMySqlStore(builder.Configuration);

// 身份与组织架构：两种模式各一套实现，注册处只换类型 ——
//   JSON 演示模式：InMemoryOrgDirectory（固定演示数据，用户 id 与 e2e 对齐）；
//   MySQL 模式：MySqlOrgDirectory（落 0005 建的 sys_dept / sys_emp / sys_emp_group* 四张表）。
// 两者同时实现 IOrgDirectory（业务查询：部门/员工/审批人名单，组织端点与视图装饰用）
// 与 IUserGroupManager（引擎查待办要用的「用户 ↔ 组」展开）—— 同一份数据，两个视角。
if (useMySqlStore)
{
    builder.Services.AddSingleton<MySqlOrgDirectory>();
    builder.Services.AddSingleton<IOrgDirectory>(provider => provider.GetRequiredService<MySqlOrgDirectory>());
    builder.Services.Replace(ServiceDescriptor.Singleton<IUserGroupManager>(provider =>
        provider.GetRequiredService<MySqlOrgDirectory>()));
}
else
{
    builder.Services.AddSingleton<InMemoryOrgDirectory>();
    builder.Services.AddSingleton<IOrgDirectory>(provider => provider.GetRequiredService<InMemoryOrgDirectory>());
    builder.Services.Replace(ServiceDescriptor.Singleton<IUserGroupManager>(provider =>
        provider.GetRequiredService<InMemoryOrgDirectory>()));
}

// 监听器触发计数（观测用单例）：两个监听器往里 +1，/api/leave/listener-stats 供 e2e 断言
builder.Services.AddSingleton<ListenerStats>();

// 运行期上下文（流程定义 id、环节名 → 环节 id 映射）由 AddNetBpmWorkflow 一并注册，
// 部署流程定义之后由 WorkflowProcessBootstrapper 填充（见下方启动部署）。

// 应用层：发起 / 环节信息 / 提交 / 审批历史四件事都由它提供，业务数据端口按类型注册。
// 两种实现的形状完全一致（都实现 IWorkflowDataStore + ILeaveBusinessInspector），
// 所以换存储只动这一处 —— 端点、演示页、委托都不用改。
var inMemoryEngine = builder.Configuration.GetValue("NetBpm:UseInMemoryExecutionTree", true);

if (useMySqlStore)
{
    builder.Services.AddNetBpmWorkflow<MySqlWorkflowDataStore>();
    builder.Services.AddSingleton<ILeaveBusinessInspector>(
        provider => provider.GetRequiredService<MySqlWorkflowDataStore>());
}
else
{
    builder.Services.AddNetBpmWorkflow<LeaveBusinessStore>();
    builder.Services.AddSingleton<ILeaveBusinessInspector>(
        provider => provider.GetRequiredService<LeaveBusinessStore>());
}

// 两个服务任务委托 —— 与 BPMN 里的 activiti:delegateExpression 一一对应。
//
// 名字写在委托类上（见 Leave/LeaveDelegates.cs 的 [ServiceTaskName("leave.ApproveComplete")]），
// 这里只负责扫一遍程序集把它登记成 keyed 服务 —— 也就是
// ServiceProviderActivityDelegateResolver 的第一条解析路径。新增委托时只需在类上加特性，
// 不用回来改这一行。
//
// 名字不必等于 C# 类型名："leave.ApproveComplete" 表达的是流程里的业务语义。
// 除了特性名，类型全名/短名也会被登记成别名，所以 delegateExpression 写类名同样能解析。
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);

builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.MapOpenApi();

// 演示页（wwwroot/index.html）
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    engine = "NetBpm",
    demo = "leave-approval",
    processKey = LeaveProcessBootstrapper.ProcessKey,
}));

app.MapLeaveEndpoints();

// 打开即用：启动时部署请假流程（定义来自 web/designer/examples/leave-process.bpmn20.xml）
await LeaveProcessBootstrapper.DeployAsync(app.Services, app.Logger);

// 业务数据落在哪儿 —— 这句话是给人核对的，两种存储各报各的（见 ILeaveBusinessInspector）。
var inspector = app.Services.GetRequiredService<ILeaveBusinessInspector>();

app.Logger.LogInformation("业务数据落点：{Location}", inspector.Location);

// ⚠️ 两组开关混用是个容易踩的坑：内存引擎的流程实例 id 每次启动从 1 重新开始，
// 而持久化的业务数据是累积的 —— 新实例会把同 id 的旧记录覆盖掉，看上去像「流程串了」。
// 这里提前说一句，省得排查半天。
if (inMemoryEngine != !useMySqlStore)
{
    app.Logger.LogWarning(
        "引擎与业务数据没有一起切（UseInMemoryExecutionTree={InMemory}，Leave:Store={Store}）："
        + "流程实例 id 会与旧记录撞号并覆盖它，请把两者一起切到真库。",
        inMemoryEngine, useMySqlStore ? "MySql" : "Json");
}

// 注意：此刻 app.Run() 还没跑，app.Urls 是空的（Kestrel 尚未绑定），
// 所以地址要从配置里取（--urls / ASPNETCORE_URLS / launchSettings 都落在这）。
app.Logger.LogInformation(
    "请假审批 Demo 已就绪：{Urls}（演示页在根路径；OpenAPI 见 /openapi/v1.json）",
    builder.Configuration["urls"] ?? "见下方 Kestrel 监听地址");

app.Run();

/// <summary>业务数据是否落 MySQL（<c>Leave:Store = MySql</c>）。</summary>
static bool UseMySqlStore(IConfiguration configuration) =>
    string.Equals(configuration["Leave:Store"], "MySql", StringComparison.OrdinalIgnoreCase);

/// <summary>供集成测试使用的可见性标记。</summary>
public partial class Program;
