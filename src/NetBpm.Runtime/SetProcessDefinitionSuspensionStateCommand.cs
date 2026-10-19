using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 挂起 / 激活流程定义，并可选地一并挂起其<b>运行中的实例</b>。
/// 对应 Activiti 的 <c>repositoryService.suspendProcessDefinitionById(id, suspendProcessInstances, date)</c>
/// 与 <c>activateProcessDefinitionById(id, activateProcessInstances, date)</c>。
/// </summary>
/// <remarks>
/// <para>
/// 三个动作必须在<b>同一个命令（= 一个事务）</b>里：定义挂起位、执行树、任务行。
/// 分开提交会出现「定义已挂起、实例仍可办理」或反过来的中间态。
/// </para>
/// <para>
/// <b>与 Activiti 的差异</b>：
/// <list type="bullet">
///   <item><description>
///   不接收 <c>new Date()</c>（挂起日期）—— 本子集没有定时器 / 作业，
///   该参数只用于平移作业到期时间。
///   </description></item>
///   <item><description>
///   已结束的定义也能重复挂起 / 激活（幂等，只写 <c>SUSPENSION_STATE_</c>）。
///   </description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class SetProcessDefinitionSuspensionStateCommand : ICommand<Unit>
{
    private readonly IProcessDefinitionStore _definitionStore;
    private readonly string _processDefinitionId;
    private readonly bool _suspended;
    private readonly bool _affectRunningInstances;

    public SetProcessDefinitionSuspensionStateCommand(
        IProcessDefinitionStore definitionStore,
        string processDefinitionId,
        bool suspended,
        bool affectRunningInstances)
    {
        ArgumentNullException.ThrowIfNull(definitionStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(processDefinitionId);

        _definitionStore = definitionStore;
        _processDefinitionId = processDefinitionId;
        _suspended = suspended;
        _affectRunningInstances = affectRunningInstances;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var action = _suspended ? "挂起" : "激活";

        // 依赖先取齐：缺少任务管理器时必须在改任何数据之前失败（否则定义位已改、实例却没挂）
        var executionManager = context.Services.ExecutionManager;
        var taskManager = _affectRunningInstances
            ? context.Services.TaskManager
                ?? throw new EngineException("任务管理器未注入，无法一并" + action + "运行中的流程实例。")
            : null;

        // 1. 定义本身（不存在就直接失败，不能走到「改了 0 行也算成功」）
        var exists = await _definitionStore
            .SetSuspensionStateAsync(_processDefinitionId, _suspended, ct)
            .ConfigureAwait(false);

        if (!exists)
        {
            throw new EngineException($"流程定义 '{_processDefinitionId}' 不存在，无法{action}。");
        }

        if (!_affectRunningInstances)
        {
            return Unit.Value;
        }

        // 2. 运行中的实例：执行树 + 任务（两处一起改，理由同实例级命令）

        await executionManager
            .UpdateSuspensionStateByProcessDefinitionIdAsync(_processDefinitionId, _suspended, ct)
            .ConfigureAwait(false);

        // taskManager 非空的判据已在方法开头完成（_affectRunningInstances 为真即要求注入）
        await taskManager!
            .UpdateSuspensionStateByProcessDefinitionIdAsync(_processDefinitionId, _suspended, ct)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
