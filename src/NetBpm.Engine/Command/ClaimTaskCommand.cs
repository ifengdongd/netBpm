using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 认领 / 取消认领任务。对应 Activiti 的 <c>ClaimTaskCmd</c>。
/// <para><b>语义（逐条照搬）</b>：</para>
/// <list type="bullet">
///   <item><description><paramref name="userId"/> 非空：置 <c>CLAIM_TIME_ = now</c>；
///     若已被<b>别人</b>认领则抛错；若尚未认领则改办理人（并补流程实例级 <c>'participant'</c> 关系）；</description></item>
///   <item><description><paramref name="userId"/> 为空：清空 <c>CLAIM_TIME_</c> 与办理人（<c>unclaim</c>）。</description></item>
/// </list>
/// <para>
/// ⚠️ Activiti 靠 <c>DbSqlSession</c> 的脏检查在 flush 时写库；本实现没有任务级脏检查，
/// 因此「只改了 <c>CLAIM_TIME_</c>」（办理人本来就等于要认领的人）的场景必须显式再写一次。
/// </para>
/// </summary>
public sealed class ClaimTaskCommand : ICommand<Unit>
{
    private readonly string _taskId;
    private readonly string? _userId;

    public ClaimTaskCommand(string taskId, string? userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);

        _taskId = taskId;
        _userId = userId;
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
            throw new EngineException("Cannot claim a suspended task");
        }

        var previousAssignee = task.Assignee;
        var desiredAssignee = _userId;

        if (_userId is null)
        {
            // unclaim：认领时间归零、办理人清空
            task.ClaimTime = null;
        }
        else
        {
            task.ClaimTime = DateTime.UtcNow;

            if (previousAssignee is not null && !string.Equals(previousAssignee, _userId, StringComparison.Ordinal))
            {
                // 被他人认领：报错（等于自己则视为已满足后置条件，静默通过）
                throw new EngineException(
                    $"Task '{task.Id}' is already claimed by '{previousAssignee}'");
            }
        }

        await taskManager.ChangeTaskAssigneeAsync(task, desiredAssignee, ct).ConfigureAwait(false);

        if (string.Equals(previousAssignee, desiredAssignee, StringComparison.Ordinal))
        {
            // 办理人没变 ⇒ 上面那次调用不会写库，需要显式把 CLAIM_TIME_ 落库
            await taskManager.UpdateTaskAsync(task, ct).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}
