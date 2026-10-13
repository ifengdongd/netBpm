using NetBpm.Engine;
using NetBpm.Runtime;
using NetBpm.Workflow;

namespace NetBpm.Demo.LeaveApproval.Leave;

/// <summary>
/// 请假审批 Demo 的 HTTP 适配层。
/// </summary>
/// <remarks>
/// <para>
/// 🔑 <b>这一层只做三件事</b>：绑定请求、把应用层的视图摊成 Demo 自己的 DTO、把异常翻成状态码。
/// 真正干活的是 <see cref="IWorkflowService"/>（发起 / 环节信息 / 提交 / 历史四件事都在它里面），
/// 所以换成 gRPC、消息队列、控制台调用时，业务代码一行都不用改。
/// </para>
/// <para>设计要点：<b>前端不写死任何审批选项</b>。实例视图会把当前待办连同它的表单定义
/// （含 <c>approvalResult</c> 的候选值）一次返回，前端照着渲染；提交时回传所选值即可。</para>
/// </remarks>
public static class LeaveEndpoints
{
    public static RouteGroupBuilder MapLeaveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/leave").WithTags("leave");

        group.MapPost("/instances", StartAsync).Produces<InstanceView>();
        group.MapGet("/instances", FindInstancesAsync).Produces<IReadOnlyList<InstanceSummary>>();
        group.MapGet("/instances/{processInstanceId}", GetInstanceAsync).Produces<InstanceView>();
        group.MapGet("/instances/{processInstanceId}/history", GetHistoryAsync).Produces<WorkflowHistoryView>();

        group.MapGet("/tasks", FindTasksAsync).Produces<IReadOnlyList<WorkflowTaskView>>();
        group.MapPost("/tasks/{taskId}/submit", SubmitTaskAsync).Produces<InstanceView>();

        group.MapGet("/business", GetBusinessAsync).Produces<LeaveBusinessDocument>();
        group.MapGet("/process-definition", GetProcessDefinitionAsync);

