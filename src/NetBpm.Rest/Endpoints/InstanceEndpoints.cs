using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Endpoints;

/// <summary>
/// 平台业务面：<b>流程实例</b>（<c>/workflow/v1/instances</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>/api/runtime/instances</c> 的分工：那里是引擎原语的直通面，这里是<b>对外契约</b>——
/// <see cref="InstanceView"/> 不含任何 <c>ACT_*</c> 字段，只有审批业务需要的形状，
/// 且待办（<c>Tasks</c>）与提交结果（<c>SubmitTaskResult.NextTasks</c>）走同一个
/// <see cref="ITaskCatalog"/> 组装，保证同一条待办在两处响应里长得一样。
/// </para>
/// <para>读端点 scope 走 <see cref="PlatformScopes.InstanceRead"/>；未授权 / 不存在按 404。</para>
/// </remarks>
public static class InstanceEndpoints
{
    /// <summary>注册实例端点。</summary>
    /// <param name="endpoints">端点构建器。</param>
    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/workflow/v1").WithTags("workflow-instances");

        group.MapGet("/instances", ListAsync)
            .Produces<CursorPage<InstanceSummary>>()
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden);

        group.MapGet("/instances/{instanceId}", FindAsync)
            .Produces<InstanceView>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/instances/{instanceId}/history", HistoryAsync)
            .Produces<HistoryView>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapGet("/instances/{instanceId}/parameters", ParametersAsync)
            .Produces<InstanceParameterView>()
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        // 流转状态图直接给 SVG：浏览器 / <img> 就能显示，调用方不必引入 BPMN 渲染库
        group.MapGet("/instances/{instanceId}/diagram.svg", DiagramAsync)
            .Produces<string>(StatusCodes.Status200OK, "image/svg+xml")
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound);

        group.MapPost("/instances", StartAsync)
            .Produces<StartInstanceResult>(StatusCodes.Status201Created)
            .Produces<PlatformProblem>(StatusCodes.Status400BadRequest)
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/instances/{instanceId}/terminate", TerminateAsync)
            .Produces<InstanceView>()
            .Produces<PlatformProblem>(StatusCodes.Status403Forbidden)
            .Produces<PlatformProblem>(StatusCodes.Status404NotFound)
            .Produces<PlatformProblem>(StatusCodes.Status409Conflict)
            .Produces<PlatformProblem>(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    /// <summary>
    /// 实例列表（「我的单据」/ 流程监控的数据源）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 人员范围是<b>服务端</b>决定的：非管理员带 <c>startedBy</c> 只能是自己，不传则默认收窄到本人
    /// （见 <see cref="InstanceListCatalog"/>）。这不是 scope 能替代的一层——
    /// <c>workflow:instance:read</c> 只说明「能查实例」，不说明「能查谁的单例」。
    /// </para>
    /// <para>
    /// 游标不合法（被改过、来自别的接口）返回 <c>400 request.malformed</c>，
    /// 不用 404 / 422：这是<b>请求本身</b>有问题，与资源是否存在无关。
    /// </para>
    /// </remarks>
    private static async Task<IResult> ListAsync(
        HttpContext http,
        RequestContext context,
        IInstanceListCatalog catalog,
        string? processKey,
        string? businessKey,
        string? status,
        string? startedBy,
        string? involvedUser,
        DateTimeOffset? startedFrom,
        DateTimeOffset? startedTo,
        string? cursor,
        int? limit,
        string? direction,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceRead);
        }

        // 枚举按字符串收：契约里的取值是 SCREAMING_SNAKE（RUNNING / DESCENDING，与 JSON 一致），
        // 而 Minimal API 默认的枚举绑定大小写敏感，`?status=RUNNING` 会直接绑定失败。
        if (!TryParseEnum<InstanceStatus>(status, out var parsedStatus))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "查询参数不合法",
                StatusCodes.Status400BadRequest,
                $"status 取值 '{status}' 不合法；可用值：{JoinEnumNames<InstanceStatus>()}。");
        }

        if (!TryParseEnum<SortDirection>(direction, out var parsedDirection))
        {
            return PlatformProblems.Create(
                http,
                context,
                ErrorCodes.RequestMalformed,
                "查询参数不合法",
                StatusCodes.Status400BadRequest,
                $"direction 取值 '{direction}' 不合法；可用值：{JoinEnumNames<SortDirection>()}。");
        }

        var query = new InstanceQuery(
            ProcessKey: processKey,
            BusinessKey: businessKey,
            Status: parsedStatus,
            StartedBy: startedBy,
            InvolvedUser: involvedUser,
            StartedFrom: startedFrom,
            StartedTo: startedTo,
            Cursor: cursor,
            Limit: limit,
            Direction: parsedDirection ?? SortDirection.Descending);

        try
        {
            return Results.Ok(await catalog.ListAsync(query, context, ct).ConfigureAwait(false));
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
        catch (InstanceListRejectedException exception)
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

    /// <summary>把查询参数里的枚举名解析成枚举值（不区分大小写；空值表示未传）。</summary>
    /// <remarks>
    /// 刻意<b>不接受</b>纯数字（<c>?status=0</c>）：枚举的序号是对内实现细节，
    /// 一旦对外可用，以后调整枚举顺序就变成破坏性变更。
    /// </remarks>
    private static bool TryParseEnum<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (long.TryParse(value, out _)
            || !Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            return false;
        }

        result = parsed;

        return true;
    }

    /// <summary>枚举可用值（用于 400 的 detail，让调用方一次就知道该传什么）。</summary>
    private static string JoinEnumNames<TEnum>()
        where TEnum : struct, Enum => string.Join(" / ", Enum.GetNames<TEnum>());

    private static async Task<IResult> FindAsync(
        string instanceId,
        HttpContext http,
        RequestContext context,
        IInstanceCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceRead);
        }

        var view = await catalog.FindAsync(instanceId, context, ct).ConfigureAwait(false);

        return view is null
            ? PlatformProblems.InstanceNotFound(http, context, instanceId)
            : Results.Ok(view);
    }

    /// <summary>
    /// 读实例的审批记录（append-only 时间线，<c>?afterSequence=</c> 增量拉取）。
    /// </summary>
    /// <remarks>
    /// scope 只做「能力上限」判定（<see cref="PlatformScopes.HistoryRead"/>）；
    /// 数据级可见性由 <see cref="IApprovalTimeline"/> 在实例读模型之上加一层：
    /// 管理 scope / 纯服务令牌看全文，<b>本单的相关人</b>（发起人 / 办理人 / 候选人 / 参与者）
    /// 看全文去 <c>actorCallerId</c>，其余一律按「这张单不存在」处理（404 而不是 403）。
    /// </remarks>
    private static async Task<IResult> HistoryAsync(
        string instanceId,
        HttpContext http,
        RequestContext context,
        IApprovalTimeline timeline,
        long? afterSequence,
        int? limit,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.HistoryRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.HistoryRead);
        }

        var view = await timeline
            .GetHistoryAsync(instanceId, afterSequence ?? 0, limit ?? 0, context, ct)
            .ConfigureAwait(false);

        return view is null
            ? PlatformProblems.InstanceNotFound(http, context, instanceId)
            : Results.Ok(view);
    }

    /// <summary>
    /// 实例的参数面板：<b>参数声明 + 当前值</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <c>GET /instances/{id}</c> 里的 <c>parameters</c> 是同一份数据的两种视角：
    /// 那里只给值（详情页顺带带着），这里把值接到声明上（标签、类型、约束、谁能写），
    /// 调用方拿到就能直接渲染参数面板，不必再拉一次流程参数规范自己对齐。
    /// </para>
    /// <para>
    /// <b>不满足 <c>visibleTo</c> 的参数整条消失</b>（不是返回空值）：可见性口径与实例详情同一条代码路径。
    /// 同时带上 <c>revision</c>，让调用方写回参数时能用同一套乐观并发。
    /// </para>
    /// </remarks>
    private static async Task<IResult> ParametersAsync(
        string instanceId,
        HttpContext http,
        RequestContext context,
        IInstanceCatalog catalog,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceRead);
        }

        var view = await catalog.GetParametersAsync(instanceId, context, ct).ConfigureAwait(false);

        return view is null
            ? PlatformProblems.InstanceNotFound(http, context, instanceId)
            : Results.Ok(view);
    }

    /// <summary>
    /// 实例的<b>流转状态图</b>（SVG）：走过的节点与连线标红，当前停留的环节加粗。
    /// </summary>
    /// <remarks>
    /// 与 <c>GET /processes/{key}/diagram.svg</c> 的区别是「定义的样子」与「这张单据走到哪了」：
    /// 同一个流程的两张单据会给出两张不同的图。可见性走实例读模型——能看见实例才能看见它的图。
    /// </remarks>
    private static async Task<IResult> DiagramAsync(
        string instanceId,
        HttpContext http,
        RequestContext context,
        IInstanceDiagram diagram,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceRead))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceRead);
        }

        var svg = await diagram.RenderAsync(instanceId, context, ct).ConfigureAwait(false);

        return svg is null
            ? PlatformProblems.InstanceNotFound(http, context, instanceId)
            : Results.Content(svg, "image/svg+xml");
    }

    private static async Task<IResult> StartAsync(
        StartInstanceRequest request,
        HttpContext http,
        RequestContext context,
        IInstanceStartService start,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceStart))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceStart);
        }

        try
        {
            var result = await start.StartAsync(request, context, ct).ConfigureAwait(false);

            return Results.Created($"/workflow/v1/instances/{result.InstanceId}", result);
        }
        catch (StartInstanceException exception)
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
    /// 终止流程实例（<c>workflow:instance:terminate</c>，高危 scope，单独授予）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 终止原因<b>必填</b>：它会原样进审批记录。允许「无理由终止」等于允许在审计里
    /// 留下一句「不知为何这张单没了」。
    /// </para>
    /// <para>
    /// 返回体是终止后的 <see cref="InstanceView"/>：调用方不用再发一次详情请求
    /// 才能刷新页面上的状态与待办列表（那两次请求之间实例可能又被别人改过）。
    /// </para>
    /// </remarks>
    private static async Task<IResult> TerminateAsync(
        string instanceId,
        TerminateInstanceRequest request,
        HttpContext http,
        RequestContext context,
        IInstanceTermination termination,
        CancellationToken ct)
    {
        if (!PlatformAuthorization.HasScope(context, PlatformScopes.InstanceTerminate))
        {
            return PlatformProblems.InsufficientScope(http, context, PlatformScopes.InstanceTerminate);
        }

        try
        {
            return Results.Ok(await termination.TerminateAsync(instanceId, request, context, ct)
                .ConfigureAwait(false));
        }
        catch (PlatformActionRejectedException exception)
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
}
