using NetBpm.Bpmn.Model;

namespace NetBpm.Engine;

/// <summary>
/// 流程图连通性工具。对应 Activiti 的 <c>ExecutionGraphUtil</c>。
/// </summary>
public static class ExecutionGraphUtil
{
    /// <summary>
    /// 从 <paramref name="source"/> 沿出流出发，是否**可能**到达 <paramref name="target"/>。
    /// <para>
    /// ⚠️ 逐条照搬 Activiti 的语义（<c>ExecutionGraphUtil.isReachable</c>），包括两个易被"顺手改掉"的点：
    /// </para>
    /// <list type="number">
    ///   <item><description><b>不判定条件</b>：只看"图上能不能走到"，与运行期变量无关；</description></item>
    ///   <item><description>无出流的节点视为<b>可到达</b>（Activiti 的 <c>getOutgoingFlows().size() == 0 → true</c>），
    ///   用于包含网关 Join 的保守计数；</description></item>
    /// </list>
    /// <para>用途：包含网关 Join 时统计「仍在活动、且还能到达本网关」的执行数。</para>
    /// </summary>
    public static bool IsReachable(Process process, IFlowElement source, IFlowElement target)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        return IsReachableInternal(source, target, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsReachableInternal(
        IFlowElement source,
        IFlowElement target,
        HashSet<string> visited)
    {
        if (source is not IFlowNode flowNode || flowNode.OutgoingFlows.Count == 0)
        {
            // Activiti：没有出流的元素直接视为"可以到达"，避免 Join 永远等不到
            return true;
        }

        var sourceId = source.Id;

        if (sourceId is not null && !visited.Add(sourceId))
        {
            return false;
        }

        if (sourceId is not null && target.Id is not null
            && string.Equals(sourceId, target.Id, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var sequenceFlow in flowNode.OutgoingFlows)
        {
            var next = (IFlowElement?)sequenceFlow.TargetFlowNode ?? sequenceFlow;

            if (IsReachableInternal(next, target, visited))
            {
                return true;
            }
        }

        return false;
    }
}