        return group;
    }

    // ─────────────────────────── 流程实例 ───────────────────────────

    /// <summary>发起请假：建流程实例，返回首个待办（「提交请假申请」）及其表单。</summary>
    /// <remarks>
    /// <c>businessKey</c> 是请假单号，<b>必须是正整数</b>（业务表里 <c>business_id</c> 是 <c>bigint</c>）；
    /// 留空则由 Demo 发一个（真实系统里这是业务单号生成器的活，见 <see cref="LeaveBusinessKey"/>）。
    /// </remarks>
    private static async Task<IResult> StartAsync(
        StartWorkflowRequest? request,
        IWorkflowService workflow,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.BadRequest(new LeaveErrorResponse("applicant 不能为空。"));
        }

        var businessKey = string.IsNullOrWhiteSpace(request.BusinessKey)
            ? LeaveBusinessKey.Next()
            : request.BusinessKey.Trim();

        if (!LeaveBusinessKey.IsValid(businessKey))
        {
            return Results.BadRequest(new LeaveErrorResponse(
                $"businessKey 必须是正整数（业务表里 business_id 是 bigint），当前为 '{request.BusinessKey}'。"));
        }

        try
        {
            var view = await workflow
                .StartAsync(LeaveProcessBootstrapper.ProcessKey, request with { BusinessKey = businessKey }, ct)
                .ConfigureAwait(false);

            return Results.Ok(ToInstanceView(view));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(new LeaveErrorResponse(exception.Message));
        }
    }

    /// <summary>请假单列表（业务登记 + 审批结论，用于演示页左侧列表）。</summary>
    private static async Task<IResult> FindInstancesAsync(
        IWorkflowService workflow,
        CancellationToken ct)
    {
        var summaries = await workflow.FindInstancesAsync(ct).ConfigureAwait(false);

        return Results.Ok(summaries.Select(ToInstanceSummary).ToArray());
    }

    /// <summary>流程实例详情：变量 + 当前待办（含表单定义）+ 审批结论 + 流程参数。</summary>
    private static async Task<IResult> GetInstanceAsync(
        string processInstanceId,
        IWorkflowService workflow,
        CancellationToken ct)
    {
        var view = await workflow.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        return view is null
            ? Results.NotFound(new LeaveErrorResponse($"流程实例 '{processInstanceId}' 不存在。"))
            : Results.Ok(ToInstanceView(view));
    }

    /// <summary>审批历史记录：环节序列 + 审批记录（谁在什么时候批了什么）。</summary>
    private static async Task<IResult> GetHistoryAsync(
        string processInstanceId,
        IWorkflowService workflow,
        CancellationToken ct)
    {
        var history = await workflow.FindHistoryAsync(processInstanceId, ct).ConfigureAwait(false);

        return history is null
            ? Results.NotFound(new LeaveErrorResponse($"流程实例 '{processInstanceId}' 不存在。"))
            : Results.Ok(history);
    }

    // ─────────────────────────── 任务 ───────────────────────────

    /// <summary>按候选人 / 办理人 / 流程实例查待办（演示页的「待办中心」）。</summary>
    private static async Task<IResult> FindTasksAsync(
        string? candidateUser,
        string? assignee,
        string? processInstanceId,
        IWorkflowService workflow,
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
            return Invalid(exception);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(new LeaveErrorResponse(exception.Message));
        }
    }

    /// <summary>
    /// 提交任务表单：<c>values</c> 的键必须是流程定义里的 <c>formProperty/@id</c>。
    /// <para>
    /// 以「组长审批」为例，前端回传 <c>{"values":{"approvalResult":"2"}}</c>，
    /// 该属性 <c>variable="result"</c> ⇒ 流程变量 <c>result = "2"</c>，
    /// 随后排他网关按 <c>${result == 2}</c> 把流程送往「拒绝结束」。
    /// </para>
    /// <para>
    /// 想顺带补参数就传 <c>paraDict</c>（<b>有值就按 key 更新</b>）；想记下办理人（按候选组审批时
    /// 任务上还没有 assignee）就传 <c>operator</c>。
    /// </para>
    /// </summary>
    private static async Task<IResult> SubmitTaskAsync(
        string taskId,
        SubmitWorkflowTaskRequest? request,
        IWorkflowService workflow,
        CancellationToken ct)
    {
        try
        {
            var view = await workflow.SubmitAsync(taskId, request, ct).ConfigureAwait(false);

            return view is null ? Results.NoContent() : Results.Ok(ToInstanceView(view));
        }
        catch (WorkflowNotFoundException exception)
        {
            return Results.NotFound(new LeaveErrorResponse(exception.Message));
        }
        catch (WorkflowValidationException exception)
        {
            return Invalid(exception);
        }
        catch (EngineException exception)
        {
            return Results.BadRequest(new LeaveErrorResponse(exception.Message));
        }
    }

    // ─────────────────────────── 业务数据 / 流程定义 ───────────────────────────

    /// <summary>
    /// 业务数据核对入口：<b>落点自述 + 全部业务数据</b>，方便打开核对。
    /// </summary>
    /// <remarks>
    /// 只依赖 <see cref="ILeaveBusinessInspector"/>，所以 <c>Leave:Store</c> 在 JSON / MySql
    /// 之间切换时这里一行都不用改（键名由实现方给：JSON 实现给区块名，MySQL 实现给表名）。
    /// </remarks>
    private static async Task<IResult> GetBusinessAsync(
        ILeaveBusinessInspector inspector,
        CancellationToken ct)
    {
        var snapshot = await inspector.SnapshotAsync(ct).ConfigureAwait(false);

        var payload = new Dictionary<string, object?>(snapshot, StringComparer.Ordinal)
        {
            ["store"] = inspector.Store,
            ["location"] = inspector.Location,
        };

        return Results.Ok(payload);
    }

    /// <summary>当前生效的流程定义（含 BPMN XML，便于对照表单属性）。</summary>
    private static async Task<IResult> GetProcessDefinitionAsync(
        IProcessEngine engine,
        IWorkflowService workflow,
        CancellationToken ct)
    {
        var definitionId = await workflow
            .ResolveDefinitionIdAsync(LeaveProcessBootstrapper.ProcessKey, ct)
            .ConfigureAwait(false);

        if (definitionId is null)
        {
            return Results.NotFound(new LeaveErrorResponse("流程定义尚未部署。"));
        }

        var xml = await engine.GetProcessDefinitionXmlAsync(definitionId, ct).ConfigureAwait(false);

        return Results.Ok(new { processDefinitionId = definitionId, xml });
    }

    // ─────────────────────────── DTO 映射 ───────────────────────────

    /// <summary>应用层视图 → Demo 的 HTTP 视图（只差 <c>result</c> 的形状：天数 / 事由摊成两列）。</summary>
    private static InstanceView ToInstanceView(WorkflowInstanceView view) => new(
        ProcessInstanceId: view.ProcessInstanceId,
        BusinessKey: view.BusinessKey,
        ProcessDefinitionId: view.ProcessDefinitionId,
        Running: view.Running,
        Variables: view.Variables,
        Tasks: view.Tasks,
        Result: LeaveResultRecord.From(view.Result),
        StartedAt: view.StartedAt,
        Base: view.Base,
        Parameters: view.Parameters);

    /// <summary>应用层摘要 → Demo 的列表行（天数 / 事由同样从流程参数里取）。</summary>
    private static InstanceSummary ToInstanceSummary(WorkflowInstanceSummary summary) => new(
        ProcessInstanceId: summary.ProcessInstanceId,
        BusinessKey: summary.Base.BusinessId,
        Applicant: summary.Base.ApplyUser ?? string.Empty,
        Days: LeaveParameterNames.DaysOf(summary.Parameters),
        Reason: LeaveParameterNames.ReasonOf(summary.Parameters),
        StartedAt: summary.StartedAt,
        Status: summary.Status,
        Outcome: summary.Result?.ActivityName);

    /// <summary>输入不合法 → 400，并把字段级明细一起回给前端。</summary>
    private static IResult Invalid(WorkflowValidationException exception) =>
        Results.BadRequest(new LeaveErrorResponse(
            exception.Message,
            exception.Errors.Count == 0 ? null : exception.Errors));
}
