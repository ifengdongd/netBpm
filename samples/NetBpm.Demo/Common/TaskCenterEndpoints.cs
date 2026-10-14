using Microsoft.AspNetCore.Routing;
using NetBpm.Demo.Order;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Runtime;
using NetBpm.Workflow;
using NetBpm.Workflow.Hosting;

namespace NetBpm.Demo.Common;

/// <summary>合并视图：待办任务 + 所属流程 + 业务上下文（待办列表 / 审批页的数据源）。</summary>
/// <param name="Task">任务视图（含表单定义与审批选项）。</param>
/// <param name="Process">所属流程信息（<see cref="ProcessInfoEnum"/> 里的一条）。</param>
/// <param name="BusinessKey">业务单号。</param>
/// <param name="Applicant">申请人。</param>
/// <param name="Status">流程状态（Running / Approved / Refused）。</param>
/// <param name="ApproveUrl">审批页地址（已按 taskId 渲染好占位符）。</param>
public sealed record TodoTaskView(
    WorkflowTaskView Task,
    ProcessInfo Process,
    string? BusinessKey,
    string? Applicant,
    string Status,
    string ApproveUrl);

/// <summary>合并视图：流程实例（申请单）+ 所属流程（首页「我的申请」/ 流程跳转页的数据源）。</summary>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="BusinessKey">业务单号。</param>
/// <param name="WorkflowTypeId">流程类型 id（发起时从流程信息枚举带入，落 <c>instances.workflow_type_id</c>）。</param>
/// <param name="Process">所属流程信息。</param>
/// <param name="Applicant">申请人。</param>
/// <param name="StartedAt">发起时间。</param>
/// <param name="Running">是否仍在运行。</param>
/// <param name="Status">流程状态（Running / Approved / Refused）。</param>
/// <param name="Outcome">结论文本（如「审批通过」）。</param>
/// <param name="CurrentStep">当前环节名（进行中才有）。</param>
/// <param name="TaskId">首个当前待办的任务 id（进行中才有）。</param>
/// <param name="ViewUrl">业务单查看地址（已按业务单号渲染好占位符）。</param>
/// <param name="ApproveUrl">审批页地址（进行中且有待办时才渲染得出）。</param>
public sealed record InstanceListItem(
    string ProcessInstanceId,
    string? BusinessKey,
    string? WorkflowTypeId,
    ProcessInfo Process,
    string? Applicant,
    DateTime? StartedAt,
    bool Running,
    string Status,
    string? Outcome,
    string? CurrentStep,
    string? TaskId,
    string? ViewUrl,
    string? ApproveUrl);

/// <summary>
/// 跨流程的合并端点：待办中心、申请单列表、任务详情、组织架构查询。
/// </summary>
/// <remarks>
/// <para>
/// 两条流程各自有自己的前缀（<c>/api/order</c> / <c>/api/leave</c>），那是「按流程办事」的面；
/// 这一组是<b>跨流程的合并面</b>：待办列表页与首页不关心单子是哪条流程发起的，
/// 一次查询拿全，流程归属由 <see cref="ProcessInfoEnum"/> 识别出来一起返回。
/// </para>
/// </remarks>
public static class TaskCenterEndpoints
{
    /// <summary>映射 <c>/api</c> 下的合并端点。</summary>
    public static RouteGroupBuilder MapTaskCenterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api").WithTags("task-center");

        group.MapGet("/tasks", FindTasksAsync);              // 待办中心：跨流程合并
        group.MapGet("/tasks/{taskId}", GetTaskAsync);       // 单个待办 + 所属实例（审批页数据源）
        group.MapGet("/instances", FindInstancesAsync);      // 申请单列表：跨流程合并

        // 组织架构（待办转移页选新审批人用）：JSON 演示模式是内置演示数据
        group.MapGet("/org/departments", async (IOrgDirectory directory, CancellationToken ct) =>
            Results.Ok(await directory.FindDepartmentsAsync(ct).ConfigureAwait(false)));
        group.MapGet("/org/employees", async (IOrgDirectory directory, CancellationToken ct) =>
            Results.Ok(await directory.FindEmployeesAsync(ct).ConfigureAwait(false)));

