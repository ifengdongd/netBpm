using Microsoft.AspNetCore.Http;
using NetBpm.Engine.Events;
using NetBpm.Engine.Jobs;
using NetBpm.Persistence.Admin;
using NetBpm.Rest;
using NetBpm.Rest.Auditing;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：异步作业（<c>ACT_RU_JOB</c>）与事务性 outbox（<c>ACT_EVT_OUTBOX</c>）。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>异步作业摘要（诊断视图）。</summary>
    public sealed record AsyncJobSummary(
        string Id,
        string ProcessInstanceId,
        string? ProcessDefinitionId,
        string ExecutionId,
        string ActivityId,
        string Phase,
        DateTime DueDate,
        string? TenantId,
        DateTime CreateTime,
        int Retries,
        bool DeadLetter,
        string? ExceptionMessage,
        DateTime? LastFailureTime)
    {
        public static AsyncJobSummary From(AsyncJobEntity job) => new(
            job.Id,
            job.ProcessInstanceId,
            job.ProcessDefinitionId,
            job.ExecutionId,
            job.ActivityId,
            job.PhaseValue,
            job.DueDate,
            string.IsNullOrEmpty(job.TenantId) ? null : job.TenantId,
            job.CreateTime,
            job.Retries,
            job.DeadLetter,
            job.ExceptionMessage,
            job.LastFailureTime);
    }

    /// <summary>outbox 事件摘要（诊断视图）。</summary>
    public sealed record OutboxEventSummary(
        string Id,
        string EventType,
        string? EntityId,
        string? ProcessInstanceId,
        string? ProcessDefinitionId,
        string? ActivityId,
        string? TenantId,
        DateTime CreateTime,
        bool Published,
        int PublishAttempts,
        bool DeadLetter,
        string? LastError,
        DateTime? NextAttemptTime,
        DateTime? PublishedTime)
    {
        public static OutboxEventSummary From(OutboxEvent item) => new(
            item.Id,
            item.EventType,
            item.EntityId,
            item.ProcessInstanceId,
            item.ProcessDefinitionId,
            item.ActivityId,
            string.IsNullOrEmpty(item.TenantId) ? null : item.TenantId,
            item.CreateTime,
            item.Published,
            item.PublishAttempts,
            item.DeadLetter,
            item.LastError,
            item.NextAttemptTime,
            item.PublishedTime);
    }

    /// <summary>
    /// 分页查异步作业。过滤：<c>processInstanceId</c> / <c>deadLetter</c>；默认裸数组 + <c>X-Total-Count</c>。
    /// </summary>
    private static async Task<IResult> FindAsyncJobsAsync(
        string? processInstanceId,
        bool? deadLetter,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        RequestContext requestContext,
        IProcessEngine engine,
        HttpResponse response,
        CancellationToken ct)
    {
        var first = Math.Max(0, firstResult ?? 0);
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var query = new AsyncJobQuery(
            processInstanceId,
            deadLetter,
            first,
            size,
            ResolveTenant(null, requestContext));

        var items = await engine.FindAsyncJobsAsync(query, ct).ConfigureAwait(false);
        var total = await engine.CountAsyncJobsAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            items.Select(AsyncJobSummary.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope == true);
    }

    /// <summary>立即执行一条异步作业（运维手动推进）。</summary>
    private static async Task<IResult> ExecuteAsyncJobAsync(
        string jobId,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var executed = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobExecute,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId, kind = "async" },
            successDetail: changed => new { jobId, kind = "async", changed },
            operation: async token => await engine.ExecuteAsyncJobAsync(jobId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return executed
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse(
                $"异步作业 '{jobId}' 不存在、已被处理或执行条件不满足。",
                Code: "async_job_not_executed"));
    }

    /// <summary>人工重试异步死信作业。</summary>
    private static async Task<IResult> RetryAsyncJobAsync(
        string jobId,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var retried = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobRetry,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId, kind = "async" },
            successDetail: changed => new { jobId, kind = "async", changed },
            operation: async token => await engine.RetryAsyncJobAsync(jobId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return retried
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse(
                $"异步死信作业 '{jobId}' 不存在（或不是死信状态）。",
                Code: "async_job_not_found"));
    }

    /// <summary>删除异步死信作业。</summary>
    private static async Task<IResult> DeleteAsyncJobAsync(
        string jobId,
        RequestContext requestContext,
        IProcessEngine engine,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var deleted = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.JobDelete,
            AdminAuditTargets.Job,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { jobId, kind = "async" },
            successDetail: changed => new { jobId, kind = "async", changed },
            operation: async token => await engine.DeleteAsyncJobAsync(jobId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse(
                $"异步死信作业 '{jobId}' 不存在。",
                Code: "async_job_not_found"));
    }

    /// <summary>
    /// 分页查 outbox 事件。过滤：<c>processInstanceId</c> / <c>published</c> / <c>deadLetter</c>。
    /// </summary>
    private static async Task<IResult> FindOutboxEventsAsync(
        string? processInstanceId,
        bool? published,
        bool? deadLetter,
        int? firstResult,
        int? maxResults,
        bool? envelope,
        RequestContext requestContext,
        IEventOutboxStore store,
        HttpResponse response,
        CancellationToken ct)
    {
        var first = Math.Max(0, firstResult ?? 0);
        var size = Math.Clamp(maxResults ?? DefaultPageSize, 1, MaxPageSize);

        var query = new OutboxQuery(
            processInstanceId,
            published,
            deadLetter,
            first,
            size,
            ResolveTenant(null, requestContext));

        var items = await store.FindAsync(query, ct).ConfigureAwait(false);
        var total = await store.CountAsync(query, ct).ConfigureAwait(false);

        return PagedResults.Create(
            items.Select(OutboxEventSummary.From).ToArray(),
            total,
            first,
            size,
            response,
            envelope == true);
    }

    /// <summary>人工重试 outbox 死信事件。</summary>
    private static async Task<IResult> RetryOutboxEventAsync(
        string eventId,
        RequestContext requestContext,
        IEventOutboxStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var retried = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.OutboxRetry,
            AdminAuditTargets.Outbox,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { eventId },
            successDetail: changed => new { eventId, changed },
            operation: async token => await store.RetryAsync(eventId, DateTime.UtcNow, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return retried
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse(
                $"outbox 死信事件 '{eventId}' 不存在（或不是死信状态）。",
                Code: "outbox_event_not_found"));
    }

    /// <summary>删除 outbox 事件（确认不再投递）。</summary>
    private static async Task<IResult> DeleteOutboxEventAsync(
        string eventId,
        RequestContext requestContext,
        IEventOutboxStore store,
        AdminActionAuditor auditor,
        CancellationToken ct)
    {
        var deleted = await WithAuditTrailAsync(
            requestContext,
            auditor,
            AdminAuditActions.OutboxDelete,
            AdminAuditTargets.Outbox,
            ResolveTenant(null, requestContext),
            failureDetail: () => new { eventId },
            successDetail: changed => new { eventId, changed },
            operation: async token => await store.DeleteAsync(eventId, token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(new ErrorResponse(
                $"outbox 事件 '{eventId}' 不存在。",
                Code: "outbox_event_not_found"));
    }
}
