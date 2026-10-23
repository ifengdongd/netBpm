using NetBpm.Rest.Security;
using NetBpm.Rest.Services;
using NetBpm.Runtime;

namespace NetBpm.Rest.Endpoints;

/// <summary>流程实例端点：启动 / 查询 / 生命周期 / 活动干预 / 迁移 / 批量。</summary>
public static partial class RuntimeEndpoints
{
    /// <summary>
    /// 启动流程实例：<c>processDefinitionId</c> 精确指定版本；只给 <c>processDefinitionKey</c> 时
    /// 按 key 启动最新版本（对应 <c>runtimeService.startProcessInstanceByKey</c>）。
    /// </summary>
    private static async Task<IResult> StartProcessInstanceAsync(
        StartProcessInstanceRequest request,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProcessDefinitionId)
            && string.IsNullOrWhiteSpace(request.ProcessDefinitionKey))
        {
            return InvalidRequest("processDefinitionId 与 processDefinitionKey 至少要给一个（都给时以 id 为准）。");
        }

        var processInstanceId = await instances.StartAsync(request, requestContext, ct).ConfigureAwait(false);

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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        await instances.DeleteAsync(processInstanceId, deleteReason, requestContext, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>挂起流程实例：待办仍可查，但不能完成 / 认领（对应 suspendProcessInstanceById）。</summary>
    private static async Task<IResult> SuspendProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        await instances.SuspendAsync(processInstanceId, requestContext, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>激活流程实例（与挂起对称）。</summary>
    private static async Task<IResult> ActivateProcessInstanceAsync(
        string processInstanceId,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        await instances.ActivateAsync(processInstanceId, requestContext, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>实例详情（租户校验由 <see cref="RuntimeAccess.RequireInstanceAccess"/> 完成）。</summary>
    private static IResult GetProcessInstanceAsync(HttpContext http) =>
        Results.Ok(ProcessInstanceResponse.From(http.InstanceOf()!));

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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetActivityId))
        {
            return InvalidRequest("targetActivityId 必填（目标节点 id，可填结束事件强制办结）。");
        }

        await instances
            .JumpAsync(processInstanceId, request.TargetActivityId, request.Reason, requestContext, ct)
            .ConfigureAwait(false);

        return Results.NoContent();
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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? "控制台取消活动" : request.Reason.Trim();

        await instances
            .CancelActivityAsync(processInstanceId, activityId, reason, requestContext, ct)
            .ConfigureAwait(false);

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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        await instances
            .StartBeforeActivityAsync(processInstanceId, activityId, request?.Reason, requestContext, ct)
            .ConfigureAwait(false);

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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetProcessDefinitionId))
        {
            return InvalidRequest("targetProcessDefinitionId 必填（目标流程定义 id）。");
        }

        var plan = await instances
            .MigrateAsync(processInstanceId, request, requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(plan);
    }

    /// <summary>查流程实例的迁移记录（按迁移时间倒序）。</summary>
    private static async Task<IResult> FindMigrationsAsync(
        string processInstanceId,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        var records = await instances.FindMigrationsAsync(processInstanceId, ct).ConfigureAwait(false);

        return Results.Ok(records);
    }

    /// <summary>批量删除运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchDeleteProcessInstancesAsync(
        BatchDeleteInstancesRequest? request,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.ProcessInstanceIds, "processInstanceIds", out var instanceIds, out var error))
        {
            return error!;
        }

        var result = await instances
            .BatchDeleteAsync(instanceIds, request!.DeleteReason, requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }

    /// <summary>批量挂起运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchSuspendProcessInstancesAsync(
        BatchInstanceSuspensionRequest? request,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.ProcessInstanceIds, "processInstanceIds", out var instanceIds, out var error))
        {
            return error!;
        }

        var result = await instances
            .BatchSetSuspensionAsync(suspend: true, instanceIds, requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }

    /// <summary>批量激活运行中的流程实例（逐项执行，不是原子操作）。</summary>
    private static async Task<IResult> BatchActivateProcessInstancesAsync(
        BatchInstanceSuspensionRequest? request,
        RequestContext requestContext,
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.ProcessInstanceIds, "processInstanceIds", out var instanceIds, out var error))
        {
            return error!;
        }

        var result = await instances
            .BatchSetSuspensionAsync(suspend: false, instanceIds, requestContext, ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
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
        IInstanceCommandService instances,
        CancellationToken ct)
    {
        if (!TryValidateBatch(request?.ProcessInstanceIds, "processInstanceIds", out var instanceIds, out var error))
        {
            return error!;
        }

        if (string.IsNullOrWhiteSpace(request!.TargetProcessDefinitionId))
        {
            return InvalidRequest("targetProcessDefinitionId 必填（目标流程定义 id）。");
        }

        var result = await instances
            .BatchMigrateAsync(
                instanceIds,
                request.TargetProcessDefinitionId,
                request.ActivityMappings,
                request.Reason,
                requestContext,
                ct)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }
}