        return group;
    }

    // ─────────────────────────── 待办中心 ───────────────────────────

    /// <summary>按候选人 / 办理人查待办，两条流程的结果合并返回（新的在前）。</summary>
    private static async Task<IResult> FindTasksAsync(
        string? candidateUser,
        string? assignee,
        IWorkflowService workflow,
        IProcessEngine engine,
        CancellationToken ct)
    {
        try
        {
            var tasks = await workflow
                .FindTasksAsync(new TaskQuery(assignee, candidateUser, null), ct)
                .ConfigureAwait(false);

            var definitionKeys = await GetDefinitionKeysAsync(engine, ct).ConfigureAwait(false);
            var views = new List<TodoTaskView>(tasks.Count);

            foreach (var task in tasks)
            {
                // 流程实例查不到（理论上不该发生）的待办没有归属，跳过
                if (task.ProcessInstanceId is not { } processInstanceId
                    || await workflow.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false)
                        is not { } instance)
                {
                    continue;
                }

                if (!definitionKeys.TryGetValue(instance.ProcessDefinitionId, out var processKey)
                    || ProcessInfoEnum.Of(processKey) is not { } process)
                {
                    continue;
                }

                views.Add(new TodoTaskView(
                    Task: task,
                    Process: process,
                    BusinessKey: instance.BusinessKey,
                    Applicant: instance.Base.ApplyUser,
                    Status: instance.Result?.Outcome ?? WorkflowOutcomes.Running,
                    ApproveUrl: process.ResolveApproveUrl(task.TaskId)));
            }

            return Results.Ok(views);
        }
        catch (WorkflowValidationException exception)
        {
            return Results.BadRequest(new ErrorResponse(exception.Message, exception.Errors));
        }
    }

    /// <summary>
    /// 单个待办：任务视图 + 所属流程实例 + 流程信息 —— 审批页一次请求拿全。
    /// </summary>
    private static async Task<IResult> GetTaskAsync(
        string taskId,
        IWorkflowService workflow,
        ITaskManager taskManager,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var task = await taskManager.FindTaskByIdAsync(taskId, ct).ConfigureAwait(false);

        if (task?.ProcessInstanceId is not { } processInstanceId)
        {
            return Results.NotFound(new ErrorResponse($"待办任务 '{taskId}' 不存在（可能已完成）。"));
        }

        var instance = await workflow.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        if (instance is null)
        {
            return Results.NotFound(new ErrorResponse($"待办任务 '{taskId}' 所属的流程实例 '{processInstanceId}' 不存在。"));
        }

        var definitionKeys = await GetDefinitionKeysAsync(engine, ct).ConfigureAwait(false);

        if (!definitionKeys.TryGetValue(instance.ProcessDefinitionId, out var processKey)
            || ProcessInfoEnum.Of(processKey) is not { } process)
        {
            return Results.NotFound(new ErrorResponse($"流程定义 '{instance.ProcessDefinitionId}' 不在流程信息枚举里。"));
        }

        // 实例视图里往往带表单定义（含审批选项）；按 id 对上这份待办，
        // 对不上就退回查一次同实例的全部待办再对
        var taskView = instance.Tasks.FirstOrDefault(view => view.TaskId == taskId)
            ?? (await workflow
                .FindTasksAsync(new TaskQuery(ProcessInstanceId: processInstanceId), ct)
                .ConfigureAwait(false))
                .FirstOrDefault(view => view.TaskId == taskId)
            ?? new WorkflowTaskView(
                taskId, task.Name, null, processInstanceId, null, task.Assignee,
                [], [], task.CreateTime, []);

        return Results.Ok(new
        {
            task = taskView,
            instance,
            process,
            approveUrl = process.ResolveApproveUrl(taskId),
        });
    }

    // ─────────────────────────── 申请单列表 ───────────────────────────

    /// <summary>全部申请单（可选按申请人过滤），两条流程合并、新的在前。</summary>
    /// <remarks>
    /// 已结束的实例拿不到流程定义 id（运行态行已删，见 <see cref="WorkflowInstanceViewBuilder"/>），
    /// 归属判定先走定义 id，失败再按业务落点反查：订单号能在 <c>order_bill</c> 里找到就是订单流程，
    /// 否则视为请假流程。
    /// </remarks>
    private static async Task<IResult> FindInstancesAsync(
        string? applicant,
        IWorkflowService workflow,
        IProcessEngine engine,
        IOrderBillStore orderBills,
        CancellationToken ct)
    {
        var summaries = await workflow.FindInstancesAsync(ct).ConfigureAwait(false);
        var definitionKeys = await GetDefinitionKeysAsync(engine, ct).ConfigureAwait(false);

        var items = new List<InstanceListItem>(summaries.Count);

        foreach (var summary in summaries)
        {
            if (applicant is not null
                && !string.Equals(summary.Base.ApplyUser, applicant, StringComparison.Ordinal))
            {
                continue;
            }

            // 实例视图一次拿全：流程定义（判归属）+ 当前环节 + 首个待办 id
            var instance = await workflow
                .FindInstanceAsync(summary.ProcessInstanceId, ct)
                .ConfigureAwait(false);

            string? processKey = instance is not null
                && !string.IsNullOrEmpty(instance.ProcessDefinitionId)
                && definitionKeys.TryGetValue(instance.ProcessDefinitionId, out var byDefinition)
                    ? byDefinition
                    : null;

            // 兜底：已结束实例没有定义 id，按业务落点反查归属
            processKey ??= await ResolveProcessKeyByBusinessAsync(orderBills, summary.Base.BusinessId, ct)
                .ConfigureAwait(false);

            if (ProcessInfoEnum.Of(processKey) is not { } process)
            {
                continue;
            }

            var task = instance?.Tasks.Count > 0 ? instance.Tasks[0] : null;

            items.Add(new InstanceListItem(
                ProcessInstanceId: summary.ProcessInstanceId,
                BusinessKey: summary.Base.BusinessId,
                WorkflowTypeId: summary.Base.WorkflowTypeId,
                Process: process,
                Applicant: summary.Base.ApplyUser,
                StartedAt: summary.StartedAt,
                Running: summary.Running,
                Status: summary.Status,
                Outcome: summary.Result?.ActivityName,
                CurrentStep: task?.Name,
                TaskId: task?.TaskId,
                ViewUrl: summary.Base.BusinessId is { } businessId
                    ? process.ResolveViewUrl(businessId)
                    : null,
                ApproveUrl: task is not null ? process.ResolveApproveUrl(task.TaskId) : null));
        }

        return Results.Ok(items.OrderByDescending(item => item.StartedAt ?? DateTime.MinValue));
    }

    /// <summary>按业务单号反查流程归属：order_bill 里有 → 订单流程；否则按请假流程。</summary>
    private static async ValueTask<string> ResolveProcessKeyByBusinessAsync(
        IOrderBillStore orderBills,
        string? businessId,
        CancellationToken ct)
    {
        if (long.TryParse(businessId, out var orderId)
            && await orderBills.FindAsync(orderId, ct).ConfigureAwait(false) is not null)
        {
            return ProcessInfoEnum.Order.Name;
        }

        return ProcessInfoEnum.Leave.Name;
    }

    // ─────────────────────────── 内部工具 ───────────────────────────

    /// <summary>定义 id → 流程 key 的对照表（一次查询，整个请求复用）。</summary>
    private static async ValueTask<IReadOnlyDictionary<string, string>> GetDefinitionKeysAsync(
        IProcessEngine engine,
        CancellationToken ct)
    {
        var definitions = await engine
            .FindProcessDefinitionsAsync(ct)
            .ConfigureAwait(false);

        return definitions.ToDictionary(definition => definition.Id, definition => definition.Key);
    }
}
