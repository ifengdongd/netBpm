using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 改派任务（转办）：把待办任务的办理人直接改为指定用户。对应 Activiti 的
/// <c>SetAssigneeCmd</c> / <c>taskService.setAssignee()</c>。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ClaimTaskCommand"/>（认领）的差异：
/// </para>
/// <list type="bullet">
///   <item>不校验「是否已被他人认领」——改派的管理动作可以覆盖任何办理人（含空）。</item>
///   <item>不动 <c>CLAIM_TIME_</c> —— 认领时间是「认领动作」的痕迹，改派不改它。</item>
///   <item>
///   底层走 <see cref="ITaskManager.ChangeTaskAssigneeAsync"/>：改 <c>ASSIGNEE_</c>、
///   按需补流程实例级 <c>'participant'</c> 身份关系，并回写任务历史。
///   </item>
/// </list>
/// <para>
/// <paramref name="assignee"/> 传空（<see langword="null"/> / 空白）表示把任务退回未分配状态
/// （候选组/候选人重新可见）。
/// </para>
/// </remarks>
public sealed class SetAssigneeCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string? _assignee;

    /// <param name="taskId">任务 id。</param>
    /// <param name="assignee">新办理人；空表示退回未分配。</param>
    public SetAssigneeCommand(string taskId, string? assignee)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var taskManager = services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "P7",
                "宿主必须注册任务管理器实现");

        var task = await taskManager.FindTaskByIdAsync(_taskId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"任务 '{_taskId}' 不存在（可能已完成或已被删除）。");

        if (task.SuspensionState != TaskEntity.ActiveSuspensionState)
        {
            throw new EngineException($"任务 '{_taskId}' 处于挂起状态，不能改派。");
        }

        if (string.Equals(task.Assignee, _assignee, StringComparison.Ordinal))
        {
            // 后置条件已满足：不产生任何写入（保持幂等）
            return Unit.Value;
        }

        await taskManager.ChangeTaskAssigneeAsync(task, _assignee, ct).ConfigureAwait(false);

        return Unit.Value;
    }
}
