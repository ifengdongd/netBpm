using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Agenda;

/// <summary>
/// 操作队列（议程）。对应 Activiti 的 <c>Agenda</c>。
/// <para>
/// ⚠️ 必须用「队列 + 排空」而非递归调用（开发计划 §5.3 / 风险 R4）：
/// 递归写法在并行 Join 场景下会因「谁先到达」的时序不确定而产生错误结果。
/// </para>
/// </summary>
public interface IEngineAgenda
{
    /// <summary>规划「执行当前流程元素的行为」。</summary>
    void PlanContinueProcessOperation(IExecutionEntity execution);

    /// <summary>
    /// 规划「离开当前节点、沿出流前进」。
    /// </summary>
    /// <param name="execution">执行。</param>
    /// <param name="evaluateConditions">
    /// 是否校验出流条件。并行网关必须传 <see langword="false"/>（见 §5.4 之二 ⑤）。
    /// </param>
    void PlanTakeOutgoingSequenceFlowsOperation(IExecutionEntity execution, bool evaluateConditions);

    /// <summary>规划「结束执行」。</summary>
    void PlanEndExecutionOperation(IExecutionEntity execution);

    /// <summary>
    /// 规划「触发当前节点上的行为」。对应 Activiti 的 <c>planTriggerExecutionOperation</c>。
    /// <para>用户任务完成（<c>CompleteTaskCommand</c>）后用它把流程继续推进。</para>
    /// </summary>
    /// <param name="execution">执行。</param>
    /// <param name="signalName">信号名（本子集恒为 <see langword="null"/>）。</param>
    /// <param name="signalData">信号数据（本子集恒为 <see langword="null"/>）。</param>
    void PlanTriggerExecutionOperation(
        IExecutionEntity execution,
        string? signalName = null,
        object? signalData = null);

    /// <summary>队列是否为空。</summary>
    bool IsEmpty { get; }

    /// <summary>
    /// 排空队列。必须在一个命令（= 一个事务）内完成，且不要在其中 await 外部 IO（§5.3）。
    /// </summary>
    ValueTask DrainAsync(CancellationToken ct = default);

    /// <summary>清空队列（异常回滚时使用）。</summary>
    void Clear();
}
