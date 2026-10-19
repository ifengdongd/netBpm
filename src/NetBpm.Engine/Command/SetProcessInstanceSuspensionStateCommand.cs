namespace NetBpm.Engine.Command;

/// <summary>
/// 挂起 / 激活单个流程实例（执行树 + 任务）。对应 Activiti 的
/// <c>runtimeService.suspendProcessInstanceById</c> / <c>activateProcessInstanceById</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须同时改两张表</b>：<c>ACT_RU_EXECUTION.SUSPENSION_STATE_</c> 决定「引擎会不会继续推进」，
/// <c>ACT_RU_TASK.SUSPENSION_STATE_</c> 才是 <c>CompleteTaskCommand</c> / <c>ClaimTaskCommand</c>
/// 的挂起检查依据。只改前者会出现「实例已挂起、待办却能完成」的漏洞。
/// </para>
/// <para>
/// <b>与 Activiti 对齐的点</b>：
/// <list type="bullet">
///   <item><description>只动 <c>SUSPENSION_STATE_</c>，<b>不</b>动 <c>IS_ACTIVE_</c> ——
///   挂起是「暂停受理」，不是「节点失效」，并行 Join 与流程图着色不受影响；</description></item>
///   <item><description>幂等：重复挂起 / 激活只会再写一次同样的值；</description></item>
///   <item><description>挂起后待办仍能被查询到（Activiti 的 <c>TaskQuery</c> 默认不过滤挂起位），
///   只是不能完成 / 认领。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>子集边界</b>：没有定时器 / 作业，因此不存在「挂起时顺延到期时间」的语义
/// （Activiti 的 <c>activateProcessInstanceById(id, new Date())</c> 用这个日期平移作业）；
/// 本命令不接收日期参数。
/// </para>
/// </remarks>
public sealed class SetProcessInstanceSuspensionStateCommand : ICommand<Unit>
{
    private readonly string _processInstanceId;
    private readonly bool _suspended;

    /// <param name="processInstanceId">流程实例 id（根执行 id）。</param>
    /// <param name="suspended"><see langword="true"/> 挂起，<see langword="false"/> 激活。</param>
    public SetProcessInstanceSuspensionStateCommand(string processInstanceId, bool suspended)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        _processInstanceId = processInstanceId;
        _suspended = suspended;
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var executionManager = context.Services.ExecutionManager;
        var taskManager = context.Services.TaskManager
            ?? throw new EngineException("任务管理器未注入，无法挂起 / 激活流程实例。");

        var action = _suspended ? "挂起" : "激活";

        // 1. 实例必须还在运行
        var instance = await executionManager
            .FindByIdAsync(_processInstanceId, ct)
            .ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException(
                $"流程实例 '{_processInstanceId}' 不存在或已结束，无法{action}。");

        // 2. 只接受流程实例 id：对子执行挂起没有语义（Activiti 也只允许实例级）
        if (!instance.IsProcessInstanceType)
        {
            throw new EngineException(
                $"'{_processInstanceId}' 不是流程实例（是执行 '{instance.Id}'），不能单独{action}；请传流程实例 id。");
        }

        // 3. 执行树 + 任务一起改（见类注释：少改一张表就是漏洞）
        await executionManager
            .UpdateSuspensionStateByProcessInstanceIdAsync(instance.Id, _suspended, ct)
            .ConfigureAwait(false);

        await taskManager
            .UpdateSuspensionStateByProcessInstanceIdAsync(instance.Id, _suspended, ct)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
