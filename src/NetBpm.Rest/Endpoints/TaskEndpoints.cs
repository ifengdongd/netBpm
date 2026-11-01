using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

// 引擎面（NetBpm.Rest.Contracts）也有一个 ClaimTaskRequest，这里要的是平台契约的那一个。
using PlatformClaimRequest = NetBpm.Platform.Contracts.ClaimTaskRequest;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 平台业务面：<b>待办</b>（<c>/workflow/v1/tasks</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>/api/runtime/tasks</c> 的分工：那里是引擎原语的直通面（<c>assignee</c> / <c>candidateUser</c> 裸查询），
/// 这里是<b>对外契约</b>——<c>scope=MINE|CLAIMED|ALL</c> 由服务端判定，<c>TaskView.options</c> 是
/// 三码模型算好的审批按钮，调用方直接渲染。
/// </para>
/// <para>
/// 读端点 scope 走 <see cref="PlatformScopes.TaskRead"/>；<c>ALL</c> 范围额外要求管理权限
/// （超管或 <see cref="PlatformScopes.HistoryReadAll"/>），由 <see cref="ITaskCatalog"/> 服务端判定，
/// 不靠调用方自觉传参。
/// </para>
/// </remarks>
public static class TaskEndpoints
{
    /// <summary>注册待办端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/workflow/v1").WithTags("workflow-tasks");

