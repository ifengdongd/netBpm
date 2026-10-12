using NetBpm.Bpmn.Model;
using NetBpm.Engine.Services;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// <see cref="IActivityBehaviorFactory"/> 的默认实现（8 个具体行为的唯一分发点）。
/// <para>元素 → 行为 的对照表见开发计划 §1.2。</para>
/// </summary>
public sealed class ActivityBehaviorFactory : IActivityBehaviorFactory
{
    private readonly Func<IEngineServices> _servicesAccessor;

    /// <param name="servicesAccessor">
    /// 引擎服务的延迟取用。用 <see cref="Func{TResult}"/> 而非直接注入实例，是为了打破
    /// 「<see cref="IEngineServices"/> 依赖行为工厂、行为工厂又依赖引擎服务」的构造循环。
    /// </param>
    public ActivityBehaviorFactory(Func<IEngineServices> servicesAccessor)
    {
        ArgumentNullException.ThrowIfNull(servicesAccessor);
        _servicesAccessor = servicesAccessor;
    }

    /// <inheritdoc />
    public IActivityBehavior Create(IFlowElement flowElement)
    {
        ArgumentNullException.ThrowIfNull(flowElement);

        var services = _servicesAccessor();

        return flowElement switch
        {
            // 事件
            StartEvent => new NoneStartEventActivityBehavior(services),
            EndEvent => new NoneEndEventActivityBehavior(services),

            // 网关
            ExclusiveGateway => new ExclusiveGatewayActivityBehavior(services),
            ParallelGateway => new ParallelGatewayActivityBehavior(services),

            // 任务
            UserTask => new UserTaskActivityBehavior(services),
            ServiceTask => new ServiceTaskDelegateBehavior(services),
            ManualTask => new ManualTaskActivityBehavior(services),
            ScriptTask => new ScriptTaskActivityBehavior(services),

            _ => throw new EngineException(
                $"流程元素 '{flowElement.Id}'（{flowElement.GetType().Name}）不在本子集支持范围内。" +
                "见开发计划 §1.2 支持的元素列表。"),
        };
    }
}
