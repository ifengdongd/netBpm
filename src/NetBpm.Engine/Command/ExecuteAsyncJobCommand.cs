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
    private readonly string _jobId;

    public ExecuteAsyncJobCommand(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        _jobId = jobId;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var jobs = services.AsyncJobManager
            ?? throw new EngineException("异步作业存储未注册（IAsyncJobManager）。");

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
            await jobs.RescheduleAsync(_jobId, DateTime.UtcNow.AddMinutes(1), ct).ConfigureAwait(false);
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

        // 抢占：删不到说明别的宿主已经处理
        if (!await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false))
        {
            return false;
        }

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
