using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

// 引擎面（NetBpm.Rest.Contracts）也有一个 ClaimTaskRequest：命名空间嵌套下它会先被解析到，
// 这里显式用平台契约的那一个。
using PlatformClaimRequest = NetBpm.Platform.Contracts.ClaimTaskRequest;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 待办归属调整：认领 / 取消认领 / 转办（<c>POST /workflow/v1/tasks/{id}/{claim|unclaim|transfer}</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与引擎面 <c>/api/runtime/tasks/{id}/assignee</c> 的分工：那里是原语（给个 id 就改），
/// 这里要回答三个业务问题 —— <b>谁有资格拿这条待办</b>、<b>已被别人拿走怎么办</b>、
/// <b>事后怎么解释这次归属变化</b>。三个动作都落时间线（<c>TASK_CLAIMED</c> /
/// <c>TASK_UNCLAIMED</c> / <c>TASK_TRANSFERRED</c>），否则「这条待办为什么换了人」
/// 在审批记录里查无对证。
/// </para>
/// <para>
/// 归属不满足与待办不存在<b>同口径 404</b>：认领接口如果回答 403，就等于告诉调用方
/// 「这条待办存在，只是不归你」，而待办的存在性本身就是不该泄露的信息。
/// 只有「已被别人认领」用 409 —— 那是调用方<b>已经看得到</b>这条待办时的合理回答。
/// </para>
/// </remarks>
public interface ITaskAssignment
{
    /// <summary>认领待办：把办理人设为当前主体（待办必须尚未被认领，且当前主体在候选范围内）。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">认领请求（<c>ExpectedAssignee</c> 用于「抢单」的乐观并发）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">不存在 / 无主体 / 已被认领 / 不在候选范围。</exception>
    Task<TaskView> ClaimAsync(
        string taskId,
        PlatformClaimRequest? request,
        RequestContext context,
        CancellationToken ct = default);

    /// <summary>取消认领：清空办理人，待办回到候选池。</summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">不存在 / 无主体 / 不是当前办理人。</exception>
    Task<TaskView> UnclaimAsync(string taskId, RequestContext context, CancellationToken ct = default);

