using NetBpm.Bpmn.Model;

namespace NetBpm.Engine.Services;

/// <summary>
/// 按流程定义解析流程元素。
/// <para>
/// 🔑 为什么需要它：执行实体的 <c>CurrentFlowElement</c> 只在<b>同一命令内</b>成立。
/// 下一个命令（例如「完成任务」）从库里把执行读回来时，只知道 <c>ACT_ID_</c> 字符串，
/// 必须靠流程定义把它解析回元素对象 —— 这正是 Activiti 在
/// <c>ExecutionEntityImpl.getCurrentFlowElement()</c> 里做的事（懒解析 + 缓存）。
/// </para>
/// </summary>
public interface IProcessDefinitionResolver
{
    /// <summary>解析 <paramref name="processDefinitionId"/> 下 id 为 <paramref name="activityId"/> 的元素。</summary>
    /// <returns>找不到时返回 <see langword="null"/>。</returns>
    ValueTask<IFlowElement?> ResolveFlowElementAsync(
        string? processDefinitionId,
        string? activityId,
        CancellationToken ct = default);
}
