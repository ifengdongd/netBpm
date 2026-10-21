using System.Text.Json;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Migration;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Admin;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 运行时端点（开发计划 P9）：启动实例 / 查待办 / 认领 / 完成任务 / 查实例 /
/// 查变量 / 查历史 / 流程图数据。
/// <para>
/// 约定：<b>未实现的端点返回 501 而不是空数组</b>，避免"看着能用、其实没数据"（当前已无 501 端点）。
/// 「查历史」一律走 <c>ACT_HI_*</c>：实例结束后运行时行已被删除，
/// 只有历史表还能回答"走过哪些节点、谁办过"。
/// </para>
/// </summary>
public static class RuntimeEndpoints
{
    /// <summary>单次批量操作的条数上限。</summary>
    private const int MaxBatchSize = 200;

    public static RouteGroupBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/runtime").WithTags("runtime");

        // 流程实例
        group.MapPost("/process-instances", StartProcessInstanceAsync).Produces<StartProcessInstanceResponse>();
        group.MapGet("/process-instances", FindProcessInstancesAsync).Produces<IReadOnlyList<ProcessInstanceResponse>>();
        group.MapGet("/process-instances/{processInstanceId}", GetProcessInstanceAsync);
        group.MapDelete("/process-instances/{processInstanceId}", DeleteProcessInstanceAsync);
        group.MapPost("/process-instances/{processInstanceId}/suspend", SuspendProcessInstanceAsync);
        group.MapPost("/process-instances/{processInstanceId}/activate", ActivateProcessInstanceAsync);
        group.MapPost("/process-instances/{processInstanceId}/jump", JumpProcessInstanceAsync);
        group.MapPost("/process-instances/{processInstanceId}/activities/{activityId}/cancel", CancelActivityAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/process-instances/{processInstanceId}/activities/{activityId}/start-before", StartBeforeActivityAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/process-instances/{processInstanceId}/migrate", MigrateProcessInstanceAsync)
            .Produces<MigrationPlanResponse>();
        group.MapGet("/process-instances/{processInstanceId}/migrations", FindMigrationsAsync)
            .Produces<IReadOnlyList<MigrationRecordResponse>>();

        // 任务（P7 已落地）
        group.MapGet("/tasks", FindTasksAsync).Produces<IReadOnlyList<TaskResponse>>();
        group.MapGet("/tasks/{taskId}", GetTaskAsync).Produces<TaskDetailResponse>();
        group.MapGet("/tasks/{taskId}/identity-links", FindTaskIdentityLinksAsync)
            .Produces<IReadOnlyList<IdentityLinkResponse>>();
        group.MapGet("/process-instances/{processInstanceId}/identity-links", FindProcessInstanceIdentityLinksAsync)
            .Produces<IReadOnlyList<IdentityLinkResponse>>();
        group.MapPatch("/tasks/{taskId}", UpdateTaskAsync);
        group.MapPost("/tasks/{taskId}/assignee", SetAssigneeAsync);
        group.MapPost("/tasks/{taskId}/candidate-users", AddCandidateUserAsync);
        group.MapDelete("/tasks/{taskId}/candidate-users/{userId}", RemoveCandidateUserAsync);
        group.MapPost("/tasks/{taskId}/candidate-groups", AddCandidateGroupAsync);
        group.MapDelete("/tasks/{taskId}/candidate-groups/{groupId}", RemoveCandidateGroupAsync);
        group.MapPost("/tasks/{taskId}/delegate", DelegateTaskAsync);
        group.MapPost("/tasks/{taskId}/resolve", ResolveTaskAsync);
        group.MapGet("/tasks/{taskId}/form", GetTaskFormAsync).Produces<FormMetadataResponse>();
        group.MapPost("/tasks/{taskId}/claim", ClaimTaskAsync);
        group.MapPost("/tasks/{taskId}/unclaim", UnclaimTaskAsync);
        group.MapPost("/tasks/{taskId}/complete", CompleteTaskAsync);

        // 统计与批量（管理端）
        group.MapGet("/tasks/stats", GetTaskStatsAsync).Produces<TaskStatsResponse>();
        group.MapPost("/tasks/batch/assignee", BatchSetAssigneeAsync).Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/due-date", BatchSetTaskDueDateAsync).Produces<BatchResultResponse>();
        group.MapPost("/tasks/batch/complete", BatchCompleteTasksAsync).Produces<BatchResultResponse>();
        group.MapPost("/process-instances/batch/delete", BatchDeleteProcessInstancesAsync)
            .Produces<BatchResultResponse>();
        group.MapPost("/process-instances/batch/suspend", BatchSuspendProcessInstancesAsync)
            .Produces<BatchResultResponse>();
        group.MapPost("/process-instances/batch/activate", BatchActivateProcessInstancesAsync)
            .Produces<BatchResultResponse>();
        group.MapPost("/process-instances/batch/migrate", BatchMigrateProcessInstancesAsync)
            .Produces<BatchResultResponse>();

        // 变量（P5 后半段 + B2.4 删除 + 运行期写入）
        group.MapGet("/process-instances/{processInstanceId}/variables", GetVariablesAsync);
        group.MapPut("/process-instances/{processInstanceId}/variables", SetVariablesAsync);
        group.MapPut("/process-instances/{processInstanceId}/variables/{name}", SetVariableAsync);
        group.MapDelete("/process-instances/{processInstanceId}/variables/{name}", RemoveVariableAsync);

        // 任务局部变量（B2.4）：随任务存亡，不写回流程实例作用域
        group.MapGet("/tasks/{taskId}/variables", GetTaskVariablesAsync);
        group.MapPut("/tasks/{taskId}/variables/{name}", SetTaskVariableAsync);
        group.MapDelete("/tasks/{taskId}/variables/{name}", RemoveTaskVariableAsync);

        // 流程图数据（P9 交付项：BPMN XML + 活动高亮）
        group.MapGet("/process-instances/{processInstanceId}/diagram", GetDiagramAsync).Produces<DiagramResponse>();

        // 历史（P5 后半段写入，P9 提供读取）
        group.MapGet("/history/process-instances", FindHistoricProcessInstancesAsync)
            .Produces<IReadOnlyList<HistoricProcessInstanceResponse>>();
        group.MapGet("/history/process-instances/{processInstanceId}", FindHistoricProcessInstanceAsync);
        group.MapGet("/history/process-instances/{processInstanceId}/activities", FindHistoricActivitiesAsync);
        group.MapGet("/history/process-instances/{processInstanceId}/tasks", FindHistoricTasksAsync)
            .Produces<IReadOnlyList<HistoricTaskResponse>>();
        group.MapGet("/history/process-instances/{processInstanceId}/variables", FindHistoricVariablesAsync)
            .Produces<IReadOnlyList<HistoricVariableResponse>>();

        // 跨实例已办（我的已办 / 审计）：不再要求 processInstanceId
        group.MapGet("/history/tasks", FindHistoricTasksAsync).Produces<IReadOnlyList<HistoricTaskResponse>>();

