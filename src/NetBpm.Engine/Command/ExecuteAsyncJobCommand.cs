using NetBpm.Bpmn.Model;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Operations;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 执行一条异步作业（<c>asyncBefore</c> / <c>asyncAfter</c>）。
/// <para>
/// 与定时作业同构：先做有效性校验，再以「条件删除」抢占作业（多宿主同时轮询只有一个能执行），
/// 最后在<b>本命令事务</b>里推进执行。失败时命令回滚，作业仍在表里，由调用方记录失败与退避。
/// </para>
/// </summary>
public sealed class ExecuteAsyncJobCommand : ICommand<bool>
{
    /// <summary>
    /// 单次拾取的租约时长。
    /// <para>
    /// 与定时作业取同一个量级（5min）：应远大于任何单条作业的合理执行时长，
    /// 避免正常长作业被误判为「宿主已崩溃」而被并发接管。
    /// </para>
    /// </summary>
    private static readonly TimeSpan JobLease = TimeSpan.FromMinutes(5);

    private readonly string _jobId;
    private readonly string? _owner;
    private readonly bool _leaseAlreadyHeld;

    /// <param name="jobId">作业 id。</param>
    /// <param name="owner">
    /// 持有者标识（宿主实例 id）。传入时先按租约抢占（无人持有 / 租约已过期才成功），
    /// 执行成功后删除作业行 —— 宿主崩溃后作业会在租约到期时自动回收，不会永久丢失。
    /// 为 <see langword="null"/> 时退化为旧的「条件删除抢占」（不推荐）。
    /// </param>
    /// <param name="leaseAlreadyHeld">
    /// 调用方<b>已经</b>用同一个 <paramref name="owner"/> 抢到租约（例如轮询器先批量抢占、
    /// 再逐条交给命令）。为 <see langword="true"/> 时命令内不再重复抢占 ——
    /// 那条 <c>update ... where LOCK_OWNER_ = @owner</c> 除了多一次往返什么也不做。
    /// </param>
    public ExecuteAsyncJobCommand(string jobId, string? owner = null, bool leaseAlreadyHeld = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        _jobId = jobId;
        _owner = owner;
        _leaseAlreadyHeld = leaseAlreadyHeld;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var jobs = services.AsyncJobManager
            ?? throw new EngineException("异步作业存储未注册（IAsyncJobManager）。");

        // 租约式抢占：执行前只标记归属，成功后才删除。
        // 与旧的「先删后执行」相比，宿主崩溃不会丢作业。
        // 调用方已持有同一 owner 的租约时跳过（抢占只做一次，见 leaseAlreadyHeld）。
        if (!_leaseAlreadyHeld
            && !string.IsNullOrWhiteSpace(_owner)
            && !await jobs.TryAcquireAsync(_jobId, _owner!, services.Clock.UtcNow, JobLease, ct).ConfigureAwait(false))
        {
            return false;
        }

        var job = await jobs.FindByIdAsync(_jobId, ct).ConfigureAwait(false);

        if (job is null || job.DeadLetter)
        {
            return false;
        }

        var execution = await services.ExecutionManager
            .FindByIdAsync(job.ExecutionId, ct)
            .ConfigureAwait(false);

        // 实例 / 执行已结束或被删除：作业作废（不报错，避免死信里堆无意义记录）
        if (execution is null || execution.IsEnded)
        {
            await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);
            return false;
        }

        if (execution.SuspensionState != 1)
        {
            // 挂起不是删除：顺延一分钟，避免占满批次头部
            await jobs.RescheduleAsync(_jobId, services.Clock.UtcNow.AddMinutes(1), ct).ConfigureAwait(false);
            return false;
        }

        // 执行已经不在作业指向的节点上（例如被迁移 / 人工干预 / 另一条并行令牌推进）：
        // 这是陈旧作业，删除即可；继续执行会重复推进业务。
        if (!string.Equals(execution.CurrentActivityId, job.ActivityId, StringComparison.Ordinal))
        {
            await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);
            return false;
        }

        var element = services.ProcessDefinitionResolver is { } resolver
            ? await resolver.ResolveFlowElementAsync(job.ProcessDefinitionId, job.ActivityId, ct)
                .ConfigureAwait(false)
            : null;

        if (element is not Activity activity)
        {
            await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);
            return false;
        }

        // 租约式抢占的收口：代码走到这里说明业务已执行成功，此时才真正删除作业行
        await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);

        execution.CurrentFlowElement = activity;

        execution.CurrentActivityId = activity.Id;
        execution.IsActive = true;

        await services.ExecutionManager.UpdateAsync(execution, ct).ConfigureAwait(false);

        switch (job.Phase)
        {
            case AsyncJobPhase.Before:
                // 已是作业推进：跳过 asyncBefore 检查，避免作业自我续命
                await new ContinueProcessOperation(execution, services, skipAsyncBefore: true)
                    .RunAsync(ct)
                    .ConfigureAwait(false);
                break;

            case AsyncJobPhase.After:
                // 活动行为已执行完：只补「结束 + 离开节点」，不再重跑行为
                await new TakeOutgoingSequenceFlowsOperation(
                        execution,
                        services,
                        evaluateConditions: true,
                        skipAsyncAfter: true)
                    .RunAsync(ct)
                    .ConfigureAwait(false);
                break;

            default:
                throw new EngineException($"未知的异步作业阶段：{job.Phase}。");
        }

        return true;
    }
}
