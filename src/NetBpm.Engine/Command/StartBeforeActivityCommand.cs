using NetBpm.Bpmn.Model;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Command;

/// <summary>
/// 在指定活动**之前**启动一个新令牌（Camunda <c>startBeforeActivity</c> 的裁剪版）。
/// <para>
/// 语义：不动现有执行，给流程实例挂一个并发子执行，把它的当前元素设为目标活动并继续推进 ——
/// 目标活动的 start 监听器、行为（用户任务建待办 / 服务任务执行）、出流都会照常发生。
/// 因此「在该活动前启动」的结果就是「该活动多了一个活动实例」，与并行网关加一条分支等价。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>只接受可执行的流节点</b>（用户任务 / 服务任务 / 脚本任务 / 人工任务 / 子流程 / 结束事件等）：
/// 顺序流、数据对象这类元素没有行为，挂上去会得到一个无法推进的执行。
/// </para>
/// <para>
/// 并发：每次调用都会新增一个子执行（幂等由调用方决定）；同一活动上重复调用会得到多个活动实例，
/// 这正是「多开一条分支」的预期语义。
/// </para>
/// </remarks>
public sealed class StartBeforeActivityCommand : ICommand<Unit>
{
    private readonly string _processInstanceId;
    private readonly string _activityId;

    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="activityId">要启动的活动（BPMN 流节点）id。</param>
    public StartBeforeActivityCommand(string processInstanceId, string activityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);

        _processInstanceId = processInstanceId;
        _activityId = activityId;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.Services;
        var executionManager = services.ExecutionManager;
        var resolver = services.ProcessDefinitionResolver
            ?? throw new EngineNotImplementedException(
                "流程元素解析器（IProcessDefinitionResolver）",
                "运行期",
                "启动活动需要把活动 id 解析回元素对象，宿主必须注册流程元素解析器实现");

        var instance = await executionManager.FindByIdAsync(_processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"流程实例 '{_processInstanceId}' 不存在或已结束，无法启动活动。");

        var target = await resolver
            .ResolveFlowElementAsync(instance.ProcessDefinitionId, _activityId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineException(
                $"活动 '{_activityId}' 不在流程定义 '{instance.ProcessDefinitionId}' 中。");

        // 顺序流 / 数据对象没有行为：挂上去的执行永远推进不了，必须提前拒绝
        if (target is not FlowNode)
        {
            throw new EngineException(
                $"元素 '{_activityId}' 不是可执行的流节点（当前类型 {target.GetType().Name}），无法在其之前启动。");
        }

        // 新令牌：子执行挂在流程实例下，共享实例级变量作用域。
        // 不置 IS_CONCURRENT_：全引擎（并行/包含网关分叉）刻意保持该列为 0，
        // 以保证与 Activiti 的库级对拍一致；这里是唯一会额外建令牌的入口，不能开先例。
        var child = executionManager.CreateChildExecution(instance);
        child.CurrentFlowElement = target;

        await executionManager.InsertAsync(child, ct).ConfigureAwait(false);

        // 从目标节点继续推进（start 监听器 / 行为 / 出流由议程统一处理）
        services.Agenda.PlanContinueProcessOperation(child);

        return Unit.Value;
    }
}
