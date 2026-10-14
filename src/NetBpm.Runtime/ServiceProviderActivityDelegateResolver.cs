using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Behavior;

namespace NetBpm.Runtime;

/// <summary>
/// 基于 <see cref="IServiceProvider"/> 的服务任务实现解析器。
/// <para>
/// 对应 Activiti 的两条路径：
/// <list type="bullet">
///   <item><description><c>activiti:class</c> → 按类型全名解析（Activiti 用反射 new 实例，
///     这里优先从容器取，取不到再反射创建）；</description></item>
///   <item><description><c>activiti:delegateExpression</c> → 按名称从容器取（Activiti 对应 Spring bean）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ServiceProviderActivityDelegateResolver : IActivityDelegateResolver
{
    private readonly IServiceProvider _serviceProvider;

    public ServiceProviderActivityDelegateResolver(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public object? ResolveByClassName(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);

        // 宿主常把 delegateExpression 的写法（${name}）误用到 class 上：
        // class 的本义是 .NET 类型名，但剥壳后按 keyed 服务名解析同样成立 —— 宽容兼容。
        var unwrapped = UnwrapExpression(className);
        var type = Type.GetType(unwrapped, throwOnError: false);
        if (type is null)
        {
            // 类型名解析不到：回退 keyed 服务名（与 delegateExpression 同一条路）
            return _serviceProvider.GetKeyedService<object>(unwrapped);
        }

        var fromContainer = _serviceProvider.GetService(type);
        return fromContainer ?? Activator.CreateInstance(type);
    }

    /// <inheritdoc />
    public object? ResolveByExpressionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Activiti 的 delegateExpression 是 EL 表达式（${beanName} 取容器里的 bean）：
        // 这里解析 ${...} 包裹的名字，裸名字同样兼容（与既有 BPMN 的写法一致）。
        var unwrapped = UnwrapExpression(name);

        // 先按「容器中的类型名」解析，再退化为「已注册的委托实例」
        var byKeyedService = _serviceProvider.GetKeyedService<object>(unwrapped);
        if (byKeyedService is not null)
        {
            return byKeyedService;
        }

        foreach (var service in _serviceProvider.GetServices<IServiceTaskDelegate>())
        {
            if (string.Equals(service.GetType().FullName, unwrapped, StringComparison.Ordinal)
                || string.Equals(service.GetType().Name, unwrapped, StringComparison.Ordinal))
            {
                return service;
            }
        }

        return null;
    }

    /// <summary>剥掉 <c>${...}</c> 包裹（不配对时按原文返回）。</summary>
    private static string UnwrapExpression(string name)
    {
        var trimmed = name.Trim();

        return trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
            ? trimmed[2..^1].Trim()
            : trimmed;
    }
}
