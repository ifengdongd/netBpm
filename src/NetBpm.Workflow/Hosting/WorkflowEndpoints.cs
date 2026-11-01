using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 审批流程的通用 HTTP 端点：<b>流程发起、实例列表 / 详情、审批历史、待办查询、审批提交、流程定义查看</b>。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 这一层只做三件事：绑定请求、把应用层的视图交给业务加工（<see cref="WorkflowEndpointOptions"/>
/// 里的 Decorator）、把异常翻成状态码 —— 真正干活的是 <see cref="IWorkflowService"/>。
/// 任何宿主（Demo、正式系统）挂一组路由就能得到完整的审批流 HTTP 面；业务语义
/// （业务单号生成、DTO 摊平、错误响应形状）全部经 <see cref="WorkflowEndpointOptions"/> 注入。
/// </para>
/// <para>
/// 设计要点：<b>前端不写死任何审批选项</b>。实例视图与待办视图都带表单定义
/// （含 <c>approvalResult</c> 这类候选值），前端照着渲染，提交时回传所选值。
/// </para>
/// <para>路由与状态码约定：</para>
/// <list type="table">
///   <item><description><c>POST /instances</c>：发起 → 200（视图）/ 400；</description></item>
///   <item><description><c>GET /instances</c>：业务登记列表 → 200；</description></item>
///   <item><description><c>GET /instances/{id}</c> 与 <c>.../history</c>：查不到 → 404；</description></item>
///   <item><description><c>GET /tasks</c>：待办中心（候选人 / 办理人 / 实例任一条件）→ 200 / 400；</description></item>
///   <item><description><c>POST /tasks/{id}/submit</c>：审批提交 → 200 / 204（无实例）/ 404 / 400；</description></item>
///   <item><description><c>GET /process-definition</c>：当前生效定义（含 BPMN XML）→ 200 / 404。</description></item>
/// </list>
/// </para>
/// </remarks>
[Obsolete(Deprecation.Message)]
public static class WorkflowEndpoints
{
#pragma warning disable CS0618 // 旧面内部互相引用（本类与 WorkflowEndpointOptions 一起废弃）

