using NetBpm.Bpmn.Model;
using NetBpm.Rest.Security;
using NetBpm.Rest.Services;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>任务端点：查询 / 办理 / 管理动作 / 批量 / 统计。</summary>
public static partial class RuntimeEndpoints
{
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
            unfiltered = string.IsNullOrWhiteSpace(assignee) && RuntimeAccess.IsAdminPrincipal(requestContext);
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
        HttpContext http,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = http.TaskOf()!;

        var identityLinks = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

        return Results.Ok(TaskDetailResponse.From(task, identityLinks));
    }

    /// <summary>任务身份关系（候选人 / 候选组 / 办理人 / 所有者）。</summary>
    private static async Task<IResult> FindTaskIdentityLinksAsync(
        HttpContext http,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = http.TaskOf()!;

        var identityLinks = await engine.FindTaskIdentityLinksAsync(task.Id, ct).ConfigureAwait(false);

        return Results.Ok(identityLinks.Select(IdentityLinkResponse.From).ToArray());
    }

    /// <summary>流程实例级身份关系（参与者 / 发起人 / 候选组）。</summary>
    private static async Task<IResult> FindProcessInstanceIdentityLinksAsync(
        string processInstanceId,
        HttpContext http,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var instance = http.InstanceOf();

        if (instance is null || !string.IsNullOrEmpty(instance.ParentId))
        {
            return Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 不存在（可能已结束）。",
                Code: EngineErrorCode.NotFound));
        }

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
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.TaskIds, "taskIds", out var taskIds, out var error))
        {
            return error!;
        }

        var result = await tasks
            .BatchCompleteAsync(taskIds, JsonVariableReader.Read(request!.Variables), requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }

    /// <summary>
    /// 转办（改派）：把待办直接改给指定用户，<c>assignee</c> 为空表示退回未分配。
    /// <para>管理动作，不校验是否已被他人认领，也不改 <c>CLAIM_TIME_</c>（与 Activiti <c>setAssignee</c> 同语义）。</para>
    /// </summary>
    private static async Task<IResult> SetAssigneeAsync(
        SetAssigneeRequest? request,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks
            .SetAssigneeAsync(http.TaskOf()!, request?.Assignee, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// 修改任务字段：名称 / 描述 / 优先级 / 到期时间 / 分类 / 所有者。
    /// <para>未提供的字段保持不变；<c>owner</c> 传空串表示清空。</para>
    /// </summary>
    private static async Task<IResult> UpdateTaskAsync(
        UpdateTaskRequest? request,
        HttpRequest httpRequest,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (request is null)
        {
            return InvalidRequest("请求体不能为空。");
        }

        var expectedRevision = Preconditions.ReadExpectedRevision(httpRequest);

        await tasks
            .UpdateAsync(http.TaskOf()!, request, expectedRevision, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>加候选用户（任务级 candidate 关系）。</summary>
    private static async Task<IResult> AddCandidateUserAsync(
        TaskCandidateRequest? request,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.UserId))
        {
            return InvalidRequest("userId 必填。");
        }

        await tasks
            .AddCandidateUserAsync(http.TaskOf()!, request.UserId, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>移除候选用户（不影响流程实例级参与者记录）；关系不存在返回 404。</summary>
    private static async Task<IResult> RemoveCandidateUserAsync(
        string userId,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks
            .RemoveCandidateUserAsync(http.TaskOf()!, userId, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>加候选组。</summary>
    private static async Task<IResult> AddCandidateGroupAsync(
        TaskCandidateRequest? request,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.GroupId))
        {
            return InvalidRequest("groupId 必填。");
        }

        await tasks
            .AddCandidateGroupAsync(http.TaskOf()!, request.GroupId, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>移除候选组；关系不存在返回 404。</summary>
    private static async Task<IResult> RemoveCandidateGroupAsync(
        string groupId,
        HttpContext http,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks
            .RemoveCandidateGroupAsync(http.TaskOf()!, groupId, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>委派任务：原办理人记入 owner，委派状态 PENDING（此时不能直接完成）。</summary>
    private static async Task<IResult> DelegateTaskAsync(
        DelegateTaskRequest? request,
        HttpContext http,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.UserId))
        {
            return InvalidRequest("userId 必填。");
        }

        await tasks.DelegateAsync(http.TaskOf()!, request.UserId, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>解决委派：办理人回到 owner，委派状态 RESOLVED（任务仍未完成）。</summary>
    private static async Task<IResult> ResolveTaskAsync(
        HttpContext http,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks.ResolveAsync(http.TaskOf()!, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// 任务表单元数据：只回传 <c>formKey</c> 与 <c>activiti:formProperty</c> 声明，
    /// 不渲染、不校验（与 B2.2「不做表单引擎」的定稿一致）。
    /// </summary>
    private static async Task<IResult> GetTaskFormAsync(
        HttpContext http,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = http.TaskOf()!;

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
            as UserTask;

        return userTask is null
            ? Results.NotFound(new ErrorResponse(
                $"流程定义中找不到用户任务节点 '{task.TaskDefinitionKey}'。",
                Code: EngineErrorCode.NotFound))
            : Results.Ok(FormMetadataResponse.From(userTask));
    }

    /// <summary>
    /// 签收任务：认领人取自请求体（JWT 下必须等于令牌 <c>sub</c>），缺省取令牌 / 头身份。
    /// <para>
    /// ⚠️ 与旧版本不同：<b>解析不出认领人时返回 400</b>，不再退化为「取消认领」；
    /// 取消认领请显式调用 <c>POST /tasks/{id}/unclaim</c>（或 <c>unclaim=true</c> 兼容旧客户端）。
    /// </para>
    /// <para>
    /// 授权为何留在 handler：<c>unclaim=true</c> 的兼容分支与认领是两套语义
    /// （前者要求「当前办理人」，后者要求「候选人」），而过滤器看不到请求体。
    /// </para>
    /// </summary>
    private static async Task<IResult> ClaimTaskAsync(
        ClaimTaskRequest? request,
        HttpContext http,
        RequestContext requestContext,
        IProcessEngine engine,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        var task = http.TaskOf()!;

        // 兼容旧客户端的 {"unclaim": true}：显式取消认领与 /unclaim 同语义
        if (request?.Unclaim == true)
        {
            TaskAuthorization.EnsureCanUnclaim(task, requestContext);

            await tasks.UnclaimAsync(task.Id, request.ExpectedRevision, ct).ConfigureAwait(false);

            return Results.NoContent();
        }

        // JWT 下认领人只能来自令牌；API Key / 开发模式允许显式传 userId。
        var userId = RequestIdentity.User(request?.UserId, requestContext);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return InvalidRequest(
                "认领必须能解析出 userId（JWT 的 sub / X-User-Id / 请求体 userId）；"
                + "取消认领请调用 POST /api/runtime/tasks/{taskId}/unclaim。");
        }

        await TaskAuthorization.EnsureCanClaimAsync(task, requestContext, engine, ct).ConfigureAwait(false);

        await tasks.ClaimAsync(task.Id, userId, request?.ExpectedRevision, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>取消认领：只能由当前办理人或管理员操作；任务未签收时幂等返回 204。</summary>
    private static async Task<IResult> UnclaimTaskAsync(
        ClaimTaskRequest? request,
        HttpContext http,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks.UnclaimAsync(http.TaskOf()!.Id, request?.ExpectedRevision, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> CompleteTaskAsync(
        CompleteTaskRequest? request,
        HttpContext http,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        await tasks
            .CompleteAsync(
                http.TaskOf()!,
                JsonVariableReader.Read(request?.Variables),
                request?.ExpectedRevision,
                ct)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

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
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        var stats = await tasks
            .GetStatsAsync(
                assignee,
                candidateUser,
                candidateGroup,
                processDefinitionId,
                tenantId,
                timeZoneOffsetMinutes,
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.Ok(stats);
    }

    /// <summary>
    /// 批量转办：逐项执行，单条失败不影响其它项。
    /// <para>⚠️ 不是原子操作（每个任务一个命令 / 事务）；响应逐项给出成功与失败原因。</para>
    /// </summary>
    private static async Task<IResult> BatchSetAssigneeAsync(
        BatchTaskAssigneeRequest? request,
        RequestContext requestContext,
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.TaskIds, "taskIds", out var taskIds, out var error))
        {
            return error!;
        }

        var result = await tasks
            .BatchSetAssigneeAsync(taskIds, request!.Assignee, requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
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
        ITaskCommandService tasks,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.TaskIds, "taskIds", out var taskIds, out var error))
        {
            return error!;
        }

        if (request!.DueDate is null && !request.ClearDueDate)
        {
            return InvalidRequest("dueDate 与 clearDueDate 至少要给一个。");
        }

        var result = await tasks
            .BatchSetDueDateAsync(
                taskIds,
                UtcTime.Normalize(request.DueDate),
                request.ClearDueDate,
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }
}
