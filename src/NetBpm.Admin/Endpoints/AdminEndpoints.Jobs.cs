using Microsoft.AspNetCore.Http;
using NetBpm.Admin.Storage;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Admin;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：边界定时器作业（<c>ACT_RU_TIMER_JOB</c>）的查询、统计与改期。
/// <para>作业的创建 / 触发 / 删除仍由引擎负责；这里只提供运维可观测性与改期。</para>
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 分页查定时作业（按到期时间升序）。
    /// <para>
    /// 过滤：<c>processInstanceId</c> / <c>processDefinitionId</c> / <c>dueBefore</c>；
    /// 分页 <c>firstResult</c> / <c>maxResults</c>（默认 20，上限 200）。
    /// 默认裸数组 + <c>X-Total-Count</c>，<c>envelope=true</c> 返回分页信封。
    /// </para>
    /// </summary>
    private static async Task<IResult> FindJobsAsync(
        string? processInstanceId,
        string? processDefinitionId,
        DateTime? dueBefore,
        bool? deadLetter,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        RequestContext requestContext,
        TimerJobStore store,
        HttpResponse response,
        CancellationToken ct)
    {
        var first = Math.Max(0, firstResult ?? 0);
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var filter = new TimerJobFilter(
            processInstanceId,
            processDefinitionId,
            ResolveTenant(null, requestContext),
            UtcTime.Normalize(dueBefore),
            deadLetter);

        var page = await store.FindAsync(filter, first, size, ct).ConfigureAwait(false);

        return PagedResults.Create(page.Items, page.Total, first, size, response, envelope == true);
    }

    /// <summary>作业统计：总数 / 已到期 / 一小时内到期 / 最近到期时间（受租户与实例过滤影响）。</summary>
    private static async Task<IResult> GetJobStatsAsync(
        string? processInstanceId,
        string? processDefinitionId,
        RequestContext requestContext,
        TimerJobStore store,
        CancellationToken ct)
    {
        var filter = new TimerJobFilter(
            processInstanceId,
            processDefinitionId,
            ResolveTenant(null, requestContext));

        return Results.Ok(await store.GetStatsAsync(filter, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 修改作业到期时间（运维改期）。作业可能已被轮询触发 / 删除，此时 404。
    /// <para>写审计 <c>job.reschedule</c>（成功与失败都留痕）。</para>
    /// </summary>
    private static async Task<IResult> RescheduleJobAsync(
        string jobId,
        RescheduleTimerJobRequest request,
        RequestContext requestContext,
        TimerJobStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var dueDate = UtcTime.Normalize(request.DueDate);

        var updated = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobReschedule,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId, dueDate },
            successDetail: changed => new { jobId, dueDate, changed },
            operation: async token => await store.UpdateDueDateAsync(jobId, dueDate, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return updated
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"定时作业 '{jobId}' 不存在或已被触发。"));
    }

    /// <summary>
    /// 人工重试死信作业：复位重试次数、清除失败原因、立即到期。
    /// <para>只对 <c>DEAD_LETTER_ = 1</c> 的作业生效；普通作业返回 404（避免误把正在退避的作业提前触发）。</para>
    /// </summary>
    private static async Task<IResult> RetryDeadLetterJobAsync(
        string jobId,
        RequestContext requestContext,
        TimerJobStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var retried = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobRetry,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId },
            successDetail: changed => new { jobId, changed },
            operation: async token => await store.RetryDeadLetterAsync(jobId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return retried
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"死信作业 '{jobId}' 不存在（或不是死信状态）。"));
    }

    /// <summary>删除死信作业（确认不再重试）；普通作业不受影响。</summary>
    private static async Task<IResult> DeleteDeadLetterJobAsync(
        string jobId,
        RequestContext requestContext,
        TimerJobStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var deleted = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobDelete,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId },
            successDetail: changed => new { jobId, changed },
            operation: async token => await store.DeleteDeadLetterAsync(jobId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse($"死信作业 '{jobId}' 不存在。"));
    }

    /// <summary>
    /// 立即触发一条定时作业（不必等到期），用于运维手动推进超时分支。
    /// <para>
    /// 复用轮询完全相同的 <see cref="TriggerTimerJobCommand"/>：作业以条件删除抢占，
    /// 多宿主 / 多控制台同时触发只有一个生效，不会重复推进实例。
    /// </para>
    /// <para>
    /// 失败的语义：作业不存在（或不属于当前租户）→ 404；作业还在但无法推进
    /// （实例已挂起、任务已完成、已被别的宿主抢占）→ 409。写审计 <c>job.execute</c>。
    /// </para>
    /// </summary>
    private static async Task<IResult> ExecuteJobAsync(
        string jobId,
        RequestContext requestContext,
        TimerJobStore store,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var tenant = ResolveTenant(null, requestContext);
        var job = await store.FindByIdAsync(jobId, ct).ConfigureAwait(false);

        // 受限身份看不到别的租户的作业：按 404 处理，不泄露存在性
        var visible = job is not null
            && (string.IsNullOrWhiteSpace(tenant)
                || string.Equals(job.TenantId, tenant, StringComparison.Ordinal));

        if (!visible)
        {
            return Results.NotFound(new ErrorResponse($"定时作业 '{jobId}' 不存在。"));
        }

        var processInstanceId = job!.ProcessInstanceId;

        var fired = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobExecute,
            AdminAuditTargets.Job,
            tenant,
            failureDetail: () => new { jobId, processInstanceId },
            // 没推进成功（挂起 / 已完成 / 被抢占）不算一次成功的运维动作，不写成功审计
            successDetail: result => result ? new { jobId, processInstanceId, fired = true } : null,
            operation: token => engine
                .ExecuteAsync(new TriggerTimerJobCommand(jobId), token)
                .AsTask(),
            ct).ConfigureAwait(false);

        return fired
            ? Results.NoContent()
            : Results.Conflict(new ErrorResponse(
                $"定时作业 '{jobId}' 未触发：实例已挂起、任务已完成或作业已被其它宿主抢占。"));
    }
}
