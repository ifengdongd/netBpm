using System.Text;
using NetBpm.Admin.Storage;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
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
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin").WithTags("admin");

        // ── 流程模型（bpmn_model，设计器编辑源）──
        group.MapGet("/models", ListModelsAsync).Produces<IReadOnlyList<BpmnModelSummary>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程模型");
        group.MapGet("/models/{id}", GetModelAsync).Produces<BpmnModelDetail>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看流程模型");
        group.MapPut("/models/{id}", SaveModelAsync).Accepts<SaveBpmnModelRequest>("application/json")
            .RequireAdminScope(TaskScopes.AdminModelWrite, "保存流程模型");
        group.MapDelete("/models/{id}", DeleteModelAsync)
            .RequireAdminScope(TaskScopes.AdminModelWrite, "删除流程模型");
        group.MapGet("/models/{id}/history", ListModelHistoryAsync)
            .Produces<IReadOnlyList<BpmnModelRevisionSummary>>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看模型历史");
        group.MapGet("/models/{id}/history/{revisionId}", GetModelRevisionAsync)
            .Produces<BpmnModelRevisionDetail>()
            .RequireAdminScope(TaskScopes.AdminModelRead, "查看模型历史");
        group.MapPost("/models/{id}/rollback/{revisionId}", RollbackModelAsync)
            .Produces<BpmnModelDetail>()
            .RequireAdminScope(TaskScopes.AdminModelWrite, "回滚模型版本");
        group.MapPost("/models/{id}/publish", PublishModelAsync).Produces<DeployResponse>()
            .RequireAdminScope(TaskScopes.AdminModelWrite, "发布流程模型");

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
        group.MapGet("/audit", FindAuditAsync).Produces<AdminAuditListResponse>()
            .RequireAdminScope(TaskScopes.AdminAuditRead, "查看管理动作审计");
        group.MapDelete("/audit", PurgeAuditAsync).Produces<AdminAuditPurgeResponse>()
            .RequireAdminScope(TaskScopes.AdminAuditPurge, "清理管理动作审计");

        // ── 管理指标 ──
        group.MapGet("/metrics", GetMetricsAsync).Produces<AdminMetricsResponse>()
            .RequireAdminScope(TaskScopes.AdminMetricsRead, "查看管理指标");

        // ── 历史归档 ──
        group.MapPost("/history/archive", ArchiveHistoryAsync).Produces<HistoryArchiveResult>()
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "归档历史数据");
        group.MapGet("/history/archives", FindArchivesAsync).Produces<HistoryArchivePage>()
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "查看历史归档");
        group.MapGet("/history/archives/{processInstanceId}", GetArchiveAsync)
            .RequireAdminScope(TaskScopes.AdminHistoryArchive, "查看历史归档");

        return group;
    }

    private static async Task<IReadOnlyList<BpmnModelSummary>> ListModelsAsync(
        BpmnModelStore store,
        CancellationToken ct) => await store.ListAsync(ct).ConfigureAwait(false);

    private static async Task<IResult> GetModelAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is { } model
            ? Results.Ok(model)
            : Results.NotFound();

    private static async Task<IResult> SaveModelAsync(
        string id,
        SaveBpmnModelRequest request,
        BpmnModelStore store,
        IProcessEngine engine,
        RequestContext requestContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.BpmnXml))
        {
            return Results.BadRequest(new ErrorResponse("bpmnXml 不能为空。"));
        }

        IReadOnlyList<ValidationError> issues;

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(request.BpmnXml));
            issues = await engine.ValidateAsync(stream, ct).ConfigureAwait(false);
        }
        catch (BpmnXmlParseException exception)
        {
            return Results.BadRequest(new ErrorResponse($"BPMN XML 解析失败：{exception.Message}"));
        }

        var errors = issues.Where(issue => issue.IsError).ToArray();
        var warnings = issues.Where(issue => !issue.IsError).ToArray();

        // 草稿语义：校验结果如实返回，但不阻止保存半成品；发布/部署时再强制拦截。
        var revision = await store
            .SaveAsync(id, request, requestContext.UserId, "save", ct)
            .ConfigureAwait(false);

        return Results.Ok(new SaveBpmnModelResponse(id, revision, errors, warnings));
    }

    private static async Task<IResult> DeleteModelAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.DeleteAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IReadOnlyList<BpmnModelRevisionSummary>> ListModelHistoryAsync(
        string id,
        BpmnModelStore store,
        CancellationToken ct) => await store.ListHistoryAsync(id, ct).ConfigureAwait(false);

    private static async Task<IResult> GetModelRevisionAsync(
        string id,
        string revisionId,
        BpmnModelStore store,
        CancellationToken ct) =>
        await store.GetRevisionAsync(id, revisionId, ct).ConfigureAwait(false) is { } revision
            ? Results.Ok(revision)
            : Results.NotFound();

    private static async Task<IResult> RollbackModelAsync(
        string id,
        string revisionId,
        BpmnModelStore store,
        RequestContext requestContext,
        CancellationToken ct) =>
        await store.RollbackAsync(id, revisionId, requestContext.UserId, ct).ConfigureAwait(false) is { } model
            ? Results.Ok(model)
            : Results.NotFound();

    /// <summary>发布模型：用当前 XML 走引擎部署（校验/版本递增与设计器部署完全一致）。</summary>
    private static async Task<IResult> PublishModelAsync(
        string id,
        BpmnModelStore store,
        IProcessEngine engine,
        CancellationToken ct)
    {
        var model = await store.GetAsync(id, ct).ConfigureAwait(false);

        if (model is null)
        {
            return Results.NotFound();
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(model.BpmnXml));

        var definition = await engine
            .DeployAsync(
                stream,
                $"{model.Id}.bpmn20.xml",
                $"模型发布：{model.Name}",
                ct: ct)
            .ConfigureAwait(false);

        return Results.Ok(new DeployResponse(
            definition.Id,
            definition.Key,
            definition.Version,
            definition.DeploymentId,
            definition.ResourceName,
            $"模型发布：{model.Name}"));
    }

    private static async Task<IReadOnlyList<ProcessType>> ListProcessTypesAsync(
        ProcessTypeStore store,
        CancellationToken ct) => await store.ListAsync(ct).ConfigureAwait(false);

    private static async Task<IResult> GetProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) is { } item
            ? Results.Ok(item)
            : Results.NotFound();

    private static async Task<IResult> CreateProcessTypeAsync(
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        CancellationToken ct)
    {
        var created = await store.CreateAsync(request, ct).ConfigureAwait(false);
        return Results.Created($"/api/admin/process-types/{created.Id}", created);
    }

    private static async Task<IResult> UpdateProcessTypeAsync(
        string id,
        SaveProcessTypeRequest request,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.UpdateAsync(id, request, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> DeleteProcessTypeAsync(
        string id,
        ProcessTypeStore store,
        CancellationToken ct) =>
        await store.DeleteAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound();

    // ─────────────────────────── 管理动作审计 ───────────────────────────

    /// <summary>
    /// 分页查管理动作审计（按时间倒序）。
    /// <para>
    /// 过滤：<c>actor</c> / <c>action</c> / <c>targetType</c> / <c>targetId</c> /
    /// <c>processInstanceId</c> / <c>success</c> / <c>since</c> / <c>until</c> / <c>tenantId</c>；
    /// 分页 <c>firstResult</c> / <c>maxResults</c>（默认 20，上限 200）。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindAuditAsync(
        string? actor,
        string? action,
        string? targetType,
        string? targetId,
        string? processInstanceId,
        string? tenantId,
        bool? success,
        DateTime? since,
        DateTime? until,
        int? firstResult,
        int? maxResults,
        RequestContext requestContext,
        IAdminAuditStore store,
        CancellationToken ct)
    {
        var query = new AdminAuditQuery(
            actor,
            action,
            targetType,
            targetId,
            processInstanceId,
            ResolveTenant(tenantId, requestContext),
            success,
            UtcTime.Normalize(since),
            UtcTime.Normalize(until),
            Math.Max(0, firstResult ?? 0),
            Math.Clamp(maxResults ?? 20, 1, 200));

        var page = await store.QueryAsync(query, ct).ConfigureAwait(false);

        return Results.Ok(new AdminAuditListResponse(page.Total, [.. page.Items.Select(MapAudit)]));
    }

    /// <summary>
    /// 审计保留策略：删除写入时间早于 <c>before</c> 的记录（<c>before</c> 必填）。
    /// <para>
    /// 清理本身也是管理动作 —— 清理后写一条 <c>audit.purge</c>（时间晚于 <c>before</c>，不会被同一次清理删掉），
    /// 因此「谁在什么时候清了多少」始终可查。
    /// </para>
    /// </summary>
    private static async Task<IResult> PurgeAuditAsync(
        DateTime? before,
        string? tenantId,
        RequestContext requestContext,
        IAdminAuditStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (before is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "before 必填（删除该时刻之前的审计记录），避免误清全部历史。"));
        }

        var cutoff = UtcTime.Normalize(before.Value);

        // 允许 before <= now（含 now），但拒绝未来时间：否则本次清理会把它自己的审计记录一起删掉
        if (cutoff > DateTime.UtcNow.AddMinutes(1))
        {
            return Results.BadRequest(new ErrorResponse(
                "before 不能晚于当前时间：本次清理的审计记录会被自己删掉。"));
        }

        var tenant = ResolveTenant(tenantId, requestContext);

        try
        {
            var removed = await store.PurgeAsync(tenant, cutoff, ct).ConfigureAwait(false);

            AdminDiagnostics.AuditPurged.Add(removed);

            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.AuditPurge,
                AdminAuditTargets.Audit,
                tenant ?? "all",
                processInstanceId: null,
                detail: new { before = cutoff, removed },
                success: true,
                ct: ct).ConfigureAwait(false);

            return Results.Ok(new AdminAuditPurgeResponse(removed, cutoff, tenant));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.AuditPurge,
                AdminAuditTargets.Audit,
                tenant ?? "all",
                processInstanceId: null,
                detail: new { before = cutoff },
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            throw;
        }
    }

    // ─────────────────────────── 指标 ───────────────────────────

    /// <summary>
    /// 管理指标：运行中 / 历史实例、流程定义、待办分布、管理动作计数、归档量。
    /// <para>
    /// <c>pendingTasks</c> 系列在内存宿主为 <see langword="null"/>（没有任务表可聚合），
    /// 而不是伪造的 0 —— 前端应显示为「不可用」。
    /// </para>
    /// </summary>
    private static async Task<AdminMetricsResponse> GetMetricsAsync(
        string? tenantId,
        int? timeZoneOffsetMinutes,
        int? windowDays,
        RequestContext requestContext,
        IProcessEngine engine,
        IAdminMetricsStore metricsStore,
        IAdminAuditStore auditStore,
        IHistoryArchiveStore archiveStore,
        CancellationToken ct)
    {
        var tenant = ResolveTenant(tenantId, requestContext);
        var now = DateTime.UtcNow;
        var window = Math.Clamp(windowDays ?? 7, 1, 365);
        var offset = timeZoneOffsetMinutes ?? 0;

        var running = await engine
            .CountProcessInstancesAsync(new ProcessInstanceQuery(TenantId: tenant), ct)
            .ConfigureAwait(false);

        var definitions = await engine
            .CountProcessDefinitionsAsync(new ProcessDefinitionQuery(TenantId: tenant), ct)
            .ConfigureAwait(false);

        var historic = await engine
            .CountHistoricProcessInstancesAsync(new HistoricProcessInstanceQuery(TenantId: tenant), ct)
            .ConfigureAwait(false);

        var finished = await engine
            .CountHistoricProcessInstancesAsync(
                new HistoricProcessInstanceQuery(TenantId: tenant, Finished: true), ct)
            .ConfigureAwait(false);

        var deleted = await engine
            .CountHistoricProcessInstancesAsync(
                new HistoricProcessInstanceQuery(TenantId: tenant, Deleted: true), ct)
            .ConfigureAwait(false);

        var tasks = await metricsStore
            .GetTaskMetricsAsync(tenant, now, offset, ct)
            .ConfigureAwait(false);

        var audit = await auditStore
            .SummarizeAsync(tenant, now.AddDays(-window), ct)
            .ConfigureAwait(false);

        int? archives = null;

        try
        {
            var page = await archiveStore
                .QueryAsync(new HistoryArchiveQuery(TenantId: tenant, MaxResults: 1), ct)
                .ConfigureAwait(false);

            archives = page.Total;
        }
        catch (EngineNotImplementedException)
        {
            // 内存宿主没有持久化历史可归档：指标如实返回 null
        }

        return new AdminMetricsResponse(
            now,
            tenant,
            window,
            running,
            definitions,
            historic,
            finished,
            deleted,
            tasks?.Pending,
            tasks?.Overdue,
            tasks?.DueToday,
            tasks is null ? null : MergeByProcessKey(tasks.ByProcessDefinitionId),
            archives,
            [.. audit.Select(item => new AdminAuditActionCount(item.Action, item.Count))]);
    }

    // ─────────────────────────── 历史归档 ───────────────────────────

    /// <summary>
    /// 归档已结束实例的历史：把 5 张 <c>ACT_HI_*</c> 的行快照进 <c>admin_history_archive</c>，
    /// 再删除原行（同一事务）。<c>dryRun=true</c> 只统计不写。
    /// </summary>
    private static async Task<IResult> ArchiveHistoryAsync(
        ArchiveHistoryRequest? request,
        RequestContext requestContext,
        IHistoryArchiveStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        if (request?.Before is null)
        {
            return Results.BadRequest(new ErrorResponse(
                "before 必填（归档结束时间早于该时刻的实例），避免误删最近的历史。"));
        }

        var tenant = ResolveTenant(request.TenantId, requestContext);

        var archiveRequest = new HistoryArchiveRequest(
            UtcTime.Normalize(request.Before.Value),
            request.DryRun,
            request.BatchSize ?? 200,
            tenant,
            requestContext.UserId);

        try
        {
            var result = await store.ArchiveAsync(archiveRequest, ct).ConfigureAwait(false);

            if (!request.DryRun)
            {
                await auditor.RecordAsync(
                    requestContext,
                    AdminAuditActions.HistoryArchive,
                    AdminAuditTargets.History,
                    tenant ?? "all",
                    processInstanceId: null,
                    detail: new
                    {
                        before = archiveRequest.Before,
                        archivedCount = result.ArchivedCount,
                        remainingCount = result.RemainingCount,
                        batchSize = archiveRequest.BatchSize,
                    },
                    success: true,
                    ct: ct).ConfigureAwait(false);
            }

            return Results.Ok(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await auditor.RecordAsync(
                requestContext,
                AdminAuditActions.HistoryArchive,
                AdminAuditTargets.History,
                tenant ?? "all",
                processInstanceId: null,
                detail: new { before = archiveRequest.Before, batchSize = archiveRequest.BatchSize },
                success: false,
                error: exception.Message,
                ct: ct).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>分页查归档记录（不含 payload）。</summary>
    private static async Task<IResult> FindArchivesAsync(
        string? tenantId,
        string? businessKey,
        string? processInstanceId,
        DateTime? archivedAfter,
        int? firstResult,
        int? maxResults,
        RequestContext requestContext,
        IHistoryArchiveStore store,
        CancellationToken ct)
    {
        var query = new HistoryArchiveQuery(
            ResolveTenant(tenantId, requestContext),
            businessKey,
            processInstanceId,
            UtcTime.Normalize(archivedAfter),
            Math.Max(0, firstResult ?? 0),
            Math.Clamp(maxResults ?? 20, 1, 200));

        var page = await store.QueryAsync(query, ct).ConfigureAwait(false);

        return Results.Ok(page);
    }

    /// <summary>取单个实例的归档详情（含历史行 JSON 快照）。</summary>
    private static async Task<IResult> GetArchiveAsync(
        string processInstanceId,
        RequestContext requestContext,
        IHistoryArchiveStore store,
        CancellationToken ct)
    {
        var detail = await store.FindAsync(processInstanceId, ct).ConfigureAwait(false);

        if (detail is null)
        {
            return Results.NotFound(new ErrorResponse($"流程实例 '{processInstanceId}' 没有归档记录。"));
        }

        TenantGuard.Ensure(requestContext, detail.Summary.TenantId, "归档记录", processInstanceId);

        return Results.Ok(detail);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>
    /// 解析管理查询的租户口径：JWT 只能看自己租户；限定租户的服务密钥不能越界；
    /// 未受限（开发 / 可信服务）时才允许显式指定或全租户。
    /// </summary>
    private static string? ResolveTenant(string? requested, RequestContext context) =>
        RequestIdentity.Tenant(requested, context);

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
