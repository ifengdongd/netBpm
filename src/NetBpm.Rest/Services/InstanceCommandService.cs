using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Migration;
using NetBpm.Persistence.Admin;
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
    RuntimeAudit audit) : IInstanceCommandService
{
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