    /// <summary>
    /// 把通用流程端点映射到 <paramref name="pattern"/> 路由组下。宿主决定挂在哪个前缀
    /// （如 <c>/api/leave</c> 或 <c>/api/workflow</c>），路由约定保持一致。
    /// </summary>
    public static IEndpointConventionBuilder MapWorkflowEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        WorkflowEndpointOptions options)
    {
        var group = endpoints.MapGroup(pattern).WithTags("workflow");

        group.MapPost("/instances", (HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            StartAsync(http, workflow, options, ct)).Produces<object>();
        group.MapGet("/instances", (HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            FindInstancesAsync(http, workflow, options, ct));
        group.MapGet("/instances/{processInstanceId}", (string processInstanceId, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            GetInstanceAsync(processInstanceId, http, workflow, options, ct)).Produces<object>();
        group.MapGet("/instances/{processInstanceId}/history", (string processInstanceId, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            GetHistoryAsync(processInstanceId, http, workflow, options, ct)).Produces<WorkflowHistoryView>();

        group.MapGet("/tasks", (string? candidateUser, string? assignee, string? processInstanceId, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            FindTasksAsync(candidateUser, assignee, processInstanceId, http, workflow, options, ct));
        group.MapPost("/tasks/{taskId}/submit", (string taskId, SubmitWorkflowTaskRequest? request, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            SubmitTaskAsync(taskId, request, http, workflow, options, ct)).Produces<object>();


        // 管理动作：改派（转办）与流程跳转 —— 面向「查看所有实例并干预」的管理端
        group.MapPost("/tasks/{taskId}/assignee", (string taskId, ReassignTaskRequest? request, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            ReassignTaskAsync(taskId, request, http, workflow, options, ct)).Produces<object>();
        group.MapPost("/instances/{processInstanceId}/jump", (string processInstanceId, JumpToNodeRequest? request, HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
            JumpToNodeAsync(processInstanceId, request, http, workflow, options, ct)).Produces<object>();

        group.MapGet("/process-definition", (HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetProcessDefinitionAsync(http, engine, workflow, options, ct));

        // 流程图图片：发布（部署）后的定义渲染成 SVG —— 浏览器直接当图片显示，可另存。
        // ?definitionId= 指定任意历史版本（流程部署列表的「查看历史流程图」链接指向这里）
        group.MapGet("/process-diagram.svg", (string? definitionId, HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetProcessDiagramAsync(definitionId, http, engine, workflow, options, ct)).Produces<string>(200, "image/svg+xml");

        // 流程部署列表：每个部署一行（含历史版本），每项带流程图链接 —— 方便查看历史流程图信息
        group.MapGet("/process-deployments", (HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetProcessDeploymentsAsync(http, engine, workflow, options, ct)).Produces<object>();

        // 实例的流转状态图：走过的节点=红框、走过的线=红线，当前正停的环节加粗红框
        group.MapGet("/instances/{processInstanceId}/diagram.svg", (string processInstanceId, HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetInstanceDiagramAsync(processInstanceId, http, engine, workflow, options, ct)).Produces<string>(200, "image/svg+xml");

        // 可跳转的节点清单（流程管理页的「目标步骤」下拉数据源，对齐老系统「环节名 [节点id]」的核对习惯）
        group.MapGet("/process-nodes", (HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetProcessNodesAsync(http, engine, workflow, options, ct));

        return group;
    }

    // ─────────────────────────── 流程实例 ───────────────────────────

    /// <summary>发起流程：建流程实例，返回首个待办及其表单。业务加工由 options 决定（如生成单号）。</summary>
    private static async Task<IResult> StartAsync(
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        StartWorkflowRequest? request;

        try
        {
            request = await http.Request.ReadFromJsonAsync<StartWorkflowRequest>(ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(options.Error("请求体不是合法 JSON。"));
        }

        if (request is null)
        {
            return Results.BadRequest(options.Error("applicant 不能为空。"));
        }

        try
        {
            if (options.TransformStartRequest is not null)
            {
                request = options.TransformStartRequest(http.RequestServices, request);
            }

            var view = await workflow
                .StartAsync(options.ProcessKey(http.RequestServices), request, ct)
                .ConfigureAwait(false);

            return Results.Ok(await Decorate(http.RequestServices, options.DecorateInstanceView, view).ConfigureAwait(false));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception, options);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(options.Error(exception.Message));
        }
    }

    /// <summary>
    /// 业务登记列表（新的在前），列表页用。
    /// <para>分页 <c>firstResult</c> / <c>maxResults</c>（默认 20、上限 200），总数放 <c>X-Total-Count</c>。</para>
    /// </summary>
    private static async Task<IResult> FindInstancesAsync(
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var first = ParseInt(http.Request.Query["firstResult"], 0);
        var size = Math.Clamp(ParseInt(http.Request.Query["maxResults"], 20), 1, 200);

        var summaries = await workflow.FindInstancesAsync(first, size, ct).ConfigureAwait(false);
        var total = await workflow.CountInstancesAsync(ct).ConfigureAwait(false);

        http.Response.Headers["X-Total-Count"] = total.ToString(CultureInfo.InvariantCulture);

        var decorated = new List<object?>(summaries.Count);

        foreach (var summary in summaries)
        {
            decorated.Add(await Decorate(http.RequestServices, options.DecorateInstanceSummary, summary).ConfigureAwait(false));
        }

        return Results.Ok(decorated);
    }

    /// <summary>流程实例详情：变量 + 当前待办（含表单定义）+ 审批结论 + 流程参数。</summary>
    private static async Task<IResult> GetInstanceAsync(
        string processInstanceId,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var view = await workflow.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        return view is null
            ? Results.NotFound(options.Error($"流程实例 '{processInstanceId}' 不存在。"))
            : Results.Ok(await Decorate(http.RequestServices, options.DecorateInstanceView, view).ConfigureAwait(false));
    }

    /// <summary>审批历史记录：环节序列 + 审批记录（谁在什么时候批了什么）。</summary>
    private static async Task<IResult> GetHistoryAsync(
        string processInstanceId,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var history = await workflow.FindHistoryAsync(processInstanceId, ct).ConfigureAwait(false);

        return history is null
            ? Results.NotFound(options.Error($"流程实例 '{processInstanceId}' 不存在。"))
            : Results.Ok(history);
    }

    // ─────────────────────────── 任务 ───────────────────────────

    /// <summary>按候选人 / 办理人 / 流程实例查待办（「待办中心」）。</summary>
    private static async Task<IResult> FindTasksAsync(
        string? candidateUser,
        string? assignee,
        string? processInstanceId,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        try
        {
            var tasks = await workflow
                .FindTasksAsync(new TaskQuery(assignee, candidateUser, processInstanceId), ct)
                .ConfigureAwait(false);

            return Results.Ok(tasks);
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception, options);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(options.Error(exception.Message));
        }
    }

    /// <summary>
    /// 提交任务表单：<c>values</c> 的键必须是流程定义里的 <c>formProperty/@id</c>，
    /// 属性上的 <c>variable</c> 决定它落到哪个流程变量，随后网关按变量取道。
    /// 顺带补参数传 <c>paraDict</c>（有值就按 key 更新）；按候选组审批时
    /// 记下办理人传 <c>operator</c>。
    /// </summary>
    private static async Task<IResult> SubmitTaskAsync(
        string taskId,
        SubmitWorkflowTaskRequest? request,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        try
        {
            var view = await workflow.SubmitAsync(taskId, request, ct).ConfigureAwait(false);

            return view is null
                ? Results.NoContent()
                : Results.Ok(await Decorate(http.RequestServices, options.DecorateInstanceView, view).ConfigureAwait(false));
        }
        catch (WorkflowNotFoundException exception)
        {
            return Results.NotFound(options.Error(exception.Message));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception, options);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(options.Error(exception.Message));
        }
    }

    // ─────────────────────────── 流程定义 ───────────────────────────

    /// <summary>当前生效的流程定义（含 BPMN XML，便于对照表单属性）。</summary>
    private static async Task<IResult> GetProcessDefinitionAsync(
        HttpContext http,
        IProcessEngine engine,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var definitionId = await workflow
            .ResolveDefinitionIdAsync(options.ProcessKey(http.RequestServices), ct)
            .ConfigureAwait(false);

        if (definitionId is null)
        {
            return Results.NotFound(options.Error("流程定义尚未部署。"));
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(definitionId, ct).ConfigureAwait(false);

        return Results.Ok(new { processDefinitionId = definitionId, xml });
    }

    /// <summary>
    /// 流程图图片：渲染流程定义为 SVG。不带 <c>?definitionId=</c> 时取当前生效定义；
    /// 带了就渲染指定版本（流程部署列表的「查看历史流程图」链接指向这里）。
    /// </summary>
    private static async Task<IResult> GetProcessDiagramAsync(
        string? definitionId,
        HttpContext http,
        IProcessEngine engine,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var definitions = await engine.FindProcessDefinitionsAsync(ct: ct).ConfigureAwait(false);
        var processKey = options.ProcessKey(http.RequestServices);

        string? resolvedId;

        if (!string.IsNullOrEmpty(definitionId))
        {
            // 指定版本：必须是本流程 key 下真实存在的定义（防串流程取图）
            resolvedId = definitions
                .Where(definition => definition.Key == processKey && definition.Id == definitionId)
                .Select(definition => definition.Id)
                .FirstOrDefault();

            if (resolvedId is null)
            {
                return Results.NotFound(options.Error($"流程定义 '{definitionId}' 不存在或不属于流程 '{processKey}'。"));
            }
        }
        else
        {
            resolvedId = await workflow
                .ResolveDefinitionIdAsync(processKey, ct)
                .ConfigureAwait(false);

            if (resolvedId is null)
            {
                return Results.NotFound(options.Error("流程定义尚未部署。"));
            }
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(resolvedId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(xml))
        {
            return Results.NotFound(options.Error($"流程定义 '{resolvedId}' 没有 XML 资源。"));
        }

        var svg = BpmnDiagramSvgRenderer.RenderSvg(xml, DiagramHighlight.None);

        return Results.Content(svg, "image/svg+xml");
    }

    /// <summary>
    /// 实例的流转状态图：审批历史（走过的环节）+ 当前待办（正停的环节）映射到流程图上 ——
    /// 走过的节点/线标红，当前环节加粗红框。已结束的实例全图标红到结束事件。
    /// </summary>
    private static async Task<IResult> GetInstanceDiagramAsync(
        string processInstanceId,
        HttpContext http,
        IProcessEngine engine,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var history = await workflow.FindHistoryAsync(processInstanceId, ct).ConfigureAwait(false);

        if (history is null)
        {
            return Results.NotFound(options.Error($"流程实例 '{processInstanceId}' 不存在。"));
        }

        var visited = history.Steps
            .Select(step => step.ActivityId)
            .Where(activityId => !string.IsNullOrEmpty(activityId))
            .Distinct()
            .ToArray();
        var active = history.Steps.Where(step => step.IsActive).Select(step => step.ActivityId).ToArray();

        // 定义 id：优先用历史里记录的（实例可能停在旧版本定义上），没有就取当前生效定义
        var definitionId = !string.IsNullOrEmpty(history.ProcessDefinitionId)
            ? history.ProcessDefinitionId
            : await workflow.ResolveDefinitionIdAsync(options.ProcessKey(http.RequestServices), ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(definitionId))
        {
            return Results.NotFound(options.Error("流程定义尚未部署。"));
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(definitionId, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(xml))
        {
            return Results.NotFound(options.Error($"流程定义 '{definitionId}' 没有 XML 资源。"));
        }

        var svg = BpmnDiagramSvgRenderer.RenderSvg(xml, new DiagramHighlight(visited, active));

        return Results.Content(svg, "image/svg+xml");
    }

    /// <summary>
    /// 流程部署列表：本流程 key 下每个部署一行（<b>含历史版本</b>，按部署时间倒序），
    /// 每项带 <c>diagramUrl</c> —— 指向该版本定义的流程图图片，方便查看历史流程图信息。
    /// </summary>
    private static async Task<IResult> GetProcessDeploymentsAsync(
        HttpContext http,
        IProcessEngine engine,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var processKey = options.ProcessKey(http.RequestServices);
        var definitions = await engine.FindProcessDefinitionsAsync(ct: ct).ConfigureAwait(false);

        // 同 key 取版本最高者为「当前生效」；其余是历史版本
        var currentId = definitions
            .Where(definition => definition.Key == processKey)
            .OrderByDescending(definition => definition.Version)
            .Select(definition => definition.Id)
            .FirstOrDefault();

        var items = definitions
            .Where(definition => definition.Key == processKey)
            .Select(definition => new
            {
                definition.Id,
                definition.Key,
                definition.Name,
                definition.Version,
                definition.DeploymentId,
                DeployTime = definition.DeployTime,
                IsCurrent = definition.Id == currentId,

                // 相对链接（挂在本前缀下）：流程部署列表的「查看流程图」直接跳
                DiagramUrl = $"process-diagram.svg?definitionId={Uri.EscapeDataString(definition.Id)}",
            })
            .ToArray();

        return Results.Ok(items);
    }

    /// <summary>
    /// 可跳转的节点清单（流程管理页「目标步骤」下拉的数据源）：
    /// id + 名称 + 类型，按流程定义顺序返回 —— 前端渲染成「环节名 [节点id]」，
    /// 让操作人能对着 BPMN 核对（同名环节靠 id 区分，对齐老系统的跳转弹窗习惯）。
    /// </summary>
    private static async Task<IResult> GetProcessNodesAsync(
        HttpContext http,
        IProcessEngine engine,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var definitionId = await workflow
            .ResolveDefinitionIdAsync(options.ProcessKey(http.RequestServices), ct)
            .ConfigureAwait(false);

        if (definitionId is null)
        {
            return Results.NotFound(options.Error("流程定义尚未部署。"));
        }

        var process = (await engine.GetBpmnModelAsync(definitionId, ct).ConfigureAwait(false))
            ?.GetMainProcess();

        if (process is null)
        {
            return Results.NotFound(options.Error($"流程定义 '{definitionId}' 的模型不可用。"));
        }

        // 开始事件与顺序流没有跳转意义；其余节点（任务/网关/中间事件/结束事件）都可作为目标
        var nodes = process.FlowElements
            .Where(element => element is not StartEvent and not SequenceFlow)
            .Select(element => new
            {
                id = element.Id,
                name = string.IsNullOrWhiteSpace(element.Name) ? element.Id : element.Name,
                type = element switch
                {
                    UserTask => "userTask",
                    ServiceTask => "serviceTask",
                    Gateway => "gateway",
                    EndEvent => "endEvent",
                    _ => element.GetType().Name,
                },
            })
            .ToArray();

        return Results.Ok(new { processDefinitionId = definitionId, nodes });
    }


    /// <summary>改派待办（转办）：把当前环节的审批人改为指定用户。</summary>
    private static async Task<IResult> ReassignTaskAsync(
        string taskId,
        ReassignTaskRequest? request,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        if (await ForbidUnlessAuthorizedAsync(http, options, WorkflowAdminAction.ReassignTask, ct)
                .ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        try
        {
            var view = await workflow.ReassignTaskAsync(
                taskId,
                request?.UserId,
                ct).ConfigureAwait(false);

            return view is null
                ? Results.NotFound(options.Error($"待办任务 '{taskId}' 不存在。"))
                : Results.Ok(await Decorate(http.RequestServices, options.DecorateInstanceView, view)
                    .ConfigureAwait(false));
        }
        catch (WorkflowNotFoundException exception)
        {
            return Results.NotFound(options.Error(exception.Message));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception, options);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(options.Error(exception.Message));
        }
    }

    /// <summary>流程跳转：把未结束的实例搬到指定环节并继续推进。</summary>
    private static async Task<IResult> JumpToNodeAsync(
        string processInstanceId,
        JumpToNodeRequest? request,
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        if (await ForbidUnlessAuthorizedAsync(http, options, WorkflowAdminAction.JumpToNode, ct)
                .ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(request?.TargetActivityId))
        {
            return Results.BadRequest(options.Error("targetActivityId 必填（目标节点 id，可填结束事件强制办结）。"));
        }

        try
        {
            var view = await workflow.JumpToNodeAsync(
                processInstanceId,
                request.TargetActivityId,
                request.Reason,
                ct).ConfigureAwait(false);

            return view is null
                ? Results.NotFound(options.Error($"流程实例 '{processInstanceId}' 不存在。"))
                : Results.Ok(await Decorate(http.RequestServices, options.DecorateInstanceView, view)
                    .ConfigureAwait(false));
        }
        catch (WorkflowNotFoundException exception)
        {
            return Results.NotFound(options.Error(exception.Message));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception, options);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(options.Error(exception.Message));
        }
    }

    /// <summary>改派待办请求。</summary>
    /// <param name="UserId">新审批人；空表示退回未分配（候选人/组重新可见）。</param>
    public sealed record ReassignTaskRequest(string? UserId);

    /// <summary>流程跳转请求。</summary>
    /// <param name="TargetActivityId">目标节点 id（节点 / 网关 / 结束事件）。</param>
    /// <param name="Reason">跳转原因（审计用）。</param>
    public sealed record JumpToNodeRequest(string? TargetActivityId, string? Reason);

    // ─────────────────────────── 工具 ───────────────────────────

    /// <summary>
    /// 管理动作授权：未配置钩子或钩子拒绝时返回 403，允许时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// fail-closed 是刻意的：转办能「把别人待办改给自己再完成」，跳转能跳过审批环节，
    /// 默认放行等于给每个宿主装一个未授权后门。宿主不配置就等于不开放这两个端点。
    /// </remarks>
    private static async ValueTask<IResult?> ForbidUnlessAuthorizedAsync(
        HttpContext http,
        WorkflowEndpointOptions options,
        WorkflowAdminAction action,
        CancellationToken ct)
    {
        if (options.AuthorizeAdminAction is null)
        {
            return Results.Json(
                options.Error(
                    "管理动作未授权：宿主未配置 WorkflowEndpointOptions.AuthorizeAdminAction，" +
                    "引擎已默认拒绝该操作。"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!await options.AuthorizeAdminAction(http, action, ct).ConfigureAwait(false))
        {
            return Results.Json(
                options.Error($"当前身份无权执行该管理动作（{action}）。"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>解析查询参数的整数，非法或缺省回退到 <paramref name="fallback"/>。</summary>
    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static async ValueTask<object?> Decorate<TView>(
        IServiceProvider services,
        Func<IServiceProvider, TView, ValueTask<object?>>? decorator,
        TView view)
    {
        if (decorator is null)
        {
            return view;
        }

        var decorated = await decorator(services, view).ConfigureAwait(false);

        return decorated ?? view;
    }

    /// <summary>输入不合法 → 400，并把字段级明细一起回给前端。</summary>
    private static IResult Invalid(WorkflowValidationException exception, WorkflowEndpointOptions options) =>
        Results.BadRequest(options.Error(exception.Message, exception.Errors));
#pragma warning restore CS0618
}
