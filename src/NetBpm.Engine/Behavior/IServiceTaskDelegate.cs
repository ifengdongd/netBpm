using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Behavior;

/// <summary>
/// 服务任务的业务实现契约。对应 Activiti 的 <c>JavaDelegate</c>。
/// <para>
/// 三种写法（<c>activiti:class</c> / <c>delegateExpression</c> / <c>expression</c>）
/// 统一路由到 <see cref="ServiceTaskDelegateBehavior"/>（见 §1.2）。
/// </para>
/// </summary>
public interface IServiceTaskDelegate
{
    ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default);
}

/// <summary>
/// 服务任务实现的解析器。对应 Activiti 中「按类名实例化」与「按 bean 名从容器取」的两条路径。
/// </summary>
public interface IActivityDelegateResolver
{
    /// <summary>解析 <c>activiti:class</c>（.NET 类型全名）。</summary>
    object? ResolveByClassName(string className);

    /// <summary>解析 <c>activiti:delegateExpression</c>（容器中的服务/委托名）。</summary>
    object? ResolveByExpressionName(string name);
}
