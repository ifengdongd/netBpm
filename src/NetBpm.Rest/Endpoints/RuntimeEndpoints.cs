using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 运行时端点（开发计划 P9）：启动实例 / 查待办 / 认领 / 完成任务 / 查实例 /
/// 查变量 / 查历史 / 流程图数据。
/// <para>
/// 约定：<b>未实现的端点返回 501 而不是空数组</b>，避免"看着能用、其实没数据"（当前已无 501 端点）。
/// 「查历史」一律走 <c>ACT_HI_*</c>：实例结束后运行时行已被删除，
/// 只有历史表还能回答"走过哪些节点、谁办过"。
/// </para>
/// <para>
/// 结构（原 2200 行单文件拆分为路由表 + 4 个域文件）：
/// <list type="bullet">
///   <item><description><see cref="MapProcessInstanceEndpoints"/>：实例生命周期 / 活动干预 / 迁移 / 批量；</description></item>
///   <item><description><see cref="MapTaskEndpoints"/>：待办查询 / 办理 / 管理动作 / 批量 / 统计；</description></item>
///   <item><description><see cref="MapVariableEndpoints"/>：流程实例变量与任务局部变量；</description></item>
///   <item><description><see cref="MapHistoryEndpoints"/>：历史查询与流程图数据。</description></item>
/// </list>
/// </para>
/// <para>
/// 🔑 <b>授权是声明式的，而且是两层</b>：
/// <list type="number">
///   <item><description><b>能力上限</b>（<c>RequireEngineScope</c>，<c>workflow:*</c>）：只对注册客户端生效，
///     决定「这个接入系统允不允许碰这类原语」。逐条写在路由上，一眼能看出每个原语要什么授权。
///     ⚠️ 它必须排在访问过滤器<b>之后</b> —— 先 404（不存在 / 跨租户）再 403（缺 scope），
///     否则跨租户探测会从 404 变成 403，泄露「这个 id 存在」。</description></item>
///   <item><description><b>数据级授权</b>（<c>RequireInstanceAccess</c> / <c>RequireTaskAccess</c> /
///     <c>RequireHistoryAccess</c> / <c>RequireAdminScope</c>）：租户边界、实例归属、任务归属、
///     变量参与人、<c>netbpm.admin.*</c> 管理动作。</description></item>
/// </list>
/// 两层都过才执行 handler —— 与 <see cref="PlatformScopes"/> 声明的「scope 是能力上限，不是数据授权」一致。
/// </para>
/// <para>
/// 🔑 <b>业务写入不对客户端开放</b>：发起实例 / 办理任务 / 批量办理标的是 <c>RequireBusinessSurface</c>，
/// 注册客户端一律 403（无论持有什么 scope），必须改走 <c>/workflow/v1</c>。
/// 原因是原语面的写入不产生审批结论（无 <c>optionId</c>、不写时间线与表单快照），
/// 放行等于允许「无结论的办结」。客户端仍可用的写入只剩管理干预（挂起 / 跳转 / 转办 / 变量直写）。
/// </para>
/// </summary>
public static partial class RuntimeEndpoints
{
    /// <summary>映射全部运行态端点（<c>/api/runtime</c>）。</summary>
    public static RouteGroupBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/runtime").WithTags("runtime");

        group.MapProcessInstanceEndpoints();
        group.MapTaskEndpoints();
        group.MapVariableEndpoints();
        group.MapExternalTaskEndpoints();
        group.MapHistoryEndpoints();

