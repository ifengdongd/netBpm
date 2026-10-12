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

        var type = Type.GetType(className, throwOnError: false);
        if (type is null)
        {
            return null;
        }

        var fromContainer = _serviceProvider.GetService(type);
        return fromContainer ?? Activator.CreateInstance(type);
    }

    /// <inheritdoc />
    public object? ResolveByExpressionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 先按「容器中的类型名」解析，再退化为「已注册的委托实例」
        var byKeyedService = _serviceProvider.GetKeyedService<object>(name);
        if (byKeyedService is not null)
        {
            return byKeyedService;
        }

        foreach (var service in _serviceProvider.GetServices<IServiceTaskDelegate>())
        {
            if (string.Equals(service.GetType().FullName, name, StringComparison.Ordinal)
                || string.Equals(service.GetType().Name, name, StringComparison.Ordinal))
            {
                return service;
            }
        }

        return null;
    }
}
