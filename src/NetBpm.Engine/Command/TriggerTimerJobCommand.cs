using NetBpm.Bpmn.Model;
using NetBpm.Engine.Jobs;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Command;

/// <summary>
/// 触发一条到期的定时作业（边界定时器）。
/// <para>
/// 对应 Activiti 的 <c>TriggerTimerEventJobHandler</c> + <c>TimerEventHandler</c> 的裁剪版：
/// <b>中断型</b>边界事件到点后取消挂靠的用户任务、收口其历史活动行，然后沿边界事件的出流继续。
/// </para>
/// <para>
/// <b>并发语义</b>：作业以「条件删除」抢占（<see cref="ITimerJobManager.TryDeleteAsync"/>），
/// 多宿主同时轮询时只有一个能真正执行；任务若已被完成，作业直接作废。
/// </para>
/// </summary>
public sealed class TriggerTimerJobCommand : ICommand<bool>
{
    /// <summary>边界定时器取消任务时写入的删除原因（<c>ACT_HI_TASKINST.DELETE_REASON_</c>）。</summary>
    public const string DeleteReason = "boundary timer";

    private readonly string _jobId;

    /// <param name="jobId">到期作业 id（<c>ACT_RU_TIMER_JOB.ID_</c>）。</param>
    public TriggerTimerJobCommand(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        _jobId = jobId;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var jobs = services.TimerJobManager
            ?? throw new EngineException("定时作业存储未注入，无法触发边界定时器。");

        var taskManager = services.TaskManager
            ?? throw new EngineException("任务管理器未注入，无法触发边界定时器。");

        // 1. 读作业（先不删：挂起的实例要保留作业，激活后还能触发）
        var job = await jobs.FindByIdAsync(_jobId, ct).ConfigureAwait(false);

        if (job is null)
        {
            return false;
        }

        var execution = await services.ExecutionManager
            .FindByIdAsync(job.ExecutionId, ct)
            .ConfigureAwait(false);

        if (execution is null || execution.IsEnded)
        {
            await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);
            return false;
        }

        if (execution.SuspensionState != 1)
        {
            // 挂起不是删除：保留作业，等实例激活后由下一轮轮询触发
            return false;
        }

        // 2. 任务已被完成（或被人为删除）→ 作业作废
        var tasks = await taskManager.FindTasksByExecutionIdAsync(execution.Id, ct).ConfigureAwait(false);

        if (tasks.Count == 0)
        {
            await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false);
            return false;
        }

        // 3. 抢占作业：删不到说明别的宿主已经处理
        if (!await jobs.TryDeleteAsync(_jobId, ct).ConfigureAwait(false))
        {
            return false;
        }

        // 4. 解析边界事件（部署期已保证存在）
        var process = services.ProcessDefinitionResolver is { } resolver
            ? await resolver.ResolveProcessModelAsync(execution.ProcessDefinitionId, ct).ConfigureAwait(false)
            : null;

        var boundaryEvent = process?
            .FindFlowElementsOfType<BoundaryEvent>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Id, job.BoundaryEventId, StringComparison.Ordinal));

        if (boundaryEvent is null)
        {
            throw new EngineException(
                $"定时作业 '{job.Id}' 指向的边界事件 '{job.BoundaryEventId}' 在流程定义 " +
                $"'{execution.ProcessDefinitionId}' 中不存在。");
        }

        // 5. 中断挂靠活动：先收口「用户任务」的历史活动行（否则 ACT_HI_ACTINST 永远没有结束时间），
        //    再删任务（任务历史写入 DELETE_REASON_），最后把执行挪到边界事件上继续
        await services.HistoryManager
            .RecordActivityEndQuietlyAsync(execution, DeleteReason, ct)
            .ConfigureAwait(false);

        foreach (var task in tasks)
        {
            await taskManager.DeleteTaskAsync(task, DeleteReason, ct).ConfigureAwait(false);
        }

        execution.CurrentFlowElement = boundaryEvent;
        execution.CurrentActivityId = boundaryEvent.Id;
        execution.IsActive = true;

        await services.ExecutionManager.UpdateAsync(execution, ct).ConfigureAwait(false);

        services.Agenda.PlanContinueProcessOperation(execution);

        return true;
    }
}