        return group;
    }

    /// <summary>流程实例：启动 / 查询 / 删除 / 挂起激活 / 活动干预 / 迁移 / 批量。</summary>
    private static RouteGroupBuilder MapProcessInstanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/process-instances", StartProcessInstanceAsync)
            .RequireBusinessSurface("发起流程实例", "POST /workflow/v1/instances")
            .Produces<StartProcessInstanceResponse>();
        group.MapGet("/process-instances", FindProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.InstanceRead, "查询流程实例列表")
            .Produces<IReadOnlyList<ProcessInstanceResponse>>();
        group.MapGet("/process-instances/{processInstanceId}", GetProcessInstanceAsync)
            .RequireInstanceAccess(InstanceAccess.RequireExisting)
            .RequireEngineScope(PlatformScopes.InstanceRead, "查看流程实例");

        // 管理动作：先对象存在性 / 租户（404），再 scope（403）—— 与既有契约一致
        group.MapDelete("/process-instances/{processInstanceId}", DeleteProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "删除流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceDelete, "删除流程实例");

        group.MapPost("/process-instances/{processInstanceId}/suspend", SuspendProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "挂起流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "挂起流程实例");

        group.MapPost("/process-instances/{processInstanceId}/activate", ActivateProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "激活流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "激活流程实例");

        group.MapPost("/process-instances/{processInstanceId}/jump", JumpProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "跳转流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "跳转流程实例");

        group.MapPost(
                "/process-instances/{processInstanceId}/activities/{activityId}/cancel",
                CancelActivityAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "取消活动实例")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "取消活动实例")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost(
                "/process-instances/{processInstanceId}/activities/{activityId}/start-before",
                StartBeforeActivityAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "在活动之前启动")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "在活动之前启动")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/process-instances/{processInstanceId}/modify", ModifyProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "指令式修改流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "指令式修改流程实例")
            .Produces<ModifyProcessInstanceResponse>()
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/process-instances/{processInstanceId}/migrate", MigrateProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.AdminInstance, "迁移流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "迁移流程实例")
            .Produces<MigrationPlanResponse>();

        group.MapGet("/process-instances/{processInstanceId}/migrations", FindMigrationsAsync)
            .RequireInstanceAccess()
            .RequireEngineScope(PlatformScopes.InstanceRead, "查看迁移记录")
            .Produces<IReadOnlyList<MigrationRecordResponse>>();

        // 批量：授权在循环外整批判定（缺 scope 整体 403，而不是逐项失败）
        group.MapPost("/process-instances/batch/delete", BatchDeleteProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.AdminInstance, "批量删除流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceDelete, "批量删除流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/suspend", BatchSuspendProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.AdminInstance, "批量挂起流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "批量挂起流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/activate", BatchActivateProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.AdminInstance, "批量激活流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "批量激活流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/migrate", BatchMigrateProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.AdminInstance, "批量迁移流程实例")
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "批量迁移流程实例")
            .Produces<BatchResultResponse>();

        return group;
    }

    /// <summary>任务：查询 / 办理 / 管理动作 / 批量 / 统计。</summary>
    private static RouteGroupBuilder MapTaskEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/tasks", FindTasksAsync)
            .RequireEngineScope(PlatformScopes.TaskRead, "查询待办")
            .Produces<IReadOnlyList<TaskResponse>>();
        group.MapGet("/tasks/{taskId}", GetTaskAsync)
            .RequireTaskAccess()
            .RequireEngineScope(PlatformScopes.TaskRead, "查看任务")
            .Produces<TaskDetailResponse>();
        group.MapGet("/tasks/{taskId}/identity-links", FindTaskIdentityLinksAsync)
            .RequireTaskAccess()
            .RequireEngineScope(PlatformScopes.TaskRead, "查看任务候选关系")
            .Produces<IReadOnlyList<IdentityLinkResponse>>();
        group.MapGet(
                "/process-instances/{processInstanceId}/identity-links",
                FindProcessInstanceIdentityLinksAsync)
            .RequireInstanceAccess(InstanceAccess.RequireExisting)
            .RequireEngineScope(PlatformScopes.InstanceRead, "查看实例参与关系")
            .Produces<IReadOnlyList<IdentityLinkResponse>>();

        group.MapPatch("/tasks/{taskId}", UpdateTaskAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "修改任务")
            .RequireEngineScope(PlatformScopes.TaskAssign, "修改任务");

        group.MapPost("/tasks/{taskId}/assignee", SetAssigneeAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "转办")
            .RequireEngineScope(PlatformScopes.TaskAssign, "转办");

        group.MapPost("/tasks/{taskId}/candidate-users", AddCandidateUserAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "增加候选人")
            .RequireEngineScope(PlatformScopes.TaskAssign, "增加候选人");
        group.MapDelete("/tasks/{taskId}/candidate-users/{userId}", RemoveCandidateUserAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "移除候选人")
            .RequireEngineScope(PlatformScopes.TaskAssign, "移除候选人");

        group.MapPost("/tasks/{taskId}/candidate-groups", AddCandidateGroupAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "增加候选组")
            .RequireEngineScope(PlatformScopes.TaskAssign, "增加候选组");
        group.MapDelete("/tasks/{taskId}/candidate-groups/{groupId}", RemoveCandidateGroupAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "移除候选组")
            .RequireEngineScope(PlatformScopes.TaskAssign, "移除候选组");

        group.MapPost("/tasks/{taskId}/delegate", DelegateTaskAsync)
            .RequireTaskAccess(TaskAccess.Delegate)
            .RequireEngineScope(PlatformScopes.TaskAssign, "委派任务");
        group.MapPost("/tasks/{taskId}/resolve", ResolveTaskAsync)
            .RequireTaskAccess(TaskAccess.Resolve)
            .RequireEngineScope(PlatformScopes.TaskAssign, "解决委派");

        group.MapGet("/tasks/{taskId}/form", GetTaskFormAsync)
            .RequireTaskAccess()
            .RequireEngineScope(PlatformScopes.TaskRead, "查看任务表单")
            .Produces<FormMetadataResponse>();

        // 认领：unclaim=true 的兼容分支与认领是两套授权语义，故授权留在 handler 内按请求体判定
        group.MapPost("/tasks/{taskId}/claim", ClaimTaskAsync)
            .RequireTaskAccess()
            .RequireEngineScope(PlatformScopes.TaskAssign, "认领任务");

        group.MapPost("/tasks/{taskId}/unclaim", UnclaimTaskAsync)
            .RequireTaskAccess(TaskAccess.Unclaim)
            .RequireEngineScope(PlatformScopes.TaskAssign, "取消认领");

        // 办理 = 业务写入：原语面的 complete 不写审批结论，客户端一律改走平台面的 submit
        group.MapPost("/tasks/{taskId}/complete", CompleteTaskAsync)
            .RequireTaskAccess(TaskAccess.Complete)
            .RequireBusinessSurface("办理任务", "POST /workflow/v1/tasks/{taskId}/submit");

        // 统计与批量（管理端）：批量逐项执行，授权在 Service 内逐项完成
        group.MapGet("/tasks/stats", GetTaskStatsAsync)
            .RequireEngineScope(PlatformScopes.TaskRead, "查看待办统计")
            .Produces<TaskStatsResponse>();
        group.MapPost("/tasks/batch/assignee", BatchSetAssigneeAsync)
            .RequireEngineScope(PlatformScopes.TaskAssign, "批量转办")
            .Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/due-date", BatchSetTaskDueDateAsync)
            .RequireEngineScope(PlatformScopes.TaskAssign, "批量改到期时间")
            .Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/complete", BatchCompleteTasksAsync)
            .RequireBusinessSurface(
                "批量办理任务",
                "逐个调用 POST /workflow/v1/tasks/{taskId}/submit（平台面没有批量入口）")
            .Produces<BatchResultResponse>();

        return group;
    }

    /// <summary>变量：流程实例作用域 + 任务局部作用域。</summary>
    private static RouteGroupBuilder MapVariableEndpoints(this RouteGroupBuilder group)
    {
        // 直写引擎变量等于越过参数 schema 与 writableBy —— 平台面里这是 workflow:admin:variables
        // （默认不授予任何客户端），引擎面沿用同一口径
        group.MapGet("/process-instances/{processInstanceId}/variables", GetVariablesAsync)
            .RequireInstanceAccess(InstanceAccess.ReadVariables)
            .RequireEngineScope(PlatformScopes.InstanceRead, "查看流程变量");
        group.MapPut("/process-instances/{processInstanceId}/variables", SetVariablesAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables)
            .RequireEngineScope(PlatformScopes.AdminVariables, "写入流程变量");
        group.MapPut("/process-instances/{processInstanceId}/variables/{name}", SetVariableAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables)
            .RequireEngineScope(PlatformScopes.AdminVariables, "写入流程变量");
        group.MapDelete("/process-instances/{processInstanceId}/variables/{name}", RemoveVariableAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables)
            .RequireEngineScope(PlatformScopes.AdminVariables, "删除流程变量");

        // 任务局部变量（B2.4）：随任务存亡，不写回流程实例作用域
        group.MapGet("/tasks/{taskId}/variables", GetTaskVariablesAsync)
            .RequireTaskAccess(TaskAccess.ReadVariables)
            .RequireEngineScope(PlatformScopes.TaskRead, "查看任务变量");
        group.MapPut("/tasks/{taskId}/variables/{name}", SetTaskVariableAsync)
            .RequireTaskAccess(TaskAccess.WriteVariables)
            .RequireEngineScope(PlatformScopes.AdminVariables, "写入任务变量");
        group.MapDelete("/tasks/{taskId}/variables/{name}", RemoveTaskVariableAsync)
            .RequireTaskAccess(TaskAccess.WriteVariables)
            .RequireEngineScope(PlatformScopes.AdminVariables, "删除任务变量");

        return group;
    }

    /// <summary>历史与流程图数据（一律走 <c>ACT_HI_*</c>）。</summary>
    private static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder group)
    {
        // 跨实例列表：引擎面没有人员收窄（不带条件时就是全租户），因此要审计口径而不是「读自己的」
        group.MapGet("/history/process-instances", FindHistoricProcessInstancesAsync)
            .RequireEngineScope(PlatformScopes.HistoryReadAll, "查询历史流程实例列表")
            .Produces<IReadOnlyList<HistoricProcessInstanceResponse>>();

        group.MapGet("/history/process-instances/{processInstanceId}", FindHistoricProcessInstanceAsync)
            .RequireHistoryAccess()
            .RequireEngineScope(PlatformScopes.HistoryRead, "查看流程实例历史");

        group.MapGet(
                "/history/process-instances/{processInstanceId}/activities",
                FindHistoricActivitiesAsync)
            .RequireHistoryAccess()
            .RequireEngineScope(PlatformScopes.HistoryRead, "查看流程实例活动历史");

        group.MapGet("/history/process-instances/{processInstanceId}/tasks", FindHistoricTasksAsync)
            .RequireEngineScope(PlatformScopes.HistoryRead, "查看流程实例历史任务")
            .Produces<IReadOnlyList<HistoricTaskResponse>>();

        group.MapGet("/history/process-instances/{processInstanceId}/variables", FindHistoricVariablesAsync)
            .RequireHistoryAccess(HistoryAccess.ReadVariables)
            .RequireEngineScope(PlatformScopes.HistoryRead, "查看历史变量")
            .Produces<IReadOnlyList<HistoricVariableResponse>>();

        // 跨实例已办（我的已办 / 审计）：不再要求 processInstanceId，同样只对审计口径开放
        group.MapGet("/history/tasks", FindHistoricTasksAsync)
            .RequireEngineScope(PlatformScopes.HistoryReadAll, "查询历史任务列表")
            .Produces<IReadOnlyList<HistoricTaskResponse>>();

        // 流程图数据（P9 交付项：BPMN XML + 活动高亮）
        group.MapGet("/process-instances/{processInstanceId}/diagram", GetDiagramAsync)
            .RequireHistoryAccess()
            .RequireEngineScope(PlatformScopes.HistoryRead, "查看流程图数据")
            .Produces<DiagramResponse>();

        return group;
    }
}
