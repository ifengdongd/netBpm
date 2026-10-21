using NetBpm.Engine;
using NetBpm.Persistence.Admin;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Diagnostics;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：管理动作审计、管理指标与历史归档的处理器。
/// 路由表见 <see cref="AdminEndpoints.MapAdminEndpoints"/>。
/// </summary>
public static partial class AdminEndpoints
{
    // ─────────────────────────── 管理动作审计 ───────────────────────────

    /// <summary>
    /// 分页查管理动作审计（按时间倒序）。
    /// <para>
    /// 过滤：<c>actor</c> / <c>action</c> / <c>targetType</c> / <c>targetId</c> /
    /// <c>processInstanceId</c> / <c>success</c> / <c>since</c> / <c>until</c> / <c>tenantId</c>；
    /// 分页 <c>firstResult</c> / <c>maxResults</c>（默认 20，上限 200）。
    /// </para>
    /// <para>
    /// 分页协议与引擎列表端点对齐：<c>envelope=true</c> 返回 <see cref="PagedResponse{T}"/>
    /// 信封（<c>items / total / firstResult / maxResults</c>）；默认保持旧的
    /// <c>{ total, items }</c> 结构，不破坏既有调用方。
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
        bool? envelope,
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
            Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize));

        var page = await store.QueryAsync(query, ct).ConfigureAwait(false);
        var items = new List<AdminAuditItem>(page.Items.Count);

        foreach (var entry in page.Items)
        {
            items.Add(MapAudit(entry));
        }

        return envelope == true
            ? Results.Ok(new PagedResponse<AdminAuditItem>(items, page.Total, query.FirstResult, query.MaxResults))
            : Results.Ok(new AdminAuditListResponse(page.Total, items));
    }

    /// <summary>
    /// 导出审计为 CSV（流式分页写出，最多 10 万行；列：时间 / 操作人 / 鉴权方式 / 动作 /
    /// 目标 / 实例 / 结果 / 失败原因 / 明细）。
    /// </summary>
    private static async Task<IResult> ExportAuditAsync(
        string? actor,
        string? action,
        string? targetType,
        string? targetId,
        string? processInstanceId,
        string? tenantId,
        bool? success,
        DateTime? since,
        DateTime? until,
        RequestContext requestContext,
        IAdminAuditStore store,
        HttpResponse response,
        CancellationToken ct)
    {
        const int pageSize = 500;

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
            0,
            pageSize);

        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition =
            $"attachment; filename=\"audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv\"";

        // BOM：Excel 打开中文不乱码
        await response.WriteAsync("\uFEFF", ct).ConfigureAwait(false);
        await response.WriteAsync(
            "createdAt,actor,authMode,action,targetType,targetId,processInstanceId,success,error,detail\r\n",
            ct).ConfigureAwait(false);

        // 分页拉取后顺序写出，避免把整张审计表读进内存
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await store
                .QueryAsync(query with { FirstResult = offset }, ct)
                .ConfigureAwait(false);

            foreach (var entry in page.Items)
            {
                await response.WriteAsync(ToCsvRow(entry), ct).ConfigureAwait(false);
            }

            if (page.Items.Count < pageSize || offset + page.Items.Count >= page.Total)
            {
                break;
            }
        }

        return Results.Empty;
    }

    private static string ToCsvRow(AdminAuditEntry entry) =>
        string.Join(
            ',',
            entry.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            CsvField(entry.Actor),
            CsvField(entry.AuthMode),
            CsvField(entry.Action),
            CsvField(entry.TargetType),
            CsvField(entry.TargetId),
            CsvField(entry.ProcessInstanceId),
            entry.Success ? "true" : "false",
            CsvField(entry.Error),
            CsvField(entry.Detail)) + "\r\n";

    /// <summary>CSV 字段转义：统一加引号，内部引号翻倍。</summary>
    private static string CsvField(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// 审计筛选项：返回出现过的动作 / 目标类型 / 操作人。
    /// <para>控制台筛选下拉据此生成，不再硬编码动作名；actor 最多 200 个。</para>
    /// </summary>
    private static async Task<IResult> GetAuditOptionsAsync(
        string? tenantId,
        RequestContext requestContext,
        IAdminAuditStore store,
        CancellationToken ct) =>
        Results.Ok(
            await store
                .GetOptionsAsync(ResolveTenant(tenantId, requestContext), ct)
                .ConfigureAwait(false));

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

        var removed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.AuditPurge,
            AdminAuditTargets.Audit,
            tenant,
            failureDetail: () => new { before = cutoff },
            successDetail: removed => new { before = cutoff, removed },
            operation: async token =>
            {
                var count = await store.PurgeAsync(tenant, cutoff, token).ConfigureAwait(false);
                AdminDiagnostics.AuditPurged.Add(count);
                return count;
            },
            ct).ConfigureAwait(false);

        return Results.Ok(new AdminAuditPurgeResponse(removed, cutoff, tenant));
    }

    // ─────────────────────────── 指标 ───────────────────────────

    /// <summary>
    /// 管理指标：运行中 / 历史实例、流程定义、待办分布、管理动作计数、归档量。
    /// <para>
    /// 各统计查询相互独立，这里<b>并行</b>发出以降低总延迟；
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

        var runningTask = engine
            .CountProcessInstancesAsync(new ProcessInstanceQuery(TenantId: tenant), ct).AsTask();
        var definitionsTask = engine
            .CountProcessDefinitionsAsync(new ProcessDefinitionQuery(TenantId: tenant), ct).AsTask();
        var historicTask = engine
            .CountHistoricProcessInstancesAsync(new HistoricProcessInstanceQuery(TenantId: tenant), ct).AsTask();
        var finishedTask = engine
            .CountHistoricProcessInstancesAsync(
                new HistoricProcessInstanceQuery(TenantId: tenant, Finished: true), ct).AsTask();
        var deletedTask = engine
            .CountHistoricProcessInstancesAsync(
                new HistoricProcessInstanceQuery(TenantId: tenant, Deleted: true), ct).AsTask();
        var tasksTask = metricsStore.GetTaskMetricsAsync(tenant, now, offset, ct).AsTask();
        var auditTask = auditStore.SummarizeAsync(tenant, now.AddDays(-window), ct).AsTask();
        var archivesTask = CountArchivesAsync(archiveStore, tenant, ct);

        await Task.WhenAll(
            runningTask, definitionsTask, historicTask, finishedTask, deletedTask,
            tasksTask, auditTask, archivesTask).ConfigureAwait(false);

        var tasks = await tasksTask.ConfigureAwait(false);

        return new AdminMetricsResponse(
            now,
            tenant,
            window,
            await runningTask.ConfigureAwait(false),
            await definitionsTask.ConfigureAwait(false),
            await historicTask.ConfigureAwait(false),
            await finishedTask.ConfigureAwait(false),
            await deletedTask.ConfigureAwait(false),
            tasks?.Pending,
            tasks?.Overdue,
            tasks?.DueToday,
            tasks is null ? null : MergeByProcessKey(tasks.ByProcessDefinitionId),
            await archivesTask.ConfigureAwait(false),
            [.. (await auditTask.ConfigureAwait(false)).Select(item => new AdminAuditActionCount(item.Action, item.Count))]);
    }

    /// <summary>归档总量（内存宿主没有持久化历史时如实返回 <see langword="null"/>）。</summary>
    private static async Task<int?> CountArchivesAsync(
        IHistoryArchiveStore archiveStore,
        string? tenant,
        CancellationToken ct)
    {
        try
        {
            var page = await archiveStore
                .QueryAsync(new HistoryArchiveQuery(TenantId: tenant, MaxResults: 1), ct)
                .ConfigureAwait(false);

            return page.Total;
        }
        catch (EngineNotImplementedException)
        {
            return null;
        }
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

        var result = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.HistoryArchive,
            AdminAuditTargets.History,
            tenant,
            failureDetail: () => new { before = archiveRequest.Before, batchSize = archiveRequest.BatchSize },
            // dry-run 只统计不写库，不产生审计
            successDetail: request.DryRun
                ? null
                : result => new
                {
                    before = archiveRequest.Before,
                    archivedCount = result.ArchivedCount,
                    remainingCount = result.RemainingCount,
                    batchSize = archiveRequest.BatchSize,
                },
            operation: async token => await store.ArchiveAsync(archiveRequest, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return Results.Ok(result);
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
            Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize));

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
}
