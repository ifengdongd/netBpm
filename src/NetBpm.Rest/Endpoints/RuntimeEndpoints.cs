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
/// 🔑 <b>授权是声明式的</b>：每个路由上直接写明所需访问级别（<c>RequireInstanceAccess</c> /
/// <c>RequireTaskAccess</c> / <c>RequireHistoryAccess</c> / <c>RequireAdminScope</c>），
/// 不再在 handler 里手工调用 —— 手工调用的必然产物是顺序漂移与漏调。
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
        group.MapHistoryEndpoints();

        return group;
    }

    /// <summary>流程实例：启动 / 查询 / 删除 / 挂起激活 / 活动干预 / 迁移 / 批量。</summary>
    private static RouteGroupBuilder MapProcessInstanceEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/process-instances", StartProcessInstanceAsync)
            .Produces<StartProcessInstanceResponse>();
        group.MapGet("/process-instances", FindProcessInstancesAsync)
            .Produces<IReadOnlyList<ProcessInstanceResponse>>();
        group.MapGet("/process-instances/{processInstanceId}", GetProcessInstanceAsync)
            .RequireInstanceAccess(InstanceAccess.RequireExisting);

        // 管理动作：先对象存在性 / 租户（404），再 scope（403）—— 与既有契约一致
        group.MapDelete("/process-instances/{processInstanceId}", DeleteProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceDelete, "删除流程实例");

        group.MapPost("/process-instances/{processInstanceId}/suspend", SuspendProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "挂起流程实例");

        group.MapPost("/process-instances/{processInstanceId}/activate", ActivateProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "激活流程实例");

        group.MapPost("/process-instances/{processInstanceId}/jump", JumpProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "跳转流程实例");

        group.MapPost(
                "/process-instances/{processInstanceId}/activities/{activityId}/cancel",
                CancelActivityAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "取消活动实例")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost(
                "/process-instances/{processInstanceId}/activities/{activityId}/start-before",
                StartBeforeActivityAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "在活动之前启动")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/process-instances/{processInstanceId}/migrate", MigrateProcessInstanceAsync)
            .RequireInstanceAccess()
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "迁移流程实例")
            .Produces<MigrationPlanResponse>();

        group.MapGet("/process-instances/{processInstanceId}/migrations", FindMigrationsAsync)
            .RequireInstanceAccess()
            .Produces<IReadOnlyList<MigrationRecordResponse>>();

        // 批量：授权在循环外整批判定（缺 scope 整体 403，而不是逐项失败）
        group.MapPost("/process-instances/batch/delete", BatchDeleteProcessInstancesAsync)
            .RequireAdminScope(TaskScopes.AdminInstanceDelete, "批量删除流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/suspend", BatchSuspendProcessInstancesAsync)
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "批量挂起流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/activate", BatchActivateProcessInstancesAsync)
            .RequireAdminScope(TaskScopes.AdminInstanceSuspend, "批量激活流程实例")
            .Produces<BatchResultResponse>();

        group.MapPost("/process-instances/batch/migrate", BatchMigrateProcessInstancesAsync)
            .RequireAdminScope(TaskScopes.AdminInstanceJump, "批量迁移流程实例")
            .Produces<BatchResultResponse>();

        return group;
    }

    /// <summary>任务：查询 / 办理 / 管理动作 / 批量 / 统计。</summary>
    private static RouteGroupBuilder MapTaskEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/tasks", FindTasksAsync).Produces<IReadOnlyList<TaskResponse>>();
        group.MapGet("/tasks/{taskId}", GetTaskAsync)
            .RequireTaskAccess()
            .Produces<TaskDetailResponse>();
        group.MapGet("/tasks/{taskId}/identity-links", FindTaskIdentityLinksAsync)
            .RequireTaskAccess()
            .Produces<IReadOnlyList<IdentityLinkResponse>>();
        group.MapGet(
                "/process-instances/{processInstanceId}/identity-links",
                FindProcessInstanceIdentityLinksAsync)
            .RequireInstanceAccess(InstanceAccess.RequireExisting)
            .Produces<IReadOnlyList<IdentityLinkResponse>>();

        group.MapPatch("/tasks/{taskId}", UpdateTaskAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "修改任务");

        group.MapPost("/tasks/{taskId}/assignee", SetAssigneeAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "转办");

        group.MapPost("/tasks/{taskId}/candidate-users", AddCandidateUserAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "增加候选人");
        group.MapDelete("/tasks/{taskId}/candidate-users/{userId}", RemoveCandidateUserAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "移除候选人");

        group.MapPost("/tasks/{taskId}/candidate-groups", AddCandidateGroupAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "增加候选组");
        group.MapDelete("/tasks/{taskId}/candidate-groups/{groupId}", RemoveCandidateGroupAsync)
            .RequireTaskAccess(TaskAccess.Moderate, "移除候选组");

        group.MapPost("/tasks/{taskId}/delegate", DelegateTaskAsync)
            .RequireTaskAccess(TaskAccess.Delegate);
        group.MapPost("/tasks/{taskId}/resolve", ResolveTaskAsync)
            .RequireTaskAccess(TaskAccess.Resolve);

        group.MapGet("/tasks/{taskId}/form", GetTaskFormAsync)
            .RequireTaskAccess()
            .Produces<FormMetadataResponse>();

        // 认领：unclaim=true 的兼容分支与认领是两套授权语义，故授权留在 handler 内按请求体判定
        group.MapPost("/tasks/{taskId}/claim", ClaimTaskAsync)
            .RequireTaskAccess();

        group.MapPost("/tasks/{taskId}/unclaim", UnclaimTaskAsync)
            .RequireTaskAccess(TaskAccess.Unclaim);

        group.MapPost("/tasks/{taskId}/complete", CompleteTaskAsync)
            .RequireTaskAccess(TaskAccess.Complete);

        // 统计与批量（管理端）：批量逐项执行，授权在 Service 内逐项完成
        group.MapGet("/tasks/stats", GetTaskStatsAsync).Produces<TaskStatsResponse>();
        group.MapPost("/tasks/batch/assignee", BatchSetAssigneeAsync).Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/due-date", BatchSetTaskDueDateAsync).Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/complete", BatchCompleteTasksAsync).Produces<BatchResultResponse>();

        return group;
    }

    /// <summary>变量：流程实例作用域 + 任务局部作用域。</summary>
    private static RouteGroupBuilder MapVariableEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/process-instances/{processInstanceId}/variables", GetVariablesAsync)
            .RequireInstanceAccess(InstanceAccess.ReadVariables);
        group.MapPut("/process-instances/{processInstanceId}/variables", SetVariablesAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables);
        group.MapPut("/process-instances/{processInstanceId}/variables/{name}", SetVariableAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables);
        group.MapDelete("/process-instances/{processInstanceId}/variables/{name}", RemoveVariableAsync)
            .RequireInstanceAccess(InstanceAccess.WriteVariables);

        // 任务局部变量（B2.4）：随任务存亡，不写回流程实例作用域
        group.MapGet("/tasks/{taskId}/variables", GetTaskVariablesAsync)
            .RequireTaskAccess(TaskAccess.ReadVariables);
        group.MapPut("/tasks/{taskId}/variables/{name}", SetTaskVariableAsync)
            .RequireTaskAccess(TaskAccess.WriteVariables);
        group.MapDelete("/tasks/{taskId}/variables/{name}", RemoveTaskVariableAsync)
            .RequireTaskAccess(TaskAccess.WriteVariables);

        return group;
    }

    /// <summary>历史与流程图数据（一律走 <c>ACT_HI_*</c>）。</summary>
    private static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/history/process-instances", FindHistoricProcessInstancesAsync)
            .Produces<IReadOnlyList<HistoricProcessInstanceResponse>>();

        group.MapGet("/history/process-instances/{processInstanceId}", FindHistoricProcessInstanceAsync)
            .RequireHistoryAccess();

        group.MapGet(
                "/history/process-instances/{processInstanceId}/activities",
                FindHistoricActivitiesAsync)
            .RequireHistoryAccess();

        group.MapGet("/history/process-instances/{processInstanceId}/tasks", FindHistoricTasksAsync)
            .Produces<IReadOnlyList<HistoricTaskResponse>>();

        group.MapGet("/history/process-instances/{processInstanceId}/variables", FindHistoricVariablesAsync)
            .RequireHistoryAccess(HistoryAccess.ReadVariables)
            .Produces<IReadOnlyList<HistoricVariableResponse>>();

        // 跨实例已办（我的已办 / 审计）：不再要求 processInstanceId
        group.MapGet("/history/tasks", FindHistoricTasksAsync)
            .Produces<IReadOnlyList<HistoricTaskResponse>>();

        // 流程图数据（P9 交付项：BPMN XML + 活动高亮）
        group.MapGet("/process-instances/{processInstanceId}/diagram", GetDiagramAsync)
            .RequireHistoryAccess()
            .Produces<DiagramResponse>();

        return group;
    }
}
