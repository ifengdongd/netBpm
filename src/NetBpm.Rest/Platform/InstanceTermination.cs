using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;
using NetBpm.Runtime;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 终止流程实例（<c>POST /workflow/v1/instances/{id}/terminate</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么业务面要有自己的终止端点，而不是复用引擎面的「删除实例」：
/// 引擎面的删除只在管理动作审计里留一行，<b>实例自己的审批记录里什么都不会有</b>——
/// 于是一张单的待办凭空消失，审计却问不出是谁让它消失的。
/// </para>
/// <para>
/// 两个硬约束，都是为了让「终止」这件事在事后可解释：
/// <list type="bullet">
///   <item><description><b>原因必填</b>：没有原因的终止在审计里等于「不知为何」，一律 422；</description></item>
///   <item><description><b>一定落时间线</b>（<c>INSTANCE_TERMINATED</c>）：原因直接写进 <c>Comment</c>。</description></item>
/// </list>
/// </para>
/// </remarks>
public interface IInstanceTermination
{
    /// <summary>终止实例，返回终止后的实例视图。</summary>
    /// <param name="instanceId">实例 id。</param>
    /// <param name="request">终止请求（原因 + 可选乐观并发修订号）。</param>
    /// <param name="context">调用方身份。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="PlatformActionRejectedException">不存在 / 已结束 / 无权 / 并发冲突 / 缺原因。</exception>
    Task<InstanceView> TerminateAsync(
        string instanceId,
        TerminateInstanceRequest request,
        RequestContext context,
        CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class InstanceTermination(
    IInstanceCatalog catalog,
    IApprovalTimeline timeline,
    IProcessEngine engine) : IInstanceTermination
{
    /// <inheritdoc />
    public async Task<InstanceView> TerminateAsync(
        string instanceId,
        TerminateInstanceRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var view = await catalog.FindAsync(instanceId, context, ct).ConfigureAwait(false);

        // 不存在 / 跨租户 / 对调用方不可见 → 一律 404（不泄露存在性）
        if (view is null)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.InstanceNotFound,
                "实例不存在",
                StatusCodes.Status404NotFound,
                $"流程实例 '{instanceId}' 不存在，或当前调用方无权访问。");
        }

        if (view.Status != InstanceStatus.Running)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.InstanceNotRunning,
                "实例已结束",
                StatusCodes.Status409Conflict,
                $"流程实例 '{instanceId}' 当前状态为 {view.Status}，只有运行中的实例可以终止。");
        }

        // 数据级授权：终止等于替所有待办人做决定，所以不能只凭 scope。
        // 发起人可以撤回自己的单；除此之外必须有管理视角。
        if (!PlatformIdentity.HasAdminPerspective(context)
            && !string.Equals(view.StartedBy, context.SubjectId, StringComparison.Ordinal))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.InstanceNotFound,
                "实例不存在",
                StatusCodes.Status404NotFound,
                "只有发起人本人或持管理视角的调用方可以终止该实例。");
        }

        if (request.ExpectedRevision is { } expected && expected != view.Revision)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                $"流程实例 '{instanceId}' 已被推进（期望修订号 {expected}，实际 {view.Revision}），请刷新后重试。");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ParameterRequiredMissing,
                "缺少终止原因",
                StatusCodes.Status422UnprocessableEntity,
                "终止实例必须填写原因：它会原样进审批记录，事后要能解释这张单为什么没走完。");
        }

        var reason = request.Reason.Trim();

        try
        {
            await engine.DeleteProcessInstanceAsync(instanceId, reason, ct).ConfigureAwait(false);
        }
        catch (EngineObjectNotFoundException)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.InstanceNotFound,
                "实例不存在",
                StatusCodes.Status404NotFound,
                $"流程实例 '{instanceId}' 已不在运行中（可能刚被办结或已终止）。");
        }
        catch (OptimisticLockingException exception)
        {
            throw new PlatformActionRejectedException(
                ErrorCodes.ConcurrencyRevisionMismatch,
                "并发冲突",
                StatusCodes.Status409Conflict,
                exception.Message);
        }

        // 时间线：终止原因就是审计要的那句话，直接落进 Comment
        _ = await timeline.AppendAsync(
            instanceId,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: instanceId,
                Kind: TimelineEntryKind.InstanceTerminated,
                ActivityId: null,
                ActivityName: null,
                TaskId: null,
                ActorId: context.SubjectId,
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: reason,
                TargetNodeId: null,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            context,
            ct).ConfigureAwait(false);

        // 终止后实例已不在运行态：内存宿主（EnableHistory=false）查不到已结束实例，
        // 这时回退到「终止前的视图 + 终止态」，保证调用方拿到的仍是同一个 InstanceView 形状。
        return await catalog.FindAsync(instanceId, context, ct).ConfigureAwait(false)
            ?? view with
            {
                Status = InstanceStatus.Terminated,
                EndedAt = DateTimeOffset.UtcNow,
                Tasks = [],
                CurrentActivityIds = [],
            };
    }
}

/// <summary><see cref="IInstanceTermination"/> 的服务注册。</summary>
public static class InstanceTerminationServiceExtensions
{
    /// <summary>注册终止实例服务。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddNetBpmInstanceTermination(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IInstanceTermination, InstanceTermination>();

        return services;
    }
}
