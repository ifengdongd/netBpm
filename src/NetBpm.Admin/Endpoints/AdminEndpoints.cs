using NetBpm.Admin.Storage;
using NetBpm.Persistence.Admin;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点：流程模型（设计器）、流程类型（workflow_type 注册表）、管理动作审计、
/// 指标与历史归档。
/// <para>
/// 模型保存前会跑<b>与部署完全相同的校验</b>（阻断级直接 422，提示级随响应返回），
/// 每次保存/回滚都会追加版本快照；发布 = 用当前 XML 走引擎部署。
/// </para>
/// <para>
/// 🔐 细粒度授权：读模型要 <c>netbpm.admin.model.read</c>，写 / 回滚 / 发布要
/// <c>netbpm.admin.model.write</c>，审计 <c>netbpm.admin.audit.read</c>，指标
/// <c>netbpm.admin.metrics.read</c>，归档 <c>netbpm.admin.history.archive</c>。
/// 超级范围 <c>netbpm.admin</c> 覆盖全部；未启用凭据（开发）与 API Key（可信服务）放行。
/// </para>
/// <para>
/// 处理器按领域拆分到 partial 文件：<see cref="AdminEndpoints.Models"/>（模型 + 流程类型）、
/// <see cref="AdminEndpoints.Governance"/>（审计 + 指标 + 归档）；本文件只保留路由表与公共辅助。
/// </para>
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// Admin 列表接口的默认页大小：与审计 / 归档 / 作业 / 引擎列表统一为 20。
    /// <para>
    /// 以前模型列表默认 100、其它 20，客户端不传 <c>maxResults</c> 时页大小随端点漂移；
    /// 统一后「不传就是第一页 20 条」，控制台都显式分页，不受影响。
    /// </para>
    /// </summary>
    private const int DefaultPageSize = 20;

    /// <summary>Admin 列表接口的页大小上限（避免一次拉全表）。</summary>
    private const int MaxPageSize = 200;

    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").WithTags("admin");

        // ── 流程模型（bpmn_model，设计器编辑源）──
        group.MapGet("/models", ListModelsAsync)
            .Produces<IReadOnlyList<BpmnModelSummary>>()
            .Produces<PagedResponse<BpmnModelSummary>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程模型");
        group.MapPost("/models", CreateModelAsync).Accepts<SaveBpmnModelRequest>("application/json")
            .Produces<SaveBpmnModelResponse>(StatusCodes.Status201Created)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "新建流程模型");
        group.MapPost("/models/import", ImportModelAsync)
            .Accepts<string>("text/xml")
            .Accepts<string>("application/xml")
            .Produces<SaveBpmnModelResponse>(StatusCodes.Status201Created)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "导入流程模型");
        group.MapPost("/models/from-definition/{processDefinitionId}", CreateModelFromDefinitionAsync)
            .Produces<SaveBpmnModelResponse>(StatusCodes.Status201Created)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "从已部署定义反建流程模型");
        group.MapPost("/models/{id}/duplicate", DuplicateModelAsync).Accepts<DuplicateBpmnModelRequest>("application/json")
            .Produces<SaveBpmnModelResponse>(StatusCodes.Status201Created)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "复制流程模型");
        group.MapGet("/models/{id}", GetModelAsync).Produces<BpmnModelDetail>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程模型");
        group.MapPut("/models/{id}", SaveModelAsync).Accepts<SaveBpmnModelRequest>("application/json")
            .Produces<RevisionConflictResponse>(StatusCodes.Status409Conflict)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "保存流程模型");
        group.MapPatch("/models/{id}", PatchModelAsync).Accepts<PatchBpmnModelRequest>("application/json")
            .Produces<RevisionConflictResponse>(StatusCodes.Status409Conflict)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "保存流程模型");
        group.MapPost("/models/{id}/validate", ValidateModelAsync).Produces<ModelValidationResponse>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "校验流程模型");
        group.MapDelete("/models/{id}", DeleteModelAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "删除流程模型");
        group.MapGet("/models/{id}/history", ListModelHistoryAsync)
            .Produces<IReadOnlyList<BpmnModelRevisionSummary>>()
            .Produces<PagedResponse<BpmnModelRevisionSummary>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看模型历史");
        group.MapGet("/models/{id}/history/{revisionId}", GetModelRevisionAsync)
            .Produces<BpmnModelRevisionDetail>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看模型历史");
        group.MapPost("/models/{id}/rollback/{revisionId}", RollbackModelAsync)
            .Produces<BpmnModelDetail>()
            .Produces<RevisionConflictResponse>(StatusCodes.Status409Conflict)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "回滚模型版本");
        group.MapDelete("/models/{id}/history", PruneModelHistoryAsync).Produces<ModelHistoryPruneResponse>()
            .RequireAdminScope(TaskScopes.AdminModelWrite, "清理模型历史");
        group.MapPost("/models/{id}/publish", PublishModelAsync).Produces<DeployResponse>()
            .Produces<RevisionConflictResponse>(StatusCodes.Status409Conflict)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "发布流程模型");
        group.MapGet("/models/{id}/publications", ListModelPublicationsAsync)
            .Produces<IReadOnlyList<ModelPublicationResponse>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看模型发布历史");

        // ── 流程类型（workflow_type 注册表）──
        group.MapGet("/process-types", ListProcessTypesAsync).Produces<IReadOnlyList<ProcessType>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程类型");
        group.MapGet("/process-types/{id}", GetProcessTypeAsync).Produces<ProcessType>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程类型");
        group.MapPost("/process-types", CreateProcessTypeAsync).Accepts<SaveProcessTypeRequest>("application/json")
            .RequireAdminScope(TaskScopes.AdminModelWrite, "新建流程类型");
        group.MapPut("/process-types/{id}", UpdateProcessTypeAsync).Accepts<SaveProcessTypeRequest>("application/json")
            .RequireAdminScope(TaskScopes.AdminModelWrite, "修改流程类型");
        group.MapDelete("/process-types/{id}", DeleteProcessTypeAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "删除流程类型");

        // ── 管理动作审计（转办 / 跳转 / 变量直写等）──
        group.MapGet("/audit", FindAuditAsync)
            .Produces<AdminAuditListResponse>()
            .Produces<PagedResponse<AdminAuditItem>>()
            .RequireAdminScope(TaskScopes.AdminAuditRead, "查看管理动作审计");
        group.MapGet("/audit/options", GetAuditOptionsAsync).Produces<AdminAuditOptions>()
            .RequireAdminScope(TaskScopes.AdminAuditRead, "查看管理动作审计");
        group.MapGet("/audit/export", ExportAuditAsync).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .RequireAdminScope(TaskScopes.AdminAuditRead, "导出管理动作审计");
        group.MapDelete("/audit", PurgeAuditAsync).Produces<AdminAuditPurgeResponse>()
            .RequireAdminScope(TaskScopes.AdminAuditPurge, "清理管理动作审计");

        // ── 管理指标 ──
        group.MapGet("/metrics", GetMetricsAsync).Produces<AdminMetricsResponse>()
            .RequireAdminScope(TaskScopes.AdminMetricsRead, "查看管理指标");

        // ── 定时作业（边界定时器诊断）──
        group.MapGet("/jobs", FindJobsAsync).Produces<TimerJobSummary>()
            .Produces<PagedResponse<TimerJobSummary>>()
            .RequireAdminScope(TaskScopes.AdminJobRead, "查看定时作业");
        group.MapGet("/jobs/stats", GetJobStatsAsync).Produces<TimerJobStats>()
            .RequireAdminScope(TaskScopes.AdminJobRead, "查看定时作业");
        group.MapPost("/jobs/{jobId}/due-date", RescheduleJobAsync).Accepts<RescheduleTimerJobRequest>("application/json")
            .RequireAdminScope(TaskScopes.AdminJobWrite, "修改定时作业到期时间");
        group.MapPost("/jobs/{jobId}/execute", ExecuteJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "立即触发定时作业");
        group.MapPost("/jobs/{jobId}/retry", RetryDeadLetterJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "重试死信作业");
        group.MapDelete("/jobs/{jobId}", DeleteDeadLetterJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "删除死信作业");

        // ── 异步作业（asyncBefore / asyncAfter）──
        group.MapGet("/jobs/async", FindAsyncJobsAsync).Produces<AsyncJobSummary>()
            .Produces<PagedResponse<AsyncJobSummary>>()
            .RequireAdminScope(TaskScopes.AdminJobRead, "查看异步作业");
        group.MapPost("/jobs/async/{jobId}/execute", ExecuteAsyncJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "立即执行异步作业");
        group.MapPost("/jobs/async/{jobId}/retry", RetryAsyncJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "重试异步死信作业");
        group.MapDelete("/jobs/async/{jobId}", DeleteAsyncJobAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "删除异步死信作业");

        // ── 事务性 outbox ──
        group.MapGet("/outbox", FindOutboxEventsAsync).Produces<OutboxEventSummary>()
            .Produces<PagedResponse<OutboxEventSummary>>()
            .RequireAdminScope(TaskScopes.AdminJobRead, "查看 outbox 事件");
        group.MapPost("/outbox/{eventId}/retry", RetryOutboxEventAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "重试 outbox 死信事件");
        group.MapDelete("/outbox/{eventId}", DeleteOutboxEventAsync)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAdminScope(TaskScopes.AdminJobWrite, "删除 outbox 事件");

        // ── 历史归档 ──
        group.MapPost("/history/archive", ArchiveHistoryAsync).Produces<HistoryArchiveResult>()
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "归档历史数据");
        group.MapGet("/history/archives", FindArchivesAsync).Produces<HistoryArchivePage>()
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "查看历史归档");
        group.MapGet("/history/archives/{processInstanceId}", GetArchiveAsync)
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "查看历史归档");

        return group;
    }

    // ─────────────────────────── 公共辅助 ───────────────────────────

    /// <summary>
    /// 解析管理查询的租户口径：JWT 只能看自己租户；限定租户的服务密钥不能越界；
    /// 未受限（开发 / 可信服务）时才允许显式指定或全租户。
    /// </summary>
    private static string? ResolveTenant(string? requested, RequestContext context) =>
        RequestIdentity.Tenant(requested, context);

    /// <summary>
    /// 「失败也要写审计」公共模板：执行 <paramref name="operation"/>，成功后按需写成功审计
    /// （<paramref name="successDetail"/> 为 <see langword="null"/> 表示跳过，如归档 dry-run），
    /// 失败时写失败审计并原样抛出。审计清理与历史归档共用，保证审计语义一致。
    /// </summary>
    /// <param name="context">请求上下文（操作人来源）。</param>
    /// <param name="auditor">审计写入器。</param>
    /// <param name="action">审计动作（<see cref="AdminAuditActions"/>）。</param>
    /// <param name="targetType">审计目标类型（<see cref="AdminAuditTargets"/>）。</param>
    /// <param name="tenant">目标租户（<see langword="null"/> 表示全租户）。</param>
    /// <param name="failureDetail">失败审计明细（异常时求值）。</param>
    /// <param name="successDetail">成功审计明细工厂（入参为操作结果；<see langword="null"/> 表示不写成功审计）。</param>
    /// <param name="operation">实际业务操作。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task<T> WithAuditTrailAsync<T>(
        RequestContext context,
        AdminActionAuditor auditor,
        string action,
        string targetType,
        string? tenant,
        Func<object> failureDetail,
        Func<T, object?>? successDetail,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        try
        {
            var result = await operation(ct).ConfigureAwait(false);

            if (successDetail is not null)
            {
                await auditor.RecordAsync(
                    context,
                    action,
                    targetType,
                    tenant ?? "all",
                    processInstanceId: null,
                    detail: successDetail(result),
                    success: true,
                    ct: ct).ConfigureAwait(false);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await auditor.RecordAsync(
                context,
                action,
                targetType,
                tenant ?? "all",
                processInstanceId: null,
                detail: failureDetail(),
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            throw;
        }
    }

    private static AdminAuditItem MapAudit(AdminAuditEntry entry) =>
        new(
            entry.Id,
            entry.TenantId,
            entry.Actor,
            entry.AuthMode,
            entry.Action,
            entry.TargetType,
            entry.TargetId,
            entry.ProcessInstanceId,
            entry.Detail,
            entry.Success,
            entry.Error,
            entry.CreatedAt);

    /// <summary>按流程 key 合并（同 key 的多版本定义合并成一行）。</summary>
    private static Dictionary<string, int> MergeByProcessKey(IReadOnlyDictionary<string, int> byDefinitionId)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (definitionId, count) in byDefinitionId)
        {
            var key = DefinitionIdParser.Key(definitionId) ?? definitionId;

            merged[key] = merged.TryGetValue(key, out var existing) ? existing + count : count;
        }

        return merged;
    }
}
