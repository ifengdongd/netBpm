using NetBpm.Bpmn.Model;
using NetBpm.El;
using NetBpm.Engine.Execution;

namespace NetBpm.Engine;

/// <summary>
/// 顺序流条件判定。对应 Activiti 的 <c>ConditionUtil</c>（开发计划 §5.2）。
/// </summary>
public static class ConditionUtil
{
    /// <summary>
    /// 判定顺序流条件是否为真。
    /// <para>
    /// 🔑 关键行为：<b>没有条件的顺序流恒为 true</b>。这不是"顺手优化"，
    /// 而是 Activiti 的核心语义 —— 排他网关「取第一个条件为真的流」依赖它（§5.4 之一）。
    /// </para>
    /// </summary>
    public static async ValueTask<bool> HasTrueConditionAsync(
        SequenceFlow sequenceFlow,
        IDelegateExecution execution,
        IExpressionManager expressionManager,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sequenceFlow);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(expressionManager);

        var conditionExpression = sequenceFlow.ConditionExpression;
        if (string.IsNullOrEmpty(conditionExpression))
        {
            return true;
        }

        var expression = expressionManager.CreateExpression(conditionExpression);
        var value = await expression.GetValueAsync(execution, ct).ConfigureAwait(false);
        return value is true;
    }
}
