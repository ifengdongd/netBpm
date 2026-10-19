namespace NetBpm.Engine.Command;

/// <summary>
/// 删除（撤销）运行中的流程实例：待办、变量与执行树一起清理，历史账本保留并收口。
/// 对应 Activiti 的 <c>runtimeService.deleteProcessInstance(processInstanceId, deleteReason)</c>。
/// </summary>
/// <remarks>
/// <para>
/// 与「正常跑完」的唯一区别体现在历史：<c>ACT_HI_PROCINST.DELETE_REASON_</c> 记录原因，
/// 被删待办的 <c>ACT_HI_TASKINST.DELETE_REASON_</c> 同因，活动与任务历史行照常写入结束时间
/// （<c>RecordActivityEndQuietly</c>：只收口历史，不产生「活动完成」语义）。
/// </para>
/// <para>
/// <b>与 Activiti 的对齐点与子集边界</b>：
/// <list type="bullet">
///   <item><description>
///   <c>END_ACT_ID_</c> 为 <see langword="null"/>：Activiti 的删除命令给
///   <c>deleteProcessInstanceExecutionEntity</c> 传的 <c>currentFlowElementId</c> 就是 null
///   （只有正常结束才传当前节点）。
///   </description></item>
///   <item><description>
///   <c>cancel = true</c> 与 Activiti 一致；本子集没有边界事件 / 事件子流程，
///   该标记只影响删除路径本身。
///   </description></item>
///   <item><description>
///   不派发流程级事件：Activiti 在这条命令上派发的是实体删除类事件，本子集的事件枚举
///   （<c>EngineEvents</c>）里没有对应类型 —— 宁可不发，也不发一个 Activiti 不会发的类型。
///   </description></item>
///   <item><description>
///   已结束的实例删不了（运行态行已不存在）：历史账本只增不删，要「清历史」属于运维动作。
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class DeleteProcessInstanceCommand : ICommand<Unit>
{
    /// <summary>未指定原因时的默认删除原因。</summary>
    private const string DefaultReason = "删除流程实例";

    private readonly string _processInstanceId;
    private readonly string _deleteReason;

    /// <param name="processInstanceId">流程实例 id（根执行 id）。</param>
    /// <param name="deleteReason">
    /// 删除原因（写进流程实例与任务历史）。为空时取「删除流程实例」——
    /// Activiti 允许传 null，这里取一个可审计的默认值。
    /// </param>
    public DeleteProcessInstanceCommand(string processInstanceId, string? deleteReason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId);

        _processInstanceId = processInstanceId;
        _deleteReason = string.IsNullOrWhiteSpace(deleteReason) ? DefaultReason : deleteReason.Trim();
    }

    /// <inheritdoc />
    public async ValueTask<Unit> ExecuteAsync(ICommandContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var executionManager = context.Services.ExecutionManager;

        // 1. 实例必须还在运行（结束 = 根执行已被删除）
        var instance = await executionManager.FindByIdAsync(_processInstanceId, ct).ConfigureAwait(false)
            ?? throw new EngineObjectNotFoundException($"流程实例 '{_processInstanceId}' 不存在或已结束，无法删除。");

        // 2. 只接受流程实例 id：传单个子执行会让「删除」只清掉一条分支，
        //    留下一个既没任务、又永远收不了口的实例（比报错更难排查）
        if (!instance.IsProcessInstanceType)
        {
            throw new EngineException(
                $"'{_processInstanceId}' 不是流程实例（是执行 '{instance.Id}'），不能单独删除；" +
                "请传流程实例 id。");
        }

        // 3. 一次性收口：待办（含任务历史）→ 变量与字节行 → 执行树 → 流程实例历史
        await executionManager
            .DeleteProcessInstanceExecutionEntityAsync(
                instance.Id,
                currentFlowElementId: null,
                deleteReason: _deleteReason,
                cancel: true,
                ct)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