    /// <summary>转办：把待办交给指定的人（可覆盖现有办理人）。</summary>
    /// <remarks>
    /// <para>
    /// 门槛是<b>三选一</b>：管理视角、当前办理人本人、或在候选范围内（候选人命中，
    /// 或候选项中的组与令牌里的组声明有交集）。它与 <see cref="ClaimAsync"/> 的差别在于
    /// <b>不</b>要求「待办尚未被认领」：认领是「没人拿我才拿」，转办是「协调改派」。
    /// </para>
    /// <para>
    /// 因此候选范围内的非办理人<b>可以</b>把已认领给别人的待办改派出去，这是有意保留的语义：
    /// 模板把候选人定义成一个协作组，组内换人不必先 unclaim 再 claim，也就不会在时间线上
    /// 留下一条「某人放弃过」的假记录（<see cref="UnclaimAsync"/> 明确只允许本人操作，
    /// 正是为了让「谁放弃了这单」保持真实）。要限制这种覆盖，应当收紧候选范围本身，
    /// 而不是加一条「仅未认领可转」的分支 —— 那会让管理员在待办被认领后反而失去改派能力。
    /// </para>
    /// <para>
    /// 越权统一回答 404 而不是 403：调用方既不是办理人也不在候选范围时，说明它本就不该
    /// 看见这条待办，回答 403 等于确认了待办的存在。
    /// </para>
    /// <para>
    /// 转办<b>不改认领时间</b>（<c>CLAIM_TIME_</c>）：被转办的人没有主动认领过，
    /// 把时间刷成现在等于伪造一次认领动作。
    /// </para>
    /// </remarks>
    /// <param name="taskId">任务 id。</param>
    /// <param name="request">转办请求（新办理人 + 原因）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">不存在 / 无主体 / 缺新办理人 / 无权转办。</exception>
    Task<TaskView> TransferAsync(
        string taskId,
        TransferTaskRequest request,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class TaskAssignment(
    ITaskCatalog catalog,
    IApprovalTimeline timeline,
    IProcessEngine engine) : ITaskAssignment
{
    /// <inheritdoc />
    public async Task<TaskView> ClaimAsync(
        string taskId,
        PlatformClaimRequest? request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(context);

        var subject = RequireSubject(context);

        var view = await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? throw NotFound(taskId);

        // 抢单：调用方带着「我以为现在是没人认领 / 是某人认领」的预期来，
        // 与服务端实际不一致就 409，而不是静默成功——否则两个人会各自以为自己拿到了同一条待办。
        if (!string.IsNullOrEmpty(request?.ExpectedAssignee)
            && !string.Equals(view.Assignee, request.ExpectedAssignee, StringComparison.Ordinal))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                $"待办 '{taskId}' 的当前办理人是 '{view.Assignee ?? "（无人认领）"}'，与期望的 '{request.ExpectedAssignee}' 不一致。");
        }

        if (string.Equals(view.Assignee, subject, StringComparison.Ordinal))
        {
            // 已经是我认领的：重复认领不报错、也不重复写时间线（幂等）
            return view;
        }

        if (!string.IsNullOrEmpty(view.Assignee))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.TaskAlreadyClaimed,
                "待办已被认领",
                StatusCodes.Status409Conflict,
                $"待办 '{taskId}' 已被 '{view.Assignee}' 认领；如需改派请调用 transfer。");
        }

        // 候选范围：认领不是「谁都能接」，只有被指派到这个环节的人（或其所在组）能接
        if (!CanTake(view, subject, context))
        {
            throw NotFound(taskId);
        }

        try
        {
            await engine.ClaimTaskAsync(taskId, subject, ct).ConfigureAwait(false);
        }
        catch (EngineConflictException)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.TaskAlreadyClaimed,
                "待办已被认领",
                StatusCodes.Status409Conflict,
                $"待办 '{taskId}' 刚被他人认领。");
        }
        catch (EngineObjectNotFoundException)
        {
            throw NotFound(taskId);
        }
        catch (OptimisticLockingException exception)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                exception.Message);
        }

        await AppendAsync(view, TimelineEntryKind.TaskClaimed, subject, null, context, ct)
            .ConfigureAwait(false);

        return await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? view with { Assignee = subject };
    }

    /// <inheritdoc />
    public async Task<TaskView> UnclaimAsync(
        string taskId,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(context);

        var subject = RequireSubject(context);

        var view = await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? throw NotFound(taskId);

        if (string.IsNullOrEmpty(view.Assignee))
        {
            // 本来就没认领：幂等（取消认领没有副作用可产生）
            return view;
        }

        // 只有办理人本人能放弃；管理员要改别人的归属走 transfer
        if (!PlatformIdentity.HasAdminPerspective(context)
            && !string.Equals(view.Assignee, subject, StringComparison.Ordinal))
        {
            throw NotFound(taskId);
        }

        try
        {
            await engine.UnclaimTaskAsync(taskId, null, ct).ConfigureAwait(false);
        }
        catch (EngineObjectNotFoundException)
        {
            throw NotFound(taskId);
        }
        catch (OptimisticLockingException exception)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                exception.Message);
        }

        await AppendAsync(view, TimelineEntryKind.TaskUnclaimed, subject, null, context, ct)
            .ConfigureAwait(false);

        return await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? view with { Assignee = null };
    }

    /// <inheritdoc />
    public async Task<TaskView> TransferAsync(
        string taskId,
        TransferTaskRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var subject = RequireSubject(context);

        var view = await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? throw NotFound(taskId);

        var target = request.Assignee?.Trim();

        if (string.IsNullOrEmpty(target))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ParameterRequiredMissing,
                "缺少新办理人",
                StatusCodes.Status422UnprocessableEntity,
                "转办必须指定 assignee；要把待办退回候选池请调用 unclaim。");
        }

        if (string.Equals(view.Assignee, target, StringComparison.Ordinal))
        {
            return view;
        }

        // 门槛三选一：管理视角、当前办理人本人、或在候选范围内。
        // 与 claim 的差异只有一条 —— 这里不校验 view.Assignee 是否为空，所以已认领给他人的
        // 待办也能被候选人改派（协作组的协调语义，详见该方法的 remarks）；不满足任一条则 404。
        if (!PlatformIdentity.HasAdminPerspective(context)
            && !string.Equals(view.Assignee, subject, StringComparison.Ordinal)
            && !CanTake(view, subject, context))
        {
            throw NotFound(taskId);
        }

        try
        {
            await engine.SetAssigneeAsync(taskId, target, ct).ConfigureAwait(false);
        }
        catch (EngineObjectNotFoundException)
        {
            throw NotFound(taskId);
        }
        catch (OptimisticLockingException exception)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                exception.Message);
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment)
            ? $"转办给 {target}"
            : $"转办给 {target}：{request.Comment.Trim()}";

        await AppendAsync(view, TimelineEntryKind.TaskTransferred, subject, comment, context, ct)
            .ConfigureAwait(false);

        return await catalog.FindAsync(taskId, context, ct).ConfigureAwait(false)
            ?? view with { Assignee = target };
    }

    /// <summary>真人主体：归属变化必须能追到一个人，纯服务令牌不得调整归属。</summary>
    private static string RequireSubject(RequestContext context) =>
        string.IsNullOrWhiteSpace(context.SubjectId)
            ? throw new PlatformActionRejectedException(
                ErrorCodes.AuthSubjectRequired,
                "缺少操作主体",
                StatusCodes.Status403Forbidden,
                "认领 / 取消认领 / 转办必须落到真人主体；纯服务令牌请改用代客调用（X-Subject-Id）。")
            : context.SubjectId!;

    /// <summary>当前主体是否在这条待办的候选范围内（候选人命中，或候选组与令牌里的组声明有交集）。</summary>
    private static bool CanTake(TaskView view, string subject, RequestContext context) =>
        view.CandidateUsers.Contains(subject, StringComparer.Ordinal)
        || (context.Groups.Count > 0
            && view.CandidateGroups.Any(group => context.Groups.Contains(group, StringComparer.Ordinal)));

    private async ValueTask AppendAsync(
        TaskView view,
        TimelineEntryKind kind,
        string subject,
        string? comment,
        RequestContext context,
        CancellationToken ct) =>
        _ = await timeline.AppendAsync(
            view.InstanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: view.InstanceId,
                Kind: kind,
                ActivityId: view.ActivityId,
                ActivityName: view.ActivityName,
                TaskId: view.TaskId,
                ActorId: subject,
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: comment,
                TargetNodeId: null,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct);

    private static PlatformActionRejectedException NotFound(string taskId) =>
        new(
            ErrorCodes.TaskNotFound,
            "待办不存在",
            StatusCodes.Status404NotFound,
            $"待办 '{taskId}' 不存在、已完成，或当前调用方无权调整其归属。");
}

/// <summary><see cref="ITaskAssignment"/> 的服务注册。</summary>
public static class TaskAssignmentServiceExtensions
{
    /// <summary>注册待办归属调整服务。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmTaskAssignment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITaskAssignment, TaskAssignment>();

        return services;
    }
}
