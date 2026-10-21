using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 取消流程实例中**指定活动**上的活动实例（Camunda <c>cancelActivityInstance</c> 的裁剪版）。
/// <para>
/// 与 <see cref="MoveActivityCommand"/> 的区别：跳转是「把令牌搬走」，取消是「让这处令牌消失」——
/// 其余并行分支不受影响；如果取消后实例已经没有活动分支，实例随之结束（与正常走完同口径）。
/// </para>
/// </summary>
/// <remarks>
/// 收口顺序与服务端其它运维命令保持一致：
/// <list type="number">
///   <item><description>任务：按执行（含子执行）逐个 <c>DeleteTaskAsync</c>，任务历史记「因取消被删除」；</description></item>
///   <item><description>定时作业：删掉挂在该执行上的边界定时器，避免轮询对着已消失的执行空转；</description></item>
///   <item><description>历史：删执行时由执行管理器收口 <c>ACT_HI_ACTINST</c>（只收口、不发 ACTIVITY_COMPLETED）；</description></item>
///   <item><description>实例：若已无活动分支，规划 <c>EndExecutionOperation</c> 让实例正常结束（写 PROCESS_COMPLETED + 历史收口）。</description></item>
/// </list>
/// 并发：多个控制台同时取消同一活动时，后到者读不到活动执行而抛
/// <see cref="EngineException"/>（不会重复收口历史）。
/// </remarks>
public sealed class CancelActivityCommand : ICommand<Unit>
{
    private const string DefaultReason = "取消活动";

    private readonly string _processInstanceId;
    private readonly string _activityId;
    private readonly string? _reason;

    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="activityId">要取消的活动（BPMN 元素）id。</param>
    /// <param name="reason">取消原因（写进任务历史 / 活动历史收口）。</param>
    public CancelActivityCommand(string processInstanceId, string activityId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);

        _processInstanceId = processInstanceId;
        _activityId = activityId;
        _reason = string.IsNullOrWhiteSpace(reason) ? DefaultReason : reason.Trim();
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var executionManager = services.ExecutionManager;
        var taskManager = services.TaskManager
            ?? throw new EngineNotImplementedException(
                "任务管理器（ITaskManager）",
                "运维",
                "取消活动需要结束该活动上的待办，宿主必须注册任务管理器实现");
        var resolver = services.ProcessDefinitionResolver
            ?? throw new EngineNotImplementedException(
                "流程元素解析器（IProcessDefinitionResolver）",
                "运行期",
                "取消活动需要把活动 id 解析回元素对象，宿主必须注册流程元素解析器实现");

        // 1. 实例必须还在运行
        var instance = await executionManager.FindByIdAsync(_processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"流程实例 '{_processInstanceId}' 不存在或已结束，无法取消活动。");

        // 2. 活动必须存在于流程定义中（拼错 id 时给出可读错误，而不是「没有活动执行」）
        _ = await resolver
            .ResolveFlowElementAsync(instance.ProcessDefinitionId, _activityId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineException(
                $"活动 '{_activityId}' 不在流程定义 '{instance.ProcessDefinitionId}' 中。");

        // 3. 定位该活动上的活动执行（并行 / 多实例会有多条）
        var allExecutions = await executionManager
            .FindAllByProcessInstanceIdAsync(instance.Id, ct)
            .ConfigureAwait(false);

        var targets = allExecutions
            .Where(execution =>
                !execution.IsEnded
                && execution.IsActive
                && !string.Equals(execution.Id, instance.Id, StringComparison.Ordinal)
                && string.Equals(execution.CurrentActivityId, _activityId, StringComparison.Ordinal))
            .ToArray();

        if (targets.Length == 0)
        {
            throw new EngineException(
                $"流程实例 '{instance.Id}' 的活动 '{_activityId}' 当前没有活动中的执行" +
                "（已结束、已被取消，或令牌不在此节点上）。");
        }

        // 4. 先结束待办（含被取消执行的子执行上的待办），任务历史才能记下删除原因
        var targetIds = new HashSet<string>(targets.Select(execution => execution.Id), StringComparer.Ordinal);

        foreach (var execution in allExecutions)
        {
            if (execution.ParentId is not null && targetIds.Contains(execution.ParentId))
            {
                targetIds.Add(execution.Id);
            }
        }

        var tasks = await taskManager
            .FindTasksByProcessInstanceIdAsync(instance.Id, ct)
            .ConfigureAwait(false);

        foreach (var task in tasks)
        {
            if (task.ExecutionId is null || !targetIds.Contains(task.ExecutionId))
            {
                continue;
            }

            await taskManager
                .DeleteTaskAsync(task, $"{_reason} → {_activityId}", ct)
                .ConfigureAwait(false);
        }

        // 5. 逐条执行收口：定时作业 → 删执行（含历史活动行、变量、候选关系）
        foreach (var execution in targets)
        {
            if (services.TimerJobManager is { } timerJobs)
            {
                await timerJobs.DeleteByExecutionAsync(execution.Id, ct).ConfigureAwait(false);
            }

            await executionManager
                .DeleteExecutionAndRelatedDataAsync(execution, $"{_reason} → {_activityId}", ct)
                .ConfigureAwait(false);
        }

        // 6. 取消完最后一个活动：实例按正常结束路径收口（PROCESS_COMPLETED + 历史 END_TIME_）
        var remaining = await executionManager
            .CountActiveChildExecutionsForProcessInstanceAsync(instance.Id, ct)
            .ConfigureAwait(false);

        if (remaining == 0)
        {
            services.Agenda.PlanEndExecutionOperation(instance);
        }

        return Unit.Value;
    }
}