        group.MapGet("/tasks", ListAsync)
            .Produces<CursorPage<TaskSummary>>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/tasks/{taskId}", FindAsync)
            .Produces<TaskView>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/tasks/{taskId}/form", FormAsync)
            .Produces<FormSnapshot>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/tasks/{taskId}/attachments", AttachmentsAsync)
            .Produces<IReadOnlyList<AttachmentRef>>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPost("/tasks/{taskId}/submit", SubmitAsync)
            .Produces<SubmitTaskResult>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/tasks/{taskId}/claim", ClaimAsync)
            .Produces<TaskView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict);

        group.MapPost("/tasks/{taskId}/unclaim", UnclaimAsync)
            .Produces<TaskView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict);

        group.MapPost("/tasks/{taskId}/transfer", TransferAsync)
            .Produces<TaskView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? scope,
        string? processKey,
        string? activityId,
        string? keyword,
        bool? overdueOnly,
        string? cursor,
        int? limit,
        HttpContext http,
        RequestContext context,
        ITaskCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskRead);
        }

        var parsedScope = TaskScope.Mine;

        if (!string.IsNullOrWhiteSpace(scope)
            && !Enum.TryParse<TaskScope>(scope.Trim(), ignoreCase: true, out parsedScope))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "查询参数不合法",
                StatusCodes.Status400BadRequest,
                $"scope 的取值必须是 MINE / CLAIMED / DONE / ALL，实际为 '{scope}'。");
        }

        if (parsedScope == TaskScope.Done
            && !PlatformAuthorization.HasScope(context, PlatformScopes.HistoryRead))
        {
            // 已办的数据源是 ACT_HI_TASKINST：它属于审批记录而不是待办，用专门的 scope 卡住，
            // 免得「能读待办」就被顺带拿到「这个人批过什么」。
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.HistoryRead);
        }

        if (parsedScope == TaskScope.All
            && !PlatformAuthorization.HasScope(context, PlatformScopes.HistoryReadAll)
            && !context.IsAdmin)
        {
            // 与 DONE 同口径：缺权限就 403，而不是静默降级成 MINE。
            // 降级会让调用方把「全员待办」当「我的待办」处理（业务上就是漏单），且日志里看不出任何异常。
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.HistoryReadAll);
        }

        var query = new TaskQuery(
            Scope: parsedScope,
            ProcessKey: processKey,
            ActivityId: activityId,
            Keyword: keyword,
            OverdueOnly: overdueOnly,
            Cursor: cursor,
            Limit: limit);

        try
        {
            var page = await catalog.ListAsync(query, context, ct).ConfigureAwait(false);

            return Results.Ok(page);
        }
        catch (FormatException exception)
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "查询参数不合法",
                StatusCodes.Status400BadRequest,
                exception.Message);
        }
    }

    private static async Task<IResult> FindAsync(
        string taskId,
        HttpContext http,
        RequestContext context,
        ITaskCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskRead);
        }

        var view = await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false);

        return view is null
            ? PlatformProblems.TaskNotFound(http, context, taskId)
            : Results.Ok(view);
    }

    /// <summary>
    /// 取某个任务的<b>表单快照</b>（当时的字段定义 + 表单值 + 附件引用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 它不是「当前definition 的表单 schema」（那在 <c>GET /tasks/{id}</c> 的 <c>form</c> 里），
    /// 而是<b>这一次提交当时冻住的那一份</b>。历史详情页按它渲染，才会显示
    /// 「那时候这个环节到底让填什么」，而不是三个月后已被改过三版的新定义。
    /// </para>
    /// <para>
    /// 没有快照的任务（该环节不需要表单 / 快照写失败 / 从未走过的任务）按<b>不存在</b>处理：
    /// 404 而不是 200 + 空对象——后者会让调用方以为「这是当时的表单」。
    /// </para>
    /// </remarks>
    private static async Task<IResult> FormAsync(
        string taskId,
        HttpContext http,
        RequestContext context,
        IApprovalSnapshot snapshots,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskRead);
        }

        var snapshot = await snapshots.FindAsync(taskId, context, ct).ConfigureAwait(false);

        return snapshot is null
            ? PlatformProblems.TaskNotFound(http, context, taskId)
            : Results.Ok(snapshot);
    }

    /// <summary>
    /// 取某个任务<b>提交时附带的附件引用</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 平台<b>不做附件存储</b>：本体留在调用方的对象存储里，这里只回放当时提交的
    /// <see cref="AttachmentRef"/>（名称 / URL / 大小 / 摘要）。它证明的是
    /// 「这一步提交了哪些材料」，而不是「这些材料现在还在不在」——
    /// URL 该由调用方带时效签名，过期是正常状态，不是这张单据的问题。
    /// </para>
    /// <para>
    /// 与 <c>/tasks/{id}/form</c> 同口径：<b>没有快照的任务按不存在处理</b>（404）。
    /// 没提交过就没有「当时的材料」这回事；回 200 + 空数组会让调用方以为
    /// 「提交过、只是没传附件」。
    /// </para>
    /// </remarks>
    private static async Task<IResult> AttachmentsAsync(
        string taskId,
        HttpContext http,
        RequestContext context,
        IApprovalSnapshot snapshots,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskRead);
        }

        var snapshot = await snapshots.FindAsync(taskId, context, ct).ConfigureAwait(false);

        return snapshot is null
            ? PlatformProblems.TaskNotFound(http, context, taskId)
            : Results.Ok(snapshot.Attachments ?? []);
    }

    private static async Task<IResult> SubmitAsync(
        string taskId,
        SubmitTaskRequest request,
        HttpContext http,
        RequestContext context,
        ITaskSubmitService submit,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskSubmit))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskSubmit);
        }

        try
        {
            var result = await submit.SubmitAsync(taskId, request, context, ct).ConfigureAwait(false);

            return Results.Ok(result);
        }
        catch (SubmitDecisionException exception)
        {
            return PlatformProblems.Create(
                http,
                context,
                exception.Code,
                exception.Title,
                exception.Status,
                exception.Detail);
        }
    }

    /// <summary>
    /// 认领待办（把办理人设为自己）。
    /// </summary>
    /// <remarks>
    /// 认领只认「还没人认领、且我在候选范围内」的待办。已被别人认领时返回 <c>409</c>
    /// 而不是 <c>404</c>：调用方这时候<b>已经看得到</b>这条待办，
    /// 「被谁拿走了」是它需要知道的信息，不是需要保密的信息。
    /// </remarks>
    private static async Task<IResult> ClaimAsync(
        string taskId,
        PlatformClaimRequest? request,
        HttpContext http,
        RequestContext context,
        ITaskAssignment assignment,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskAssign))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskAssign);
        }

        try
        {
            return Results.Ok(await assignment.ClaimAsync(taskId, request, context, ct).ConfigureAwait(false));
        }
        catch (PlatformActionRejectedException exception)
        {
            return Rejected(http, context, exception);
        }
    }

    /// <summary>
    /// 取消认领（把待办退回候选池）。
    /// </summary>
    /// <remarks>
    /// 只有办理人本人能放弃自己的待办；管理员要改别人的归属用 <c>transfer</c>，
    /// 不用 unclaim —— 否则「谁放弃了这单」这条时间线会记成管理员而不是实际办理人。
    /// </remarks>
    private static async Task<IResult> UnclaimAsync(
        string taskId,
        HttpContext http,
        RequestContext context,
        ITaskAssignment assignment,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskAssign))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskAssign);
        }

        try
        {
            return Results.Ok(await assignment.UnclaimAsync(taskId, context, ct).ConfigureAwait(false));
        }
        catch (PlatformActionRejectedException exception)
        {
            return Rejected(http, context, exception);
        }
    }

    /// <summary>
    /// 转办（把待办交给指定的人，可覆盖现有办理人）。
    /// </summary>
    /// <remarks>
    /// 转办比认领门槛高，因为它能覆盖别人的办理人：管理视角、当前办理人本人、或候选范围内的人才能转。
    /// 它<b>不改认领时间</b>——被转办的人没有主动认领，把认领时间刷成新的等于伪造一次认领动作。
    /// </remarks>
    private static async Task<IResult> TransferAsync(
        string taskId,
        TransferTaskRequest request,
        HttpContext http,
        RequestContext context,
        ITaskAssignment assignment,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.TaskAssign))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.TaskAssign);
        }

        try
        {
            return Results.Ok(await assignment.TransferAsync(taskId, request, context, ct).ConfigureAwait(false));
        }
        catch (PlatformActionRejectedException exception)
        {
            return Rejected(http, context, exception);
        }
    }

    /// <summary>把「写动作被拒」转成统一的问题响应（错误码 → 状态码的映射只写这一处）。</summary>
    private static IResult Rejected(HttpContext http, RequestContext context, PlatformActionRejectedException exception) =>
        PlatformProblems.Create(
            http,
            context,
            exception.Code,
            exception.Title,
            exception.Status,
            exception.Detail);
}
