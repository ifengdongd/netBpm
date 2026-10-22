using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Operations;

/// <summary>
/// 异步作业落库：在<b>当前命令事务</b>里插入一条到期的 <c>ACT_RU_JOB</c>。
/// <para>
/// 命令提交后 JobExecutor 才能看到它；命令回滚则作业一起回滚，
/// 因此不会出现「活动推进了、作业没落库」或反过来。
/// </para>
/// </summary>
internal static class AsyncJobScheduler
{
    /// <summary>为执行上的活动落一条异步作业。</summary>
    /// <exception cref="EngineException">宿主未注册 <see cref="IAsyncJobManager"/>。</exception>
    public static async ValueTask CreateAsync(
        IEngineServices services,
        IExecutionEntity execution,
        Activity activity,
        AsyncJobPhase phase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(activity);

        var manager = services.AsyncJobManager
            ?? throw new EngineException(
                $"活动 '{activity.Id}' 配置了 {(phase == AsyncJobPhase.Before ? "asyncBefore" : "asyncAfter")}，" +
                "但宿主未注册 IAsyncJobManager（见 AddNetBpmAsyncJobExecutor）。");

        var now = DateTime.UtcNow;
        var jobId = services.IdGenerator.NextId();

        await manager.InsertAsync(
            new AsyncJobEntity
            {
                Id = jobId,
                Revision = 1,
                ExecutionId = execution.Id,
                ProcessInstanceId = execution.ProcessInstanceId ?? string.Empty,
                ProcessDefinitionId = execution.ProcessDefinitionId,
                ActivityId = activity.Id ?? string.Empty,
                Phase = phase,
                DueDate = now,
                TenantId = execution.TenantId,
                CreateTime = now,
                Retries = AsyncJobRetry.DefaultMaxRetries,
            },
            ct).ConfigureAwait(false);

        // 队列只是「到期提示」通道：丢提示最多退化成定期扫库，不会丢作业。
        // 这里在事务提交后才投递，避免回滚的作业把提示留在队列里。
        var context = Command.CommandContext.Current;
        var queue = services.JobQueue;

        if (queue is null)
        {
            return;
        }

        var item = new JobQueueItem(jobId, now);

        if (context is null)
        {
            await queue.EnqueueAsync(item, ct).ConfigureAwait(false);
            return;
        }

        context.OnCommitted(() => queue.EnqueueAsync(item, CancellationToken.None));
    }
}
