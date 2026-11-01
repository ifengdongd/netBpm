using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Migration;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Services;

/// <summary>
/// 流程实例命令服务：启动 / 删除 / 挂起 / 激活 / 跳转 / 活动干预 / 迁移 + 批量变体。
/// <para>
/// 端点到引擎之间为什么需要它：这些动作的「业务形状」是三件事的组合 ——
/// <b>引擎命令</b> + <b>管理动作审计</b> + <b>逐项隔离的批处理</b>。
/// 留在端点里就只能靠 <c>IResult</c> 串联（审计要知道 HTTP 状态码、批量要知道怎么拼响应），
/// 于是每个端点重复一遍同样的 40 行。收进服务后，端点只剩「绑定 → 调服务 → 映射 TypedResults」。
/// </para>
/// <para>
/// 授权不在本类：单对象端点由 <see cref="RuntimeAccess.RequireInstanceAccess"/> 过滤器完成，
/// 批量端点由端点上的 <c>RequireAdminScope</c> 完成整批判定，循环内只做逐项租户隔离。
/// </para>
/// </summary>
public interface IInstanceCommandService
{
    /// <summary>启动实例（按 id 精确版本 / 按 key 最新版本），返回流程实例 id。</summary>
    Task<string> StartAsync(
        StartProcessInstanceRequest request,
        RequestContext context,
        CancellationToken ct);

    /// <summary>删除（撤销）运行中的实例；历史保留并写入删除原因。</summary>
    Task DeleteAsync(string processInstanceId, string? deleteReason, RequestContext context, CancellationToken ct);

    /// <summary>挂起实例。</summary>
    Task SuspendAsync(string processInstanceId, RequestContext context, CancellationToken ct);

    /// <summary>激活实例。</summary>
    Task ActivateAsync(string processInstanceId, RequestContext context, CancellationToken ct);

    /// <summary>跳转实例到目标节点（删除当前待办）。</summary>
    Task JumpAsync(
        string processInstanceId,
        string targetActivityId,
        string? reason,
        RequestContext context,
        CancellationToken ct);

    /// <summary>取消某个活动上的活动实例；活动已结束时抛 409。</summary>
    Task CancelActivityAsync(
        string processInstanceId,
        string activityId,
        string? reason,
        RequestContext context,
        CancellationToken ct);

    /// <summary>在指定活动之前额外启动一个令牌；活动非法时抛 409。</summary>
    Task StartBeforeActivityAsync(
        string processInstanceId,
        string activityId,
        string? reason,
        RequestContext context,
        CancellationToken ct);

    /// <summary>迁移预演（<c>ValidateOnly</c>）或实际迁移。</summary>
    Task<MigrationPlanResponse> MigrateAsync(
        string processInstanceId,
        MigrateProcessInstanceRequest request,
        RequestContext context,
        CancellationToken ct);

    /// <summary>查实例迁移记录（按时间倒序）。</summary>
    Task<IReadOnlyList<MigrationRecordResponse>> FindMigrationsAsync(
        string processInstanceId,
        CancellationToken ct);

