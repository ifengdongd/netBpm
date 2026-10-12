using NetBpm.Bpmn.Model;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 行为工厂：把 BPMN 元素映射到它的 <see cref="IActivityBehavior"/>。
/// 对应 Activiti 的 <c>ActivityBehaviorFactory</c> —— 但本子集只需要 8 个具体行为。
/// </summary>
public interface IActivityBehaviorFactory
{
    /// <summary>
    /// 创建元素对应的行为。子集之外的元素直接失败，不做静默降级。
    /// </summary>
    IActivityBehavior Create(IFlowElement flowElement);
}
