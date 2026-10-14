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
public static class WorkflowEndpoints
{
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
            JumpToNodeAsync(processInstanceId, request, http, workflow, options, ct)).Produces<object>();        group.MapGet("/process-definition", (HttpContext http, IProcessEngine engine, IWorkflowService workflow, CancellationToken ct) =>
            GetProcessDefinitionAsync(http, engine, workflow, options, ct));

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

    /// <summary>业务登记列表（新的在前），列表页用。</summary>
    private static async Task<IResult> FindInstancesAsync(
        HttpContext http,
        IWorkflowService workflow,
        WorkflowEndpointOptions options,
        CancellationToken ct)
    {
        var summaries = await workflow.FindInstancesAsync(ct).ConfigureAwait(false);

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
        try
        {
            var view = await workflow.ReassignTaskAsync(
                taskId,
                request?.UserId,
                request?.Reason,
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
/// <param name="Reason">改派原因（审计用）。</param>
public sealed record ReassignTaskRequest(string? UserId, string? Reason);

/// <summary>流程跳转请求。</summary>
/// <param name="TargetActivityId">目标节点 id（节点 / 网关 / 结束事件）。</param>
/// <param name="Reason">跳转原因（审计用）。</param>
public sealed record JumpToNodeRequest(string? TargetActivityId, string? Reason);    // ─────────────────────────── 工具 ───────────────────────────

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
}
