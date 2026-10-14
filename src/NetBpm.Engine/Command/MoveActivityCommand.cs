using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.Command;

/// <summary>
/// 流程实例跳转：把运行中的实例从当前等待节点搬到指定节点并继续执行。
/// 对应 Activiti 的 <c>runtimeService.createChangeActivityStateBuilder().moveActivityIdTo(...)</c>（子集版）。
/// </summary>
/// <remarks>
/// <para>
/// 行为：定位实例当前<b>唯一</b>等待的执行 → 删除该执行上的待办（带原因，任务历史一并收口）→
/// 收口当前活动的历史行 → 把执行的当前元素改为目标节点 → 规划 <c>ContinueProcessOperation</c>，
/// 由议程从目标节点重新推进（目标节点的 start 监听器、行为、后续网关取道全部照常发生；
/// 目标是结束事件时实例会自然结束）。
/// </para>
/// <para>
/// <b>限制</b>：一次只能有一处等待（本子集的等待点都是用户任务）；实例停在多个并行分支上时拒绝跳转。
/// 变量随执行保留 —— 跳转不清变量。
/// </para>
/// </remarks>
public sealed class MoveActivityCommand : ICommand<Unit>
{
    private const string DefaultReason = "跳转";

    private readonly string _processInstanceId;
    private readonly string _targetActivityId;
    private readonly string? _reason;

    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="targetActivityId">目标节点 id（节点 / 网关 / 结束事件）。</param>
    /// <param name="reason">跳转原因（写进被删任务的历史与活动历史收口）。</param>
    public MoveActivityCommand(string processInstanceId, string targetActivityId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActivityId);

        _processInstanceId = processInstanceId;
        _targetActivityId = targetActivityId;
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
                "P7",
                "跳转需要删除当前待办，宿主必须注册任务管理器实现");
        var resolver = services.ProcessDefinitionResolver
            ?? throw new EngineNotImplementedException(
                "流程元素解析器（IProcessDefinitionResolver）",
                "运行期",
                "跳转需要把目标节点 id 解析回元素对象，宿主必须注册流程元素解析器实现");

        // 1. 实例必须还在运行（实例结束 = 根执行已被删除）
        var instance = await executionManager.FindByIdAsync(_processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineException($"流程实例 '{_processInstanceId}' 不存在或已结束，无法跳转。");

        // 2. 目标节点必须存在于流程定义中
        var target = await resolver
            .ResolveFlowElementAsync(instance.ProcessDefinitionId, _targetActivityId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineException(
                $"目标节点 '{_targetActivityId}' 不在流程定义 '{instance.ProcessDefinitionId}' 中。");

        // 3. 用待办定位当前等待的执行：本子集的等待点都是用户任务（有任务必有执行）。
        //    多个待办/多个执行意味着并行分支 —— 跳转语义未定义，明确拒绝。
        var tasks = await taskManager
            .FindTasksByProcessInstanceIdAsync(instance.Id, ct)
            .ConfigureAwait(false);

        if (tasks.Count == 0)
        {
            throw new EngineException(
                $"流程实例 '{instance.Id}' 当前没有等待中的待办，无法定位跳转起点。");
        }

        var executions = new List<IExecutionEntity>(tasks.Count);
        foreach (var task in tasks)
        {
            if (task.ExecutionId is null)
            {
                continue;
            }

            var execution = await executionManager.FindByIdAsync(task.ExecutionId, ct).ConfigureAwait(false);
            if (execution is not null && executions.All(e => !string.Equals(e.Id, execution.Id, StringComparison.Ordinal)))
            {
                executions.Add(execution);
            }
        }

        if (executions.Count > 1)
        {
            throw new EngineException(
                $"流程实例 '{instance.Id}' 停在 {executions.Count} 个并行分支上，暂不支持跳转。");
        }

        var current = executions[0];

        if (string.Equals(current.CurrentActivityId, _targetActivityId, StringComparison.Ordinal))
        {
            throw new EngineException(
                $"流程实例 '{instance.Id}' 已停在目标节点 '{_targetActivityId}'，无需跳转。");
        }

        // 4. 删除当前待办（带原因：任务历史记录"因跳转被删除"）
        foreach (var task in tasks)
        {
            await taskManager.DeleteTaskAsync(task, $"{_reason} → {_targetActivityId}", ct).ConfigureAwait(false);
        }

        // 5. 收口当前活动的历史行（只收历史，不产生"活动完成"语义 —— 与执行删除清理同口径）
        if (current.CurrentActivityId is not null)
        {
            await services.HistoryManager
                .RecordActivityEndQuietlyAsync(current, _reason, ct)
                .ConfigureAwait(false);
        }

        // 6. 改道：CurrentFlowElement 的 setter 会同步 ACT_ID_，再显式落库
        current.CurrentFlowElement = target;
        await executionManager.UpdateAsync(current, ct).ConfigureAwait(false);

        // 7. 从目标节点重新推进；议程在命令结束后统一排空（继续走完到下一个等待点）
        services.Agenda.PlanContinueProcessOperation(current);

        return Unit.Value;
    }
}
