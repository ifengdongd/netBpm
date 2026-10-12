using NetBpm.Engine;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;
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
    public static RouteGroupBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/runtime").WithTags("runtime");

        // 流程实例
        group.MapPost("/process-instances", StartProcessInstanceAsync).Produces<StartProcessInstanceResponse>();
        group.MapGet("/process-instances/{processInstanceId}", GetProcessInstanceAsync);

        // 任务（P7 已落地）
        group.MapGet("/tasks", FindTasksAsync).Produces<IReadOnlyList<TaskResponse>>();
        group.MapPost("/tasks/{taskId}/claim", ClaimTaskAsync);
        group.MapPost("/tasks/{taskId}/complete", CompleteTaskAsync);

        // 变量（P5 后半段）
        group.MapGet("/process-instances/{processInstanceId}/variables", GetVariablesAsync);

        // 流程图数据（P9 交付项：BPMN XML + 活动高亮）
        group.MapGet("/process-instances/{processInstanceId}/diagram", GetDiagramAsync).Produces<DiagramResponse>();

        // 历史（P5 后半段写入，P9 提供读取）
        group.MapGet("/history/process-instances/{processInstanceId}", FindHistoricProcessInstanceAsync);
        group.MapGet("/history/process-instances/{processInstanceId}/activities", FindHistoricActivitiesAsync);
        group.MapGet("/history/process-instances/{processInstanceId}/tasks", FindHistoricTasksAsync)
            .Produces<IReadOnlyList<HistoricTaskResponse>>();

        return group;
    }

    // ─────────────────────────── 流程实例 ───────────────────────────

    private static async Task<IResult> StartProcessInstanceAsync(
        StartProcessInstanceRequest request,
        IProcessEngine engine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId))
        {
            return Results.BadRequest(new ErrorResponse("processDefinitionId 不能为空。"));
        }

        try
        {
            var processInstanceId = await engine.StartProcessInstanceAsync(
                request.ProcessDefinitionId,
                request.BusinessKey,
                request.StartUserId,
                JsonVariableReader.Read(request.Variables),
                ct).ConfigureAwait(false);

            return Results.Ok(new StartProcessInstanceResponse(processInstanceId));
        }
        catch (EngineException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> GetProcessInstanceAsync(
        string processInstanceId,
        IExecutionManager executionManager,
        CancellationToken ct)
    {
        var execution = await executionManager.FindByIdAsync(processInstanceId, ct).ConfigureAwait(false);

        if (execution is null)
        {
            return Results.NotFound(new ErrorResponse($"流程实例 '{processInstanceId}' 不存在。"));
        }

        return Results.Ok(new
        {
            execution.Id,
            execution.ProcessInstanceId,
            execution.ProcessDefinitionId,
            execution.BusinessKey,
            execution.IsActive,
            execution.IsScope,
            execution.IsConcurrent,
            Children = execution.Children.Select(child => child.Id).ToArray(),
        });
    }

    // ─────────────────────────── 任务 ───────────────────────────

    private static async Task<IResult> FindTasksAsync(
        string? assignee,
        string? candidateUser,
        IProcessEngine engine,
        CancellationToken ct)
    {
        try
        {
            var tasks = await engine
                .FindTasksAsync(new TaskQuery(assignee, candidateUser), ct)
                .ConfigureAwait(false);

            return Results.Ok(tasks.Select(TaskResponse.From).ToArray());
        }
        catch (EngineException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> ClaimTaskAsync(
        string taskId,
        ClaimTaskRequest? request,
        IProcessEngine engine,
        CancellationToken ct)
    {
        try
        {
            await engine.ClaimTaskAsync(taskId, request?.UserId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (EngineException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> CompleteTaskAsync(
        string taskId,
        CompleteTaskRequest? request,
        IProcessEngine engine,
        CancellationToken ct)
    {
        try
        {
            await engine.CompleteTaskAsync(taskId, JsonVariableReader.Read(request?.Variables), ct)
                .ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (EngineException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    // ─────────────────────────── 变量与历史 ───────────────────────────

    private static async Task<IResult> GetVariablesAsync(
        string processInstanceId,
        IProcessEngine engine,
        CancellationToken ct)
    {
        try
        {
            var variables = await engine.GetVariablesAsync(processInstanceId, ct).ConfigureAwait(false);
            return Results.Ok(variables);
        }
        catch (EngineException ex)
        {
            return Results.NotFound(new ErrorResponse(ex.Message));
        }
    }

    /// <summary>
    /// 流程图着色数据（§6.2 第 5 项）：<c>END_TIME_</c> 为空表示该节点正在执行。
    /// </summary>
    private static async Task<IResult> FindHistoricActivitiesAsync(
        string processInstanceId,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var activities = await engine.FindHistoricActivitiesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(activities.Select(HistoricActivityResponse.From).ToArray());
    }

    /// <summary>流程图数据：BPMN XML（含 BPMNDI）+ 活动高亮序列，供只读实例视图一次取全。</summary>
    private static async Task<IResult> GetDiagramAsync(
        string processInstanceId,
        IProcessEngine engine,
        CancellationToken ct)
    {
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

    private static async Task<IResult> FindHistoricProcessInstanceAsync(
        string processInstanceId,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var instance = await engine.FindHistoricProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        return instance is null
            ? Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 没有历史记录（历史写入可能未启用：NetBpm:EnableHistory）。"))
            : Results.Ok(HistoricProcessInstanceResponse.From(instance));
    }

    private static async Task<IResult> FindHistoricTasksAsync(
        string processInstanceId,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var tasks = await engine.FindHistoricTasksAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(tasks.Select(HistoricTaskResponse.From).ToArray());
    }
}

/// <summary>任务 DTO。</summary>
/// <param name="Id">任务 id。</param>
/// <param name="Name">任务名。</param>
/// <param name="TaskDefinitionKey">BPMN 中 userTask 的 id。</param>
/// <param name="Assignee">办理人。</param>
/// <param name="Owner">所有者。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ExecutionId">执行 id。</param>
/// <param name="FormKey">表单 Key（只透传，见 §1.3 第 10 条）。</param>
/// <param name="Priority">优先级。</param>
/// <param name="CreateTime">创建时间。</param>
/// <param name="DueDate">到期时间。</param>
/// <param name="ClaimTime">认领时间。</param>
public sealed record TaskResponse(
    string Id,
    string? Name,
    string? TaskDefinitionKey,
    string? Assignee,
    string? Owner,
    string? ProcessInstanceId,
    string? ExecutionId,
    string? FormKey,
    int Priority,
    DateTime? CreateTime,
    DateTime? DueDate,
    DateTime? ClaimTime)
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
            task.ExecutionId,
            task.FormKey,
            task.Priority,
            task.CreateTime,
            task.DueDate,
            task.ClaimTime);
    }
}
