using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;

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
/// <para>
/// 🔑 <b>解析用的容器随命令走</b>：命令内一律用 <see cref="CommandContext.ScopedServices"/>（命令级 DI 作用域），
/// 不用构造时注入的根容器。这样 <c>AddScoped</c> 注册的委托既能正常解析，
/// 又能保证它拿到的 <c>AddScoped</c> 依赖在<b>本命令内</b>唯一、命令结束即释放；
/// 命令外（部署自检、诊断、测试 harness）没有作用域，退回根容器。
/// </para>
/// </summary>
public sealed class ServiceProviderActivityDelegateResolver : IActivityDelegateResolver
{
    private readonly IServiceProvider _rootProvider;

    public ServiceProviderActivityDelegateResolver(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _rootProvider = serviceProvider;
    }

    /// <summary>
    /// 实际解析用的容器：命令内 = 命令作用域，命令外 = 根容器。
    /// </summary>
    private IServiceProvider Provider => CommandContext.Current?.ScopedServices ?? _rootProvider;

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
            return Provider.GetKeyedService<object>(unwrapped);
        }

        var fromContainer = Provider.GetService(type);
        if (fromContainer is not null)
        {
            return fromContainer;
        }

        // 容器里没有：按 Activiti 的 activiti:class 语义反射创建。
        // 🔑 失败必须翻译成 EngineException：裸的 MissingMethodException / TargetInvocationException
        // 会被宿主当成「框架炸了」，而真实原因通常是「忘了注册到 DI」或「类型没有 public 无参构造」。
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception exception) when (exception is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            throw new EngineException(
                $"服务任务实现类型 '{type.FullName}' 既未注册到容器，也无法反射创建（需要 public 无参构造函数）。"
                + "请把它注册到 DI（AddTransient / AddScoped，或用 [ServiceTaskName] + AddServiceTaskDelegates 扫描注册）。",
                exception);
        }
    }

    /// <inheritdoc />
    public object? ResolveByExpressionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Activiti 的 delegateExpression 是 EL 表达式（${beanName} 取容器里的 bean）：
        // 这里解析 ${...} 包裹的名字，裸名字同样兼容（与既有 BPMN 的写法一致）。
        var unwrapped = UnwrapExpression(name);

        // 先按「容器中的类型名」解析，再退化为「已注册的委托实例」
        var byKeyedService = Provider.GetKeyedService<object>(unwrapped);
        if (byKeyedService is not null)
        {
            return byKeyedService;
        }

        foreach (var service in Provider.GetServices<IServiceTaskDelegate>())
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