        return group;
    }

    // ─────────────────────────── 流程实例 ───────────────────────────

    /// <summary>
    /// 启动流程实例：<c>processDefinitionId</c> 精确指定版本；只给 <c>processDefinitionKey</c> 时
    /// 按 key 启动最新版本（对应 <c>runtimeService.startProcessInstanceByKey</c>）。
    /// </summary>
    private static async Task<IResult> StartProcessInstanceAsync(
        StartProcessInstanceRequest request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var byId = !string.IsNullOrWhiteSpace(request.ProcessDefinitionId);
        var byKey = !string.IsNullOrWhiteSpace(request.ProcessDefinitionKey);

        if (!byId && !byKey)
        {
            return Results.BadRequest(new ErrorResponse(
                "processDefinitionId 与 processDefinitionKey 至少要给一个（都给时以 id 为准）。"));
        }

        // JWT 下发起人 / 租户只能来自令牌：与请求体不一致直接 403（防止冒充他人或跨租户启动）
        var startUserId = RequestIdentity.User(request.StartUserId, requestContext);
        var tenantId = RequestIdentity.Tenant(request.TenantId, requestContext);

        // 按 id 启动时引擎不接收 tenant 参数，这里先确认定义属于生效租户，避免跨租户启动
        if (byId && requestContext.IsTenantRestricted)
        {
            var definitions = await engine
                .FindProcessDefinitionsAsync(
                    new ProcessDefinitionQuery(TenantId: RequestIdentity.Tenant(null, requestContext)), ct)
                .ConfigureAwait(false);

            if (!definitions.Any(definition => string.Equals(
                    definition.Id,
                    request.ProcessDefinitionId,
                    StringComparison.Ordinal)))
            {
                throw new EngineObjectNotFoundException(
                    $"流程定义 '{request.ProcessDefinitionId}' 不存在。");
            }
        }

        var variables = JsonVariableReader.Read(request.Variables);

        var processInstanceId = byId
            ? await engine.StartProcessInstanceAsync(
                request.ProcessDefinitionId!,
                request.BusinessKey,
                startUserId,
                variables,
                ct).ConfigureAwait(false)
            : await engine.StartProcessInstanceByKeyAsync(
                request.ProcessDefinitionKey!,
                request.BusinessKey,
                startUserId,
                variables,
                tenantId,
                ct).ConfigureAwait(false);

        // 创建资源返回 201 + Location，指向新实例详情
        return Results.Created(
            $"/api/runtime/process-instances/{processInstanceId}",
            new StartProcessInstanceResponse(processInstanceId));
    }

    /// <summary>
    /// 删除（撤销）运行中的流程实例：待办 / 变量 / 执行树清理，历史保留并写入删除原因。
    /// <para><c>deleteReason</c> 走 query string（与 Activiti 的 deleteProcessInstance 第 2 参数同义）。</para>
    /// </summary>
    private static async Task<IResult> DeleteProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceDelete, "删除流程实例");

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.InstanceDelete,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { deleteReason },
            async () =>
            {
                await engine.DeleteProcessInstanceAsync(processInstanceId, deleteReason, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>挂起流程实例：待办仍可查，但不能完成 / 认领（对应 suspendProcessInstanceById）。</summary>
    private static async Task<IResult> SuspendProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceSuspend, "挂起流程实例");

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.InstanceSuspend,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            detail: null,
            async () =>
            {
                await engine.SuspendProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>激活流程实例（与挂起对称）。</summary>
    private static async Task<IResult> ActivateProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceSuspend, "激活流程实例");

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.InstanceActivate,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            detail: null,
            async () =>
            {
                await engine.ActivateProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GetProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        CancellationToken ct)
    {
        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 不存在。",
                Code: "process_instance_not_found"));
        }

        TenantGuard.Ensure(requestContext, execution.TenantId, "流程实例", processInstanceId);

        return Results.Ok(ProcessInstanceResponse.From(execution));
    }

    /// <summary>
    /// 查运行中的流程实例（在办监控 / 按业务单号反查）。
    /// <para>
    /// 过滤：<c>processDefinitionId</c> / <c>processDefinitionKey</c> / <c>businessKey</c> /
    /// <c>startUserId</c> / <c>tenantId</c> / <c>isActive</c> / <c>startedAfter</c> / <c>startedBefore</c>；
    /// 分页 <c>firstResult</c> / <c>maxResults</c>（默认 20，上限 200），总数放 <c>X-Total-Count</c>。
    /// </para>
    /// <para>实例结束后运行态行已删除，请改查 <c>GET /history/process-instances</c>。</para>
    /// </summary>
    private static async Task<IResult> FindProcessInstancesAsync(
        string? processDefinitionId,
        string? processDefinitionKey,
        string? businessKey,
        string? startUserId,
        string? tenantId,
        bool? isActive,
        DateTime? startedAfter,
        DateTime? startedBefore,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tenant = RequestIdentity.Tenant(tenantId, requestContext);

        if (!Paging.TryRead(firstResult, maxResults, out var first, out var size, out var pagingError))
        {
            return pagingError!;
        }

        var query = new ProcessInstanceQuery(
            processDefinitionId,
            processDefinitionKey,
            businessKey,
            startUserId,
            tenant,
            isActive,
            UtcTime.Normalize(startedAfter),
            UtcTime.Normalize(startedBefore),
            first,
            size);

        var instances = await engine.FindProcessInstancesAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            instances.Select(ProcessInstanceResponse.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    /// <summary>流程实例跳转：把实例从当前等待节点搬到目标节点（管理动作，会删除当前待办）。</summary>
    private static async Task<IResult> JumpProcessInstanceAsync(
        string processInstanceId,
        JumpProcessInstanceRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetActivityId))
        {
            return Results.BadRequest(new ErrorResponse("targetActivityId 必填（目标节点 id，可填结束事件强制办结）。"));
        }

        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceJump, "跳转流程实例");

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.InstanceJump,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { targetActivityId = request.TargetActivityId, reason = request.Reason },
            async () =>
            {
                await engine
                    .MoveActivityToAsync(processInstanceId, request.TargetActivityId, request.Reason, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 取消指定活动上的活动实例（Camunda <c>cancelActivityInstance</c> 的裁剪版）。
    /// <para>
    /// 与跳转不同：不搬令牌，只让这一处令牌消失，其它并行分支不受影响；
    /// 取消后若实例已无活动分支，实例随之正常结束。任务 / 定时作业 / 历史一并收口。
    /// </para>
    /// </summary>
    private static async Task<IResult> CancelActivityAsync(
        string processInstanceId,
        string activityId,
        CancelActivityRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceJump, "取消活动实例");

        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "控制台取消活动" : request.Reason.Trim();

        try
        {
            await engine
                .ExecuteAsync(new CancelActivityCommand(processInstanceId, activityId, reason), ct)
                .ConfigureAwait(false);
        }
        catch (EngineObjectNotFoundException exception)
        {
            return Results.NotFound(new ErrorResponse(exception.Message, Code: "not_found"));
        }
        catch (EngineException exception)
        {
            // 活动已结束 / 令牌不在该节点 / 被并发取消：409，调用方据此刷新而不是盲目重试
            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.InstanceActivityCancel,
                AdminAuditTargets.ProcessInstance,
                processInstanceId,
                processInstanceId,
                new { activityId, reason },
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            return Results.Conflict(new ErrorResponse(exception.Message));
        }

        await auditor.RecordAsync(
            requestContext,
            AdminAuditActions.InstanceActivityCancel,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { activityId, reason },
            success: true,
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// 在指定活动之前启动一个新令牌（Camunda <c>startBeforeActivity</c> 的裁剪版）。
    /// <para>
    /// 现有执行不动，额外挂一个并发子执行从目标活动继续推进 —— 目标活动会真的开始执行
    /// （用户任务建待办、服务任务执行、出流照常），相当于临时多开一条分支。
    /// </para>
    /// </summary>
    private static async Task<IResult> StartBeforeActivityAsync(
        string processInstanceId,
        string activityId,
        StartBeforeActivityRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceJump, "在活动之前启动");

        var detail = new { activityId, reason = request?.Reason };

        try
        {
            await engine
                .ExecuteAsync(new StartBeforeActivityCommand(processInstanceId, activityId), ct)
                .ConfigureAwait(false);
        }
        catch (EngineObjectNotFoundException exception)
        {
            return Results.NotFound(new ErrorResponse(exception.Message, Code: "not_found"));
        }
        catch (EngineException exception)
        {
            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.InstanceActivityStart,
                AdminAuditTargets.ProcessInstance,
                processInstanceId,
                processInstanceId,
                detail,
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            return Results.Conflict(new ErrorResponse(exception.Message));
        }

        await auditor.RecordAsync(
            requestContext,
            AdminAuditActions.InstanceActivityStart,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            detail,
            success: true,
            ct: ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// 流程实例迁移（定义升级后的存量实例处置）。
    /// <para>
    /// <c>validateOnly=true</c> 时只返回计划（映射明细 / 待取消待办 / 风险提示），不写任何数据；
    /// 否则在同一事务内切定义、取消旧待办、重排定时器并在目标节点重建待办。
    /// </para>
    /// </summary>
    private static async Task<IResult> MigrateProcessInstanceAsync(
        string processInstanceId,
        MigrateProcessInstanceRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetProcessDefinitionId))
        {
            return Results.BadRequest(new ErrorResponse("targetProcessDefinitionId 必填（目标流程定义 id）。"));
        }

        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceJump, "迁移流程实例");

        Dictionary<string, string>? mappings = request.ActivityMappings is null
            ? null
            : new Dictionary<string, string>(request.ActivityMappings, StringComparer.Ordinal);

        var migration = new ProcessInstanceMigrationRequest(
            processInstanceId,
            request.TargetProcessDefinitionId,
            mappings,
            request.Reason,
            requestContext.UserId);

        if (request.ValidateOnly)
        {
            var preview = await engine.ValidateMigrationAsync(migration, ct).ConfigureAwait(false);

            return Results.Ok(MigrationPlanResponse.From(preview, applied: false));
        }

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.InstanceMigrate,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new
            {
                targetProcessDefinitionId = request.TargetProcessDefinitionId,
                activityMappingCount = mappings?.Count ?? 0,
                reason = request.Reason,
            },
            async () =>
            {
                var plan = await engine.MigrateProcessInstanceAsync(migration, ct).ConfigureAwait(false);

                return Results.Ok(MigrationPlanResponse.From(plan, applied: true));
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>查流程实例的迁移记录（按迁移时间倒序）。</summary>
    private static async Task<IResult> FindMigrationsAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        var records = await engine.FindMigrationsAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(records.Select(MigrationRecordResponse.From).ToArray());
    }

    // ─────────────────────────── 任务 ───────────────────────────

    /// <summary>
    /// 查待办。条件：<c>assignee</c> / <c>candidateUser</c> / <c>processInstanceId</c>
    /// （至少给一个）+ 可选收窄的 <c>processDefinitionId</c> / <c>taskDefinitionKey</c>。
    /// <para>
    /// 分页参数：<c>firstResult</c>（跳过条数，默认 0）、<c>maxResults</c>（默认 20，上限 200）。
    /// 响应仍是任务数组（不改变既有调用方），总数放在 <c>X-Total-Count</c> 响应头 ——
    /// 前端分页器需要它，而 Activiti 的 <c>TaskQuery.count()</c> 也是独立一次查询。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindTasksAsync(
        string? assignee,
        string? candidateUser,
        string? candidateGroup,
        string? processInstanceId,
        string? processDefinitionId,
        string? taskDefinitionKey,
        bool? suspended,
        DateTime? dueBefore,
        string? tenantId,
        string? sortBy,
        string? sortOrder,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        // 「我的待办」：一个条件都不给时，用鉴权上下文里的操作人兜底
        var unfiltered = false;

        if (string.IsNullOrWhiteSpace(assignee)
            && string.IsNullOrWhiteSpace(candidateUser)
            && string.IsNullOrWhiteSpace(candidateGroup)
            && string.IsNullOrWhiteSpace(processInstanceId))
        {
            assignee = requestContext.UserId;

            // 管理视图（实例监控的待办列表）：兜不了底时放行全量，语义与统计端点一致
            unfiltered = string.IsNullOrWhiteSpace(assignee) && IsAdminPrincipal(requestContext);
        }

        if (!Paging.TryRead(firstResult, maxResults, out var first, out var size, out var pagingError))
        {
            return pagingError!;
        }

        var query = new TaskQuery(
            Assignee: assignee,
            CandidateUser: candidateUser,
            ProcessInstanceId: processInstanceId,
            FirstResult: first,
            MaxResults: size,
            ProcessDefinitionId: processDefinitionId,
            TaskDefinitionKey: taskDefinitionKey,
            CandidateGroup: candidateGroup,
            Suspended: suspended,
            DueBefore: UtcTime.Normalize(dueBefore),
            TenantId: RequestIdentity.Tenant(tenantId, requestContext),
            AllUsers: unfiltered,
            SortBy: sortBy,
            Descending: string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase));

        var tasks = await engine.FindTasksAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountTasksAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            tasks.Select(TaskResponse.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    /// <summary>任务详情：列表字段 + 描述 / 分类 / 业务主键 / 挂起 / 委派状态 + 身份关系。</summary>
    private static async Task<IResult> GetTaskAsync(
        string taskId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await engine.GetTaskAsync(taskId, ct).ConfigureAwait(false);

        if (task is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"任务 '{taskId}' 不存在（可能已完成或流程已推进）。",
                Code: "task_not_found"));
        }

        TenantGuard.Ensure(requestContext, task.TenantId, "任务", taskId);

        var identityLinks = await engine.FindTaskIdentityLinksAsync(taskId, ct).ConfigureAwait(false);

        return Results.Ok(TaskDetailResponse.From(task, identityLinks));
    }

    /// <summary>任务身份关系（候选人 / 候选组 / 办理人 / 所有者）。</summary>
    private static async Task<IResult> FindTaskIdentityLinksAsync(
        string taskId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await engine.GetTaskAsync(taskId, ct).ConfigureAwait(false);

        if (task is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"任务 '{taskId}' 不存在（可能已完成或流程已推进）。",
                Code: "task_not_found"));
        }

        TenantGuard.Ensure(requestContext, task.TenantId, "任务", taskId);

        var identityLinks = await engine.FindTaskIdentityLinksAsync(taskId, ct).ConfigureAwait(false);

        return Results.Ok(identityLinks.Select(IdentityLinkResponse.From).ToArray());
    }

    /// <summary>流程实例级身份关系（参与者 / 发起人 / 候选组）。</summary>
    private static async Task<IResult> FindProcessInstanceIdentityLinksAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var instance = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (instance is null || !string.IsNullOrEmpty(instance.ParentId))
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 不存在（可能已结束）。",
                Code: "process_instance_not_found"));
        }

        TenantGuard.Ensure(requestContext, instance.TenantId, "流程实例", processInstanceId);

        var identityLinks = await engine
            .FindProcessInstanceIdentityLinksAsync(processInstanceId, ct)
            .ConfigureAwait(false);

        return Results.Ok(identityLinks.Select(IdentityLinkResponse.From).ToArray());
    }

    /// <summary>
    /// 批量完成任务（逐项执行，不是原子操作）。
    /// <para>
    /// 每一项都走与单条完成相同的授权校验（办理人 / 候选组 / 管理员），
    /// 单项失败只影响该条；返回逐项结果，便于前端提示「哪几条没批成功」。
    /// </para>
    /// </summary>
    private static async Task<IResult> BatchCompleteTasksAsync(
        BatchCompleteTasksRequest? request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (request?.TaskIds is not { Count: > 0 } taskIds)
        {
            return Results.BadRequest(new ErrorResponse(
                "taskIds 不能为空。",
                Code: "validation_error"));
        }

        if (taskIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse(
                $"单次批量操作最多 {MaxBatchSize} 条。",
                Code: "validation_error"));
        }

        var variables = JsonVariableReader.Read(request.Variables);
        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

                await TaskAuthorization.EnsureCanCompleteAsync(task, requestContext, engine, ct)
                    .ConfigureAwait(false);

                await engine.CompleteTaskAsync(taskId, variables, expectedRevision: null, ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException exception)
            {
                items.Add(new BatchItemResult(taskId, false, exception.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    /// <summary>
    /// 转办（改派）：把待办直接改给指定用户，<c>assignee</c> 为空表示退回未分配。
    /// <para>管理动作，不校验是否已被他人认领，也不改 <c>CLAIM_TIME_</c>（与 Activiti <c>setAssignee</c> 同语义）。</para>
    /// </summary>
    private static async Task<IResult> SetAssigneeAsync(
        string taskId,
        SetAssigneeRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "转办", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskReassign,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new { from = task.Assignee, to = request?.Assignee },
            async () =>
            {
                await engine.SetAssigneeAsync(taskId, request?.Assignee, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 修改任务字段：名称 / 描述 / 优先级 / 到期时间 / 分类 / 所有者。
    /// <para>未提供的字段保持不变；<c>owner</c> 传空串表示清空。</para>
    /// </summary>
    private static async Task<IResult> UpdateTaskAsync(
        string taskId,
        UpdateTaskRequest? request,
        HttpRequest httpRequest,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new ErrorResponse("请求体不能为空。"));
        }

        var expectedRevision = Preconditions.ReadExpectedRevision(httpRequest);
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "修改任务", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskUpdate,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new
            {
                name = request.Name,
                priority = request.Priority,
                category = request.Category,
                owner = request.Owner,
                dueDate = request.DueDate,
                clearDueDate = request.ClearDueDate == true,
                // 描述可能含敏感信息：只记「是否改动」，不记内容
                descriptionChanged = request.Description is not null || request.ClearDescription == true,
            },
            async () =>
            {
                await engine.UpdateTaskAsync(
                        taskId,
                        new TaskUpdate(
                            request.Name,
                            request.Description,
                            request.Priority,
                            request.DueDate,
                            request.Category,
                            request.Owner,
                            request.ClearDueDate == true,
                            request.ClearDescription == true,
                            expectedRevision),
                        ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>加候选用户（任务级 candidate 关系）。</summary>
    private static async Task<IResult> AddCandidateUserAsync(
        string taskId,
        TaskCandidateRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.UserId))
        {
            return Results.BadRequest(new ErrorResponse("userId 必填。"));
        }

        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "增加候选人", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskCandidateAdd,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new { kind = "user", userId = request.UserId },
            async () =>
            {
                await engine.AddTaskCandidateUsersAsync(taskId, [request.UserId], ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>移除候选用户（不影响流程实例级参与者记录）；关系不存在返回 404。</summary>
    private static async Task<IResult> RemoveCandidateUserAsync(
        string taskId,
        string userId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "移除候选人", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskCandidateRemove,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new { kind = "user", userId },
            async () =>
            {
                var links = await engine.FindTaskIdentityLinksAsync(taskId, ct).ConfigureAwait(false);

                if (!links.Any(link =>
                        string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                        && string.Equals(link.UserId, userId, StringComparison.Ordinal)))
                {
                    return Results.NotFound(
                        new ErrorResponse($"任务 '{taskId}' 的候选用户 '{userId}' 不存在。"));
                }

                await engine.RemoveTaskCandidateUserAsync(taskId, userId, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>加候选组。</summary>
    private static async Task<IResult> AddCandidateGroupAsync(
        string taskId,
        TaskCandidateRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.GroupId))
        {
            return Results.BadRequest(new ErrorResponse("groupId 必填。"));
        }

        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "增加候选组", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskCandidateAdd,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new { kind = "group", groupId = request.GroupId },
            async () =>
            {
                await engine.AddTaskCandidateGroupsAsync(taskId, [request.GroupId], ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>移除候选组；关系不存在返回 404。</summary>
    private static async Task<IResult> RemoveCandidateGroupAsync(
        string taskId,
        string groupId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization
            .EnsureCanModerateAsync(task, requestContext, executionManager, "移除候选组", ct)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskCandidateRemove,
            AdminAuditTargets.Task,
            taskId,
            task.ProcessInstanceId,
            new { kind = "group", groupId },
            async () =>
            {
                var links = await engine.FindTaskIdentityLinksAsync(taskId, ct).ConfigureAwait(false);

                if (!links.Any(link =>
                        string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal)
                        && string.Equals(link.GroupId, groupId, StringComparison.Ordinal)))
                {
                    return Results.NotFound(
                        new ErrorResponse($"任务 '{taskId}' 的候选组 '{groupId}' 不存在。"));
                }

                await engine.RemoveTaskCandidateGroupAsync(taskId, groupId, ct).ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>委派任务：原办理人记入 owner，委派状态 PENDING（此时不能直接完成）。</summary>
    private static async Task<IResult> DelegateTaskAsync(
        string taskId,
        DelegateTaskRequest? request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.UserId))
        {
            return Results.BadRequest(new ErrorResponse("userId 必填。"));
        }

        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        TaskAuthorization.EnsureCanDelegate(task, requestContext);

        await engine.DelegateTaskAsync(taskId, request.UserId, ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>解决委派：办理人回到 owner，委派状态 RESOLVED（任务仍未完成）。</summary>
    private static async Task<IResult> ResolveTaskAsync(
        string taskId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        TaskAuthorization.EnsureCanResolve(task, requestContext);

        await engine.ResolveTaskAsync(taskId, ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// 任务表单元数据：只回传 <c>formKey</c> 与 <c>activiti:formProperty</c> 声明，
    /// 不渲染、不校验（与 B2.2「不做表单引擎」的定稿一致）。
    /// </summary>
    private static async Task<IResult> GetTaskFormAsync(
        string taskId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await engine.GetTaskAsync(taskId, ct).ConfigureAwait(false);

        if (task is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"任务 '{taskId}' 不存在（可能已完成或流程已推进）。",
                Code: "task_not_found"));
        }

        TenantGuard.Ensure(requestContext, task.TenantId, "任务", taskId);

        if (string.IsNullOrWhiteSpace(task.ProcessDefinitionId)
            || string.IsNullOrWhiteSpace(task.TaskDefinitionKey))
        {
            return Results.BadRequest(new ErrorResponse("任务缺少流程定义或节点信息，无法解析表单。"));
        }

        var model = await engine.GetBpmnModelAsync(task.ProcessDefinitionId, ct).ConfigureAwait(false);

        var userTask = model?
            .GetMainProcess()?
            .FlowElements
            .FirstOrDefault(element => string.Equals(element.Id, task.TaskDefinitionKey, StringComparison.Ordinal))
            as NetBpm.Bpmn.Model.UserTask;

        return userTask is null
            ? Results.NotFound(new ErrorResponse(
                $"流程定义中找不到用户任务节点 '{task.TaskDefinitionKey}'。",
                Code: "form_not_found"))
            : Results.Ok(FormMetadataResponse.From(userTask));
    }

    /// <summary>
    /// 签收任务：认领人取自请求体（JWT 下必须等于令牌 <c>sub</c>），缺省取令牌 / 头身份。
    /// <para>
    /// ⚠️ 与旧版本不同：<b>解析不出认领人时返回 400</b>，不再退化为「取消认领」；
    /// 取消认领请显式调用 <c>POST /tasks/{id}/unclaim</c>（或 <c>unclaim=true</c> 兼容旧客户端）。
    /// </para>
    /// </summary>
    private static async Task<IResult> ClaimTaskAsync(
        string taskId,
        ClaimTaskRequest? request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        // 兼容旧客户端的 {"unclaim": true}：显式取消认领与 /unclaim 同语义
        if (request?.Unclaim == true)
        {
            TaskAuthorization.EnsureCanUnclaim(task, requestContext);

            await engine.UnclaimTaskAsync(taskId, request.ExpectedRevision, ct).ConfigureAwait(false);
            return Results.NoContent();
        }

        // JWT 下认领人只能来自令牌；API Key / 开发模式允许显式传 userId。
        var userId = RequestIdentity.User(request?.UserId, requestContext);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.BadRequest(new ErrorResponse(
                "认领必须能解析出 userId（JWT 的 sub / X-User-Id / 请求体 userId）；"
                + "取消认领请调用 POST /api/runtime/tasks/{taskId}/unclaim。"));
        }

        await TaskAuthorization.EnsureCanClaimAsync(task, requestContext, engine, ct).ConfigureAwait(false);

        await engine.ClaimTaskAsync(taskId, userId, request?.ExpectedRevision, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>取消认领：只能由当前办理人或管理员操作；任务未签收时幂等返回 204。</summary>
    private static async Task<IResult> UnclaimTaskAsync(
        string taskId,
        ClaimTaskRequest? request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        TaskAuthorization.EnsureCanUnclaim(task, requestContext);

        await engine.UnclaimTaskAsync(taskId, request?.ExpectedRevision, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> CompleteTaskAsync(
        string taskId,
        CompleteTaskRequest? request,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await TaskAuthorization.EnsureCanCompleteAsync(task, requestContext, engine, ct)
            .ConfigureAwait(false);

        await engine.CompleteTaskAsync(
                taskId,
                JsonVariableReader.Read(request?.Variables),
                request?.ExpectedRevision,
                ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    // ─────────────────────────── 统计与批量 ───────────────────────────

    /// <summary>
    /// 待办统计：总数 / 逾期 / 今天到期 / 按定义分组。
    /// <para>
    /// 条件与 <c>GET /tasks</c> 同口径（assignee / candidateUser / candidateGroup 至少一个）；
    /// 条件全空时用操作人兜底（「我的待办」统计）。调用方具备管理能力且没有可兜底的身份时
    /// （开发模式匿名 / API Key 服务 / 持 <c>netbpm.admin</c> 的管理员），放行显式全量统计 ——
    /// 实例监控的全局「待办总数」本来就该跨办理人。
    /// </para>
    /// </summary>
    private static async Task<IResult> GetTaskStatsAsync(
        string? assignee,
        string? candidateUser,
        string? candidateGroup,
        string? processDefinitionId,
        string? tenantId,
        int? timeZoneOffsetMinutes,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var unfiltered = false;

        if (string.IsNullOrWhiteSpace(assignee)
            && string.IsNullOrWhiteSpace(candidateUser)
            && string.IsNullOrWhiteSpace(candidateGroup))
        {
            assignee = requestContext.UserId;

            // 「我的待办」兜不了底（匿名 / 服务身份没有操作人）→ 管理方放行全量，普通用户仍被引擎防呆拦下
            unfiltered = string.IsNullOrWhiteSpace(assignee) && IsAdminPrincipal(requestContext);
        }

        // 统计与列表同口径：强制带租户，避免跨租户数字泄露
        var query = new TaskQuery(
            Assignee: assignee,
            CandidateUser: candidateUser,
            ProcessDefinitionId: processDefinitionId,
            CandidateGroup: candidateGroup,
            TenantId: RequestIdentity.Tenant(tenantId, requestContext),
            AllUsers: unfiltered);

        var tasks = await engine.FindTasksAsync(query, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;

        // 「今天到期」按调用方时区计算自然日（默认 UTC）；把本地 00:00~24:00 换算回 UTC 区间比较
        var offset = timeZoneOffsetMinutes ?? 0;
        var localStart = now.AddMinutes(offset).Date;
        var todayStart = localStart.AddMinutes(-offset);
        var tomorrowStart = todayStart.AddDays(1);

        var overdue = tasks.Count(task => task.DueDate is { } due && due < now);
        var dueToday = tasks.Count(task =>
            task.DueDate is { } due && due >= todayStart && due < tomorrowStart);

        // 按流程 key 分组（同 key 多版本合并，业务上更直观）
        var byDefinition = tasks
            .GroupBy(task => DefinitionIdParser.Key(task.ProcessDefinitionId) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return Results.Ok(new TaskStatsResponse(tasks.Count, overdue, dueToday, byDefinition));
    }

    /// <summary>
    /// 批量转办：逐项执行，单条失败不影响其它项。
    /// <para>⚠️ 不是原子操作（每个任务一个命令 / 事务）；响应逐项给出成功与失败原因。</para>
    /// </summary>
    private static async Task<IResult> BatchSetAssigneeAsync(
        BatchTaskAssigneeRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.TaskIds is not { Count: > 0 } taskIds)
        {
            return Results.BadRequest(new ErrorResponse("taskIds 不能为空。"));
        }

        if (taskIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse($"单次批量操作最多 {MaxBatchSize} 条。"));
        }

        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                // 跨租户的任务按「不存在」逐项失败，不影响其它项
                var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

                await TaskAuthorization
                    .EnsureCanModerateAsync(task, requestContext, executionManager, "转办", ct)
                    .ConfigureAwait(false);

                await engine.SetAssigneeAsync(taskId, request.Assignee, ct).ConfigureAwait(false);

                // 批量转办逐项审计：一条记录对应一个任务，失败项也留痕
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.TaskBatchReassign,
                    AdminAuditTargets.Task,
                    taskId,
                    task.ProcessInstanceId,
                    new { from = task.Assignee, to = request.Assignee },
                    success: true,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException ex)
            {
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.TaskBatchReassign,
                    AdminAuditTargets.Task,
                    taskId,
                    processInstanceId: null,
                    detail: new { to = request.Assignee },
                    success: false,
                    error: ex.Message,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, false, ex.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    /// <summary>
    /// 批量改任务到期时间（逐项执行，不是原子操作）。
    /// <para>
    /// 常用场景：批量把积压任务的到期时间统一推到某个时间点，或批量清空到期时间。
    /// 单条失败（任务已被办 / 无权限）不影响其它条，逐项结果 + 逐项审计。
    /// </para>
    /// </summary>
    private static async Task<IResult> BatchSetTaskDueDateAsync(
        BatchTaskDueDateRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.TaskIds is not { Count: > 0 } taskIds)
        {
            return Results.BadRequest(new ErrorResponse("taskIds 不能为空。"));
        }

        if (taskIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse($"单次批量操作最多 {MaxBatchSize} 条。"));
        }

        if (request.DueDate is null && !request.ClearDueDate)
        {
            return Results.BadRequest(new ErrorResponse("dueDate 与 clearDueDate 至少要给一个。"));
        }

        var dueDate = UtcTime.Normalize(request.DueDate);
        var items = new List<BatchItemResult>(taskIds.Count);

        foreach (var taskId in taskIds)
        {
            try
            {
                var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

                await TaskAuthorization
                    .EnsureCanModerateAsync(task, requestContext, executionManager, "修改任务到期时间", ct)
                    .ConfigureAwait(false);

                await engine.UpdateTaskAsync(
                        taskId,
                        new TaskUpdate(DueDate: dueDate, ClearDueDate: request.ClearDueDate),
                        ct)
                    .ConfigureAwait(false);

                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.TaskUpdate,
                    AdminAuditTargets.Task,
                    taskId,
                    task.ProcessInstanceId,
                    new { dueDate, clearDueDate = request.ClearDueDate, batch = true },
                    success: true,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, true, null));
            }
            catch (EngineException ex)
            {
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.TaskUpdate,
                    AdminAuditTargets.Task,
                    taskId,
                    processInstanceId: null,
                    detail: new { dueDate, clearDueDate = request.ClearDueDate, batch = true },
                    success: false,
                    error: ex.Message,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(taskId, false, ex.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    /// <summary>
    /// 批量迁移流程实例到同一目标定义版本（逐项执行，不是原子操作）。
    /// <para>
    /// 迁移不可逆且会改写运行态，逐项结果 + 逐项审计；单项校验失败（活动映射对不上、
    /// 目标版本挂起等）只影响该条，其余照常迁移。
    /// </para>
    /// </summary>
    private static async Task<IResult> BatchMigrateProcessInstancesAsync(
        BatchMigrateInstancesRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.ProcessInstanceIds is not { Count: > 0 } instanceIds)
        {
            return Results.BadRequest(new ErrorResponse("processInstanceIds 不能为空。"));
        }

        if (instanceIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse($"单次批量操作最多 {MaxBatchSize} 条。"));
        }

        if (string.IsNullOrWhiteSpace(request.TargetProcessDefinitionId))
        {
            return Results.BadRequest(new ErrorResponse("targetProcessDefinitionId 必填（目标流程定义 id）。"));
        }

        // 授权在循环外统一判定：缺 scope 整体 403，而不是逐项失败
        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceJump, "批量迁移流程实例");

        var mappings = request.ActivityMappings is null
            ? null
            : new Dictionary<string, string>(request.ActivityMappings, StringComparer.Ordinal);

        var items = new List<BatchItemResult>(instanceIds.Count);

        foreach (var instanceId in instanceIds)
        {
            var detail = new
            {
                targetProcessDefinitionId = request.TargetProcessDefinitionId,
                activityMappingCount = mappings?.Count ?? 0,
                reason = request.Reason,
                batch = true,
            };

            try
            {
                await EnsureInstanceAccessAsync(instanceId, requestContext, executionManager, ct)
                    .ConfigureAwait(false);

                await engine
                    .MigrateProcessInstanceAsync(
                        new ProcessInstanceMigrationRequest(
                            instanceId,
                            request.TargetProcessDefinitionId,
                            mappings,
                            request.Reason,
                            requestContext.UserId),
                        ct)
                    .ConfigureAwait(false);

                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.InstanceMigrate,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    detail,
                    success: true,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException ex)
            {
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.InstanceMigrate,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    detail,
                    success: false,
                    error: ex.Message,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, ex.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    /// <summary>批量删除运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchDeleteProcessInstancesAsync(
        BatchDeleteInstancesRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.ProcessInstanceIds is not { Count: > 0 } instanceIds)
        {
            return Results.BadRequest(new ErrorResponse("processInstanceIds 不能为空。"));
        }

        if (instanceIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse($"单次批量操作最多 {MaxBatchSize} 条。"));
        }

        // 授权在循环外统一判定：缺 scope 整体 403，而不是逐项失败
        AdminAuthorization.Ensure(requestContext, TaskScopes.AdminInstanceDelete, "批量删除流程实例");

        var items = new List<BatchItemResult>(instanceIds.Count);

        foreach (var instanceId in instanceIds)
        {
            try
            {
                await EnsureInstanceAccessAsync(instanceId, requestContext, executionManager, ct)
                    .ConfigureAwait(false);

                await engine.DeleteProcessInstanceAsync(instanceId, request.DeleteReason, ct)
                    .ConfigureAwait(false);

                // 批量删除逐项审计：一条记录对应一个实例，失败项也留痕
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.InstanceBatchDelete,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    new { deleteReason = request.DeleteReason },
                    success: true,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException ex)
            {
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.InstanceBatchDelete,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    new { deleteReason = request.DeleteReason },
                    success: false,
                    error: ex.Message,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, ex.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    /// <summary>批量挂起运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchSuspendProcessInstancesAsync(
        BatchInstanceSuspensionRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct) =>
        await BatchSetInstanceSuspensionAsync(
                suspend: true, request, requestContext, executionManager, engine, auditor, ct)
            .ConfigureAwait(false);

    /// <summary>批量激活运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchActivateProcessInstancesAsync(
        BatchInstanceSuspensionRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct) =>
        await BatchSetInstanceSuspensionAsync(
                suspend: false, request, requestContext, executionManager, engine, auditor, ct)
            .ConfigureAwait(false);

    private static async Task<IResult> BatchSetInstanceSuspensionAsync(
        bool suspend,
        BatchInstanceSuspensionRequest? request,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.ProcessInstanceIds is not { Count: > 0 } instanceIds)
        {
            return Results.BadRequest(new ErrorResponse("processInstanceIds 不能为空。"));
        }

        if (instanceIds.Count > MaxBatchSize)
        {
            return Results.BadRequest(new ErrorResponse($"单次批量操作最多 {MaxBatchSize} 条。"));
        }

        AdminAuthorization.Ensure(
            requestContext,
            TaskScopes.AdminInstanceSuspend,
            suspend ? "批量挂起流程实例" : "批量激活流程实例");

        var action = suspend ? AdminAuditActions.InstanceSuspend : AdminAuditActions.InstanceActivate;
        var items = new List<BatchItemResult>(instanceIds.Count);

        foreach (var instanceId in instanceIds)
        {
            try
            {
                await EnsureInstanceAccessAsync(instanceId, requestContext, executionManager, ct)
                    .ConfigureAwait(false);

                if (suspend)
                {
                    await engine.SuspendProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
                }
                else
                {
                    await engine.ActivateProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
                }

                await auditor.RecordAsync(
                    requestContext,
                    action,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    new { batch = true },
                    success: true,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException ex)
            {
                await auditor.RecordAsync(
                    requestContext,
                    action,
                    AdminAuditTargets.ProcessInstance,
                    instanceId,
                    instanceId,
                    new { batch = true },
                    success: false,
                    error: ex.Message,
                    ct: ct).ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, ex.Message));
            }
        }

        return Results.Ok(ToBatchResult(items));
    }

    private static BatchResultResponse ToBatchResult(List<BatchItemResult> items)
    {
        var succeeded = items.Count(item => item.Success);

        return new BatchResultResponse(items.Count, succeeded, items.Count - succeeded, items);
    }

    // ─────────────────────────── 变量与历史 ───────────────────────────

    private static async Task<IResult> GetVariablesAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var instance = await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessInstanceVariablesAsync(instance, processInstanceId, requestContext, engine, ct)
            .ConfigureAwait(false);

        // 运行态变量只对「还在跑」的实例成立：实例已结束 / 已删除时返回 404，
        // 调用方（管理端实例详情抽屉）据此回落到历史变量，而不是把它当成一次失败。
        var running = instance ?? await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (running is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 不在运行中（可能已结束或已删除）。",
                Code: "process_instance_not_running"));
        }

        var variables = await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);
        return Results.Ok(variables);
    }

    /// <summary>
    /// 写流程实例变量（运行期补偿 / 纠错）。请求体即 JSON 值，与任务局部变量同一套类型映射。
    /// <para>变量已有则更新，没有则在流程实例作用域新建；历史行同步更新。</para>
    /// </summary>
    private static async Task<IResult> SetVariableAsync(
        string processInstanceId,
        string name,
        JsonElement? value,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Results.BadRequest(new ErrorResponse("请求体不能为空（不支持写入 null 变量）。"));
        }

        var instance = await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        // 参与人可写自己的变量；越权直写需要 netbpm.admin.variables.write
        await VariableAuthorization
            .EnsureCanAccessInstanceVariablesAsync(
                instance, processInstanceId, requestContext, engine, ct, TaskScopes.AdminVariablesWrite)
            .ConfigureAwait(false);

        var variables = JsonVariableReader.Read(new Dictionary<string, JsonElement> { [name] = value.Value });

        if (variables is null || !variables.TryGetValue(name, out var parsedValue))
        {
            return Results.BadRequest(new ErrorResponse(
                $"变量 '{name}' 的值为 null，未写入；删除变量请调用 DELETE。"));
        }

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.VariableWrite,
            AdminAuditTargets.Variable,
            $"{processInstanceId}:{name}",
            processInstanceId,
            new { name, valueType = value.Value.ValueKind.ToString(), scope = "process_instance" },
            async () =>
            {
                await engine
                    .SetVariableAsync(processInstanceId, name, parsedValue, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>批量写流程实例变量（同一事务）。请求体为 <c>{ "name": value, ... }</c>。</summary>
    private static async Task<IResult> SetVariablesAsync(
        string processInstanceId,
        Dictionary<string, JsonElement>? variables,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (variables is null || variables.Count == 0)
        {
            return Results.BadRequest(new ErrorResponse("请求体必须是非空的变量字典。"));
        }

        var instance = await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessInstanceVariablesAsync(
                instance, processInstanceId, requestContext, engine, ct, TaskScopes.AdminVariablesWrite)
            .ConfigureAwait(false);

        var values = JsonVariableReader.Read(variables);

        if (values is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "变量值不能全部为 null：本次请求没有可写入的变量（删除变量请用 DELETE）。"));
        }

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.VariableWrite,
            AdminAuditTargets.Variable,
            processInstanceId,
            processInstanceId,
            new { names = variables.Keys.Order(StringComparer.Ordinal).ToArray(), scope = "process_instance" },
            async () =>
            {
                await engine
                    .SetVariablesAsync(
                        processInstanceId,
                        new Dictionary<string, object?>(values, StringComparer.Ordinal),
                        ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>删除流程实例作用域上的变量（B2.4）：沿作用域链查找，历史行保留。</summary>
    private static async Task<IResult> RemoveVariableAsync(
        string processInstanceId,
        string name,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var instance = await EnsureInstanceAccessAsync(processInstanceId, requestContext, executionManager, ct)
            .ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessInstanceVariablesAsync(
                instance, processInstanceId, requestContext, engine, ct, TaskScopes.AdminVariablesWrite)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.VariableDelete,
            AdminAuditTargets.Variable,
            $"{processInstanceId}:{name}",
            processInstanceId,
            new { name, scope = "process_instance" },
            async () => await engine.RemoveVariableAsync(processInstanceId, name, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new ErrorResponse($"变量 '{name}' 不存在。", Code: "variable_not_found")),
            ct).ConfigureAwait(false);
    }

    // ─────────────────────────── 任务局部变量（B2.4）───────────────────────────

    private static async Task<IResult> GetTaskVariablesAsync(
        string taskId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessTaskVariablesAsync(task, requestContext, executionManager, engine, ct)
            .ConfigureAwait(false);

        var variables = await engine.GetTaskVariablesAsync(taskId, ct).ConfigureAwait(false);
        return Results.Ok(variables);
    }

    /// <summary>写任务局部变量。请求体即变量值（JSON 原文，走 <see cref="JsonVariableReader"/> 同一套类型映射）。</summary>
    private static async Task<IResult> SetTaskVariableAsync(
        string taskId,
        string name,
        JsonElement? value,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Results.BadRequest(new ErrorResponse("请求体不能为空（不支持写入 null 变量）。"));
        }

        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessTaskVariablesAsync(
                task, requestContext, executionManager, engine, ct, TaskScopes.AdminVariablesWrite)
            .ConfigureAwait(false);

        var variables = JsonVariableReader.Read(new Dictionary<string, JsonElement> { [name] = value.Value });

        if (variables is null || !variables.TryGetValue(name, out var parsedValue))
        {
            return Results.BadRequest(new ErrorResponse(
                $"任务局部变量 '{name}' 的值为 null，未写入；删除变量请调用 DELETE。"));
        }

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskVariableWrite,
            AdminAuditTargets.Variable,
            $"{taskId}:{name}",
            task.ProcessInstanceId,
            new { name, valueType = value.Value.ValueKind.ToString(), scope = "task", taskId },
            async () =>
            {
                await engine.SetTaskVariableAsync(taskId, name, parsedValue, ct)
                    .ConfigureAwait(false);

                return Results.NoContent();
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<IResult> RemoveTaskVariableAsync(
        string taskId,
        string name,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var task = await EnsureTaskAccessAsync(taskId, requestContext, engine, ct).ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessTaskVariablesAsync(
                task, requestContext, executionManager, engine, ct, TaskScopes.AdminVariablesWrite)
            .ConfigureAwait(false);

        return await AuditAsync(
            auditor,
            requestContext,
            AdminAuditActions.TaskVariableDelete,
            AdminAuditTargets.Variable,
            $"{taskId}:{name}",
            task.ProcessInstanceId,
            new { name, scope = "task", taskId },
            async () => await engine.RemoveTaskVariableAsync(taskId, name, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound(new ErrorResponse($"任务局部变量 '{name}' 不存在。", Code: "variable_not_found")),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 流程图着色数据（§6.2 第 5 项）：<c>END_TIME_</c> 为空表示该节点正在执行。
    /// </summary>
    private static async Task<IResult> FindHistoricActivitiesAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        await EnsureHistoryAccessAsync(processInstanceId, requestContext, executionManager, engine, ct)
            .ConfigureAwait(false);

        var activities = await engine.FindHistoricActivitiesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(activities.Select(HistoricActivityResponse.From).ToArray());
    }

    /// <summary>流程图数据：BPMN XML（含 BPMNDI）+ 活动高亮序列，供只读实例视图一次取全。</summary>
    private static async Task<IResult> GetDiagramAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        await EnsureHistoryAccessAsync(processInstanceId, requestContext, executionManager, engine, ct)
            .ConfigureAwait(false);

        var diagram = await engine.GetDiagramAsync(processInstanceId, ct).ConfigureAwait(false);

        if (diagram is null)
        {
            return Results.NotFound(new ErrorResponse($"流程实例 '{processInstanceId}' 不存在，且没有历史记录。"));
        }

        return Results.Ok(new DiagramResponse(
            diagram.ProcessInstanceId,
            diagram.ProcessDefinitionId,
            diagram.BpmnXml,
            diagram.IsRunning,
            [.. diagram.Activities.Select(HistoricActivityResponse.From)]));
    }

    // ─────────────────────────── 历史 ───────────────────────────

    /// <summary>
    /// 分页查历史流程实例（已办结检索 / 审计 / 报表）。
    /// <para>
    /// 过滤：<c>processDefinitionId</c> / <c>processDefinitionKey</c> / <c>businessKey</c> /
    /// <c>startUserId</c> / <c>finished</c> / <c>deleted</c> / <c>startedAfter</c> / <c>startedBefore</c> /
    /// <c>endedAfter</c> / <c>endedBefore</c>；分页默认 20、上限 200，总数放 <c>X-Total-Count</c>。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindHistoricProcessInstancesAsync(
        string? processDefinitionId,
        string? processDefinitionKey,
        string? businessKey,
        string? startUserId,
        bool? finished,
        bool? deleted,
        DateTime? startedAfter,
        DateTime? startedBefore,
        DateTime? endedAfter,
        DateTime? endedBefore,
        string? tenantId,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (!Paging.TryRead(firstResult, maxResults, out var first, out var size, out var pagingError))
        {
            return pagingError!;
        }

        var query = new HistoricProcessInstanceQuery(
            processDefinitionId,
            processDefinitionKey,
            businessKey,
            startUserId,
            finished,
            deleted,
            UtcTime.Normalize(startedAfter),
            UtcTime.Normalize(startedBefore),
            UtcTime.Normalize(endedAfter),
            UtcTime.Normalize(endedBefore),
            first,
            size,
            RequestIdentity.Tenant(tenantId, requestContext));

        var instances = await engine.FindHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountHistoricProcessInstancesAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            instances.Select(HistoricProcessInstanceResponse.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    /// <summary>
    /// 历史变量：回答「这个实例上出现过哪些变量、最后是什么值」（运行态删除后仍在）。
    /// <para>变量是敏感数据：与运行态变量同口径做参与人校验（发起人 / 运行中任务的参与人）。</para>
    /// </summary>
    private static async Task<IResult> FindHistoricVariablesAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var running = await EnsureHistoryAccessAsync(processInstanceId, requestContext, executionManager, engine, ct)
            .ConfigureAwait(false);

        await VariableAuthorization
            .EnsureCanAccessHistoricVariablesAsync(running, processInstanceId, requestContext, engine, ct)
            .ConfigureAwait(false);

        var variables = await engine.FindHistoricVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(variables.Select(HistoricVariableResponse.From).ToArray());
    }

    private static async Task<IResult> FindHistoricProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var instance = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        if (instance is null)
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 没有历史记录（历史写入可能未启用：NetBpm:EnableHistory）。"));
        }

        TenantGuard.Ensure(requestContext, instance.TenantId, "流程实例", processInstanceId);

        return Results.Ok(HistoricProcessInstanceResponse.From(instance));
    }

    /// <summary>
    /// 历史任务（已办列表）：可选 <c>assignee</c> / <c>onlyFinished</c> 收窄，
    /// 分页参数与待办一致（<c>firstResult</c> / <c>maxResults</c>），总数放 <c>X-Total-Count</c>。
    /// </summary>
    private static async Task<IResult> FindHistoricTasksAsync(
        string? processInstanceId,
        string? assignee,
        bool? onlyFinished,
        string? processDefinitionId,
        string? taskDefinitionKey,
        DateTime? finishedAfter,
        DateTime? finishedBefore,
        string? tenantId,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        HttpResponse response,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assignee)
            && string.IsNullOrWhiteSpace(processInstanceId)
            && string.IsNullOrWhiteSpace(processDefinitionId)
            && string.IsNullOrWhiteSpace(taskDefinitionKey))
        {
            assignee = requestContext.UserId;
        }

        if (!Paging.TryRead(firstResult, maxResults, out var first, out var size, out var pagingError))
        {
            return pagingError!;
        }

        var query = new HistoricTaskQuery(
            processInstanceId,
            assignee,
            onlyFinished ?? false,
            first,
            size,
            processDefinitionId,
            taskDefinitionKey,
            UtcTime.Normalize(finishedAfter),
            UtcTime.Normalize(finishedBefore),
            RequestIdentity.Tenant(tenantId, requestContext));

        var tasks = await engine.FindHistoricTasksAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountHistoricTasksAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            tasks.Select(HistoricTaskResponse.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope ?? false);
    }

    // ─────────────────────────── 审计辅助 ───────────────────────────

    /// <summary>
    /// 执行管理动作并按结果写审计：成功与失败都留痕，失败仍照常抛给全局异常处理器。
    /// <para>审计写入本身失败不会改变业务结果（见 <see cref="AdminActionAuditor"/>）。</para>
    /// </summary>
    private static Task<IResult> AuditAsync(
        AdminActionAuditor auditor,
        RequestContext requestContext,
        string action,
        string targetType,
        string targetId,
        string? processInstanceId,
        object? detail,
        Func<Task<IResult>> execute,
        CancellationToken ct) =>
        AuditedAction.RunAsync(
            auditor,
            requestContext,
            action,
            targetType,
            targetId,
            processInstanceId,
            detail,
            execute,
            ct);

    // ─────────────────────────── 租户隔离辅助 ───────────────────────────

    /// <summary>
    /// 当前请求是否具备管理能力（判定口径与 <see cref="AdminAuthorization"/> 一致）：
    /// 未启用凭据（开发 / 单机演示）或 API Key 可信服务视为管理方；JWT 用户必须持 <c>netbpm.admin</c>。
    /// 用于「全量待办视图」的显式放行 —— 引擎对无条件任务查询的防呆仍然默认生效。
    /// </summary>
    private static bool IsAdminPrincipal(RequestContext requestContext) =>
        !requestContext.IsUserPrincipal || requestContext.HasAdminScope(TaskScopes.Admin);

    /// <summary>
    /// 按 id 校验运行态实例的租户归属（仅受限身份生效），返回实例供变量授权复用。
    /// <para>不受限（API Key 未配置服务作用域 / 未启用鉴权）时返回 <see langword="null"/>。</para>
    /// </summary>
    private static async Task<IExecutionEntity?> EnsureInstanceAccessAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        CancellationToken ct)
    {
        if (!requestContext.IsTenantRestricted)
        {
            return null;
        }

        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"流程实例 '{processInstanceId}' 不存在。");

        TenantGuard.Ensure(requestContext, execution.TenantId, "流程实例", processInstanceId);

        return execution;
    }

    /// <summary>按 id 校验任务（含已结束）的租户归属，并返回任务实体供进一步的授权判断。</summary>
    private static async Task<TaskEntity> EnsureTaskAccessAsync(
        string taskId,
        RequestContext requestContext,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await engine.GetTaskAsync(taskId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"任务 '{taskId}' 不存在（可能已完成或流程已推进）。");

        TenantGuard.Ensure(requestContext, task.TenantId, "任务", taskId);

        return task;
    }

    /// <summary>
    /// 历史 / 流程图访问：先看运行态，再看历史，二者都按租户校验。
    /// </summary>
    /// <returns>运行态实例（供变量等更细粒度授权复用）；实例已结束或调用方不受租户限制时为 <see langword="null"/>。</returns>
    private static async Task<IExecutionEntity?> EnsureHistoryAccessAsync(
        string processInstanceId,
        RequestContext requestContext,
        IExecutionManager executionManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (!requestContext.IsTenantRestricted)
        {
            return null;
        }

        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is not null)
        {
            TenantGuard.Ensure(requestContext, execution.TenantId, "流程实例", processInstanceId);
            return execution;
        }

        var historic = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"流程实例 '{processInstanceId}' 没有历史记录。");

        TenantGuard.Ensure(requestContext, historic.TenantId, "流程实例", processInstanceId);

        return null;
    }
}

/// <summary>任务 DTO。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="Name">任务名。</param>
/// <param name="TaskDefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="Owner">所有者。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ProcessDefinitionKey">从定义 id 解析出的流程 key。</param>
/// <param name="ExecutionId">执行 id。</param>
/// <param name="FormKey">表单 Key（只透传，见 §1.3 第 10 条）。</param>
/// <param name="Priority">优先级。</param>
/// <param name="Revision">乐观锁版本（完成 / 认领时可回传 <c>expectedRevision</c>）。</param>
/// <param name="CreateTime">创建时间。</param>
/// <param name="DueDate">到期时间。</param>
/// <param name="ClaimTime">认领时间。</param>
/// <param name="TenantId">租户（B2.3）。</param>
public sealed record TaskResponse(
    string Id,
    string? Name,
    string? TaskDefinitionKey,
    string? Assignee,
    string? Owner,
    string? ProcessInstanceId,
    string? ProcessDefinitionKey,
    string? ExecutionId,
    string? FormKey,
    int Priority,
    int Revision,
    DateTime? CreateTime,
    DateTime? DueDate,
    DateTime? ClaimTime,
    string? TenantId = null)
{
    /// <summary>从任务实体映射。</summary>
    public static TaskResponse From(TaskEntity task)
    {
        ArgumentNullException.ThrowIfNull(task);

        return new TaskResponse(
            task.Id,
            task.Name,
            task.TaskDefinitionKey,
            task.Assignee,
            task.Owner,
            task.ProcessInstanceId,
            DefinitionIdParser.Key(task.ProcessDefinitionId),
            task.ExecutionId,
            task.FormKey,
            task.Priority,
            task.Revision,
            task.CreateTime,
            task.DueDate,
            task.ClaimTime,
            task.TenantId);
    }
}
