using NetBpm.Rest.Security;
using NetBpm.Rest.Services;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>历史与流程图端点（数据一律来自 <c>ACT_HI_*</c>）。</summary>
public static partial class RuntimeEndpoints
{
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
        IHistoryQueryService history,
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

        var page = await history.FindInstancesAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            page.Items,
            page.Total,
            first,
            size,
            response,
            envelope ?? false);
    }

    private static async Task<IResult> FindHistoricProcessInstanceAsync(
        string processInstanceId,
        HttpContext http,
        IHistoryQueryService history,
        CancellationToken ct)
    {
        // 过滤器走历史兜底时已取到该行，避免重复查询
        var historic = http.HistoricOf();

        if (historic is not null)
        {
            return Results.Ok(HistoricProcessInstanceResponse.From(historic));
        }

        var instance = await history.FindInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

        return instance is null
            ? Results.NotFound(new ErrorResponse(
                $"流程实例 '{processInstanceId}' 没有历史记录（历史写入可能未启用：NetBpm:EnableHistory）。"))
            : Results.Ok(instance);
    }

    /// <summary>
    /// 流程图着色数据（§6.2 第 5 项）：<c>END_TIME_</c> 为空表示该节点正在执行。
    /// </summary>
    private static async Task<IResult> FindHistoricActivitiesAsync(
        string processInstanceId,
        IHistoryQueryService history,
        CancellationToken ct)
    {
        var activities = await history.FindActivitiesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(activities);
    }

    /// <summary>
    /// 历史变量：回答「这个实例上出现过哪些变量、最后是什么值」（运行态删除后仍在）。
    /// <para>变量是敏感数据：与运行态变量同口径做参与人校验（发起人 / 运行中任务的参与人）。</para>
    /// </summary>
    private static async Task<IResult> FindHistoricVariablesAsync(
        string processInstanceId,
        IHistoryQueryService history,
        CancellationToken ct)
    {
        var variables = await history.FindVariablesAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(variables);
    }

    /// <summary>
    /// 历史任务（已办列表）：可选 <c>assignee</c> / <c>onlyFinished</c> 收窄，
    /// 分页参数与待办一致（<c>firstResult</c> / <c>maxResults</c>），总数放 <c>X-Total-Count</c>。
    /// <para>
    /// 同一条 handler 服务两个路由：<c>/history/process-instances/{id}/tasks</c>（路由参数绑定
    /// <c>processInstanceId</c>）与 <c>/history/tasks</c>（查询参数绑定）。跨租户靠查询条件过滤，
    /// 返回空集合而不是 404（不泄露是否存在）。
    /// </para>
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
        IHistoryQueryService history,
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

        var page = await history.FindTasksAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            page.Items,
            page.Total,
            first,
            size,
            response,
            envelope ?? false);
    }

    /// <summary>流程图数据：BPMN XML（含 BPMNDI）+ 活动高亮序列，供只读实例视图一次取全。</summary>
    private static async Task<IResult> GetDiagramAsync(
        string processInstanceId,
        IHistoryQueryService history,
        CancellationToken ct)
    {
        var diagram = await history.GetDiagramAsync(processInstanceId, ct).ConfigureAwait(false);

        return diagram is null
            ? Results.NotFound(new ErrorResponse($"流程实例 '{processInstanceId}' 不存在，且没有历史记录。"))
            : Results.Ok(diagram);
    }
}