    /// <summary>批量删除（逐项执行，单项失败不影响其它项）。</summary>
    Task<BatchResultResponse> BatchDeleteAsync(
        IReadOnlyList<string> processInstanceIds,
        string? deleteReason,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量挂起 / 激活（逐项执行，单项失败不影响其它项）。</summary>
    Task<BatchResultResponse> BatchSetSuspensionAsync(
        bool suspend,
        IReadOnlyList<string> processInstanceIds,
        RequestContext context,
        CancellationToken ct);

    /// <summary>批量迁移到同一目标定义（逐项执行，单项失败不影响其它项）。</summary>
    Task<BatchResultResponse> BatchMigrateAsync(
        IReadOnlyList<string> processInstanceIds,
        string targetProcessDefinitionId,
        IReadOnlyDictionary<string, string>? activityMappings,
        string? reason,
        RequestContext context,
        CancellationToken ct);
}

/// <summary><see cref="IInstanceCommandService"/> 的默认实现。</summary>
internal sealed class InstanceCommandService(
    IProcessEngine engine,
    IExecutionManager executionManager,
    RuntimeAudit audit,
    IApprovalTimeline timeline) : IInstanceCommandService
{
    /// <summary>
    /// 给<b>管理干预</b>补一条时间线。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要补：管理动作审计（<c>admin_audit_log</c>）只回答「哪个调用方在什么时候调了哪个接口」，
    /// 它挂在调用方身上、不挂在实例上。审计真正断档的地方是<b>实例自己的时间线</b>——
    /// 跳转 / 终止 / 挂起这些动作有授权、有审计，却在这张单的审批记录里看不到，
    /// 于是「这张单为什么突然出现在另一个节点」只能靠人去对日志。
    /// </para>
    /// <para>
    /// 写失败不阻断：<see cref="IApprovalTimeline.AppendAsync"/> 内部已经吞掉异常，
    /// 这里再包一层只是为了让「引擎已经跳过去了但时间线没写」这件事留下一条日志。
    /// </para>
    /// </remarks>
    private async ValueTask AppendInterventionAsync(
        string processInstanceId,
        TimelineEntryKind kind,
        string? comment,
        string? targetActivityId,
        RequestContext context,
        CancellationToken ct)
    {
        // 返回值只用来确认「有没有写进去」；写失败时 AppendAsync 内部已记日志，这里不再重复。
        _ = await timeline.AppendAsync(
            processInstanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: processInstanceId,
                Kind: kind,
                ActivityId: null,
                ActivityName: null,
                TaskId: null,
                ActorId: context.SubjectId,
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: comment,
                TargetNodeId: targetActivityId,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct).ConfigureAwait(false);
    }

    public async Task<string> StartAsync(
        StartProcessInstanceRequest request,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var byId = !string.IsNullOrWhiteSpace(request.ProcessDefinitionId);

        // JWT 下发起人 / 租户只能来自令牌：与请求体不一致直接 403（防止冒充他人或跨租户启动）
        var startUserId = RequestIdentity.User(request.StartUserId, context);
        var tenantId = RequestIdentity.Tenant(request.TenantId, context);

        // 按 id 启动时引擎不接收 tenant 参数，这里先确认定义属于生效租户，避免跨租户启动
        if (byId && context.IsTenantRestricted)
        {
            var definitions = await engine
                .FindProcessDefinitionsAsync(
                    new ProcessDefinitionQuery(TenantId: RequestIdentity.Tenant(null, context)),
                    ct)
                .ConfigureAwait(false);

            if (!definitions.Any(definition => string.Equals(
                    definition.Id,
                    request.ProcessDefinitionId,
                    StringComparison.Ordinal)))
            {
                throw new ResourceNotFoundException(
                    $"流程定义 '{request.ProcessDefinitionId}' 不存在。");
            }
        }

        var variables = JsonVariableReader.Read(request.Variables);

        return byId
            ? await engine.StartProcessInstanceAsync(
                request.ProcessDefinitionId!,
                request.BusinessKey,
                startUserId,
                variables,
                ct).ConfigureAwait(false)
            : await engine.StartProcessInstanceByKeyAsync(
                request.ProcessDefinitionKey!,
                request.BusinessKey,
                startUserId,
                variables,
                tenantId,
                ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string processInstanceId,
        string? deleteReason,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceDelete,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { deleteReason },
            async () =>
            {
                await engine.DeleteProcessInstanceAsync(processInstanceId, deleteReason, ct)
                    .ConfigureAwait(false);

                // 撤销（删除）运行中的实例对审批流来说就是一次终止：单据不再往下走，
                // 时间线上必须有一行说明它去哪了，否则审批记录里只剩一串没有下文的待办。
                await AppendInterventionAsync(
                    processInstanceId,
                    TimelineEntryKind.InstanceTerminated,
                    string.IsNullOrWhiteSpace(deleteReason) ? "实例被撤销（无原因）" : deleteReason,
                    targetActivityId: null,
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task SuspendAsync(string processInstanceId, RequestContext context, CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceSuspend,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            detail: null,
            async () =>
            {
                await engine.SuspendProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

                await AppendInterventionAsync(
                    processInstanceId,
                    TimelineEntryKind.InstanceSuspended,
                    "实例被挂起",
                    targetActivityId: null,
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task ActivateAsync(string processInstanceId, RequestContext context, CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceActivate,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            detail: null,
            async () =>
            {
                await engine.ActivateProcessInstanceAsync(processInstanceId, ct).ConfigureAwait(false);

                await AppendInterventionAsync(
                    processInstanceId,
                    TimelineEntryKind.InstanceResumed,
                    "实例被恢复",
                    targetActivityId: null,
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task JumpAsync(
        string processInstanceId,
        string targetActivityId,
        string? reason,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceJump,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { targetActivityId, reason },
            async () =>
            {
                await engine
                    .MoveActivityToAsync(processInstanceId, targetActivityId, reason, ct)
                    .ConfigureAwait(false);

                // 跳转会把当前待办直接删掉：这是审批流上最容易「查无对证」的一类动作，
                // 目标节点写进 TargetNodeId，原因写进 Comment，审计才能还原「谁把单挪到了哪、为什么」。
                await AppendInterventionAsync(
                    processInstanceId,
                    TimelineEntryKind.InstanceJumped,
                    string.IsNullOrWhiteSpace(reason) ? null : reason,
                    targetActivityId,
                    context,
                    ct).ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task CancelActivityAsync(
        string processInstanceId,
        string activityId,
        string? reason,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceActivityCancel,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { activityId, reason },
            async () =>
            {
                await ExecuteConflictAsync(
                        new CancelActivityCommand(processInstanceId, activityId, reason),
                        ct)
                    .ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task StartBeforeActivityAsync(
        string processInstanceId,
        string activityId,
        string? reason,
        RequestContext context,
        CancellationToken ct) =>
        await audit.RunAsync(
            context,
            AdminAuditActions.InstanceActivityStart,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new { activityId, reason },
            async () =>
            {
                await ExecuteConflictAsync(
                        new StartBeforeActivityCommand(processInstanceId, activityId),
                        ct)
                    .ConfigureAwait(false);

                return true;
            },
            ct).ConfigureAwait(false);

    public async Task<MigrationPlanResponse> MigrateAsync(
        string processInstanceId,
        MigrateProcessInstanceRequest request,
        RequestContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var mappings = request.ActivityMappings is null
            ? null
            : new Dictionary<string, string>(request.ActivityMappings, StringComparer.Ordinal);

        var migration = new ProcessInstanceMigrationRequest(
            processInstanceId,
            request.TargetProcessDefinitionId!,
            mappings,
            request.Reason,
            context.UserId);

        if (request.ValidateOnly)
        {
            var preview = await engine.ValidateMigrationAsync(migration, ct).ConfigureAwait(false);

            return MigrationPlanResponse.From(preview, applied: false);
        }

        return await audit.RunAsync(
            context,
            AdminAuditActions.InstanceMigrate,
            AdminAuditTargets.ProcessInstance,
            processInstanceId,
            processInstanceId,
            new
            {
                targetProcessDefinitionId = request.TargetProcessDefinitionId,
                activityMappingCount = mappings?.Count ?? 0,
                reason = request.Reason,
            },
            async () =>
            {
                var plan = await engine.MigrateProcessInstanceAsync(migration, ct).ConfigureAwait(false);

                return MigrationPlanResponse.From(plan, applied: true);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MigrationRecordResponse>> FindMigrationsAsync(
        string processInstanceId,
        CancellationToken ct)
    {
        var records = await engine.FindMigrationsAsync(processInstanceId, ct).ConfigureAwait(false);

        return [.. records.Select(MigrationRecordResponse.From)];
    }

    public async Task<BatchResultResponse> BatchDeleteAsync(
        IReadOnlyList<string> processInstanceIds,
        string? deleteReason,
        RequestContext context,
        CancellationToken ct)
    {
        var items = new List<BatchItemResult>(processInstanceIds.Count);

        foreach (var instanceId in processInstanceIds)
        {
            try
            {
                await RuntimeAccess.EnsureInstanceAsync(instanceId, context, executionManager, ct)
                    .ConfigureAwait(false);

                await engine.DeleteProcessInstanceAsync(instanceId, deleteReason, ct)
                    .ConfigureAwait(false);

                // 批量删除逐项审计：一条记录对应一个实例，失败项也留痕
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.InstanceBatchDelete,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        new { deleteReason },
                        success: true,
                        error: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException exception)
            {
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.InstanceBatchDelete,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        new { deleteReason },
                        success: false,
                        error: exception.Message,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }

    public async Task<BatchResultResponse> BatchSetSuspensionAsync(
        bool suspend,
        IReadOnlyList<string> processInstanceIds,
        RequestContext context,
        CancellationToken ct)
    {
        var action = suspend ? AdminAuditActions.InstanceSuspend : AdminAuditActions.InstanceActivate;
        var items = new List<BatchItemResult>(processInstanceIds.Count);

        foreach (var instanceId in processInstanceIds)
        {
            try
            {
                await RuntimeAccess.EnsureInstanceAsync(instanceId, context, executionManager, ct)
                    .ConfigureAwait(false);

                if (suspend)
                {
                    await engine.SuspendProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
                }
                else
                {
                    await engine.ActivateProcessInstanceAsync(instanceId, ct).ConfigureAwait(false);
                }

                await audit.RecordAsync(
                        context,
                        action,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        new { batch = true },
                        success: true,
                        error: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException exception)
            {
                await audit.RecordAsync(
                        context,
                        action,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        new { batch = true },
                        success: false,
                        error: exception.Message,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }

    public async Task<BatchResultResponse> BatchMigrateAsync(
        IReadOnlyList<string> processInstanceIds,
        string targetProcessDefinitionId,
        IReadOnlyDictionary<string, string>? activityMappings,
        string? reason,
        RequestContext context,
        CancellationToken ct)
    {
        var mappings = activityMappings is null
            ? null
            : new Dictionary<string, string>(activityMappings, StringComparer.Ordinal);

        var items = new List<BatchItemResult>(processInstanceIds.Count);

        foreach (var instanceId in processInstanceIds)
        {
            var detail = new
            {
                targetProcessDefinitionId,
                activityMappingCount = mappings?.Count ?? 0,
                reason,
                batch = true,
            };

            try
            {
                await RuntimeAccess.EnsureInstanceAsync(instanceId, context, executionManager, ct)
                    .ConfigureAwait(false);

                await engine
                    .MigrateProcessInstanceAsync(
                        new ProcessInstanceMigrationRequest(
                            instanceId,
                            targetProcessDefinitionId,
                            mappings,
                            reason,
                            context.UserId),
                        ct)
                    .ConfigureAwait(false);

                await audit.RecordAsync(
                        context,
                        AdminAuditActions.InstanceMigrate,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        detail,
                        success: true,
                        error: null,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, true, null));
            }
            catch (EngineException exception)
            {
                await audit.RecordAsync(
                        context,
                        AdminAuditActions.InstanceMigrate,
                        AdminAuditTargets.ProcessInstance,
                        instanceId,
                        instanceId,
                        detail,
                        success: false,
                        error: exception.Message,
                        ct)
                    .ConfigureAwait(false);

                items.Add(new BatchItemResult(instanceId, false, exception.Message));
            }
        }

        return BatchResults.From(items);
    }

    /// <summary>
    /// 活动干预命令：引擎对「活动已结束 / 令牌不在该节点 / 被并发取消」抛 <see cref="EngineException"/>（默认 400），
    /// 这里翻译成 409 —— 调用方应据此刷新而不是盲目重试。
    /// </summary>
    private async Task ExecuteConflictAsync(ICommand<Unit> command, CancellationToken ct)
    {
        try
        {
            await engine.ExecuteAsync(command, ct).ConfigureAwait(false);
        }
        catch (EngineException exception) when (exception is not EngineObjectNotFoundException)
        {
            throw new EngineConflictException(exception.Message);
        }
    }
}
