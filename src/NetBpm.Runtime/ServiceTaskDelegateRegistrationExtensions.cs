using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetBpm.Engine.Behavior;

namespace NetBpm.Runtime;

/// <summary>
/// 服务任务委托的批量注册 —— 把「组合根里逐个 keyed 注册」换成「扫一遍程序集」。
/// <para>
/// 扫描给定程序集里所有 <see cref="IServiceTaskDelegate"/> 实现，按下面的顺序为每个类型登记名字，
/// 全部注册成 <c>GetKeyedService&lt;object&gt;(name)</c> 能取到的 keyed 服务 ——
/// 也就是 <see cref="ServiceProviderActivityDelegateResolver"/> 的第一条解析路径，
/// 于是 <c>activiti:delegateExpression</c> 写什么名字、这里就登记什么名字，两边天然对齐。
/// </para>
/// <para>名字来源三档（前者优先）：</para>
/// <list type="number">
///   <item><description><see cref="ServiceTaskNameAttribute"/> 显式声明的名字 —— <b>重名直接抛异常</b>；</description></item>
///   <item><description>类型全名（<c>Namespace.ClassName</c>）—— 天然唯一；</description></item>
///   <item><description>类型短名（<c>ClassName</c>）—— <b>仅在程序集内唯一时登记</b>，重名时让位给显式名 / 全名，避免歧义。</description></item>
/// </list>
/// <example>
/// <code>
/// // 组合根一行搞定（Program.cs）
/// builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);
///
/// // 委托自己声明流程里的名字（LeaveDelegates.cs）
/// [ServiceTaskName("leave.ApproveComplete")]
/// public sealed class ApproveCompleteDelegate : IServiceTaskDelegate { /* ... */ }
/// </code>
/// </example>
/// <remarks>
/// 所有名字共享同一个实例：具体类型只注册一次单例，keyed 登记只是它的别名。
/// 因此委托的依赖注入行为与手写 <c>AddSingleton</c> 完全一致，也不会因为登记了多个别名而被构造多次。
/// </remarks>
public static class ServiceTaskDelegateRegistrationExtensions
{
    /// <summary>扫描并注册若干程序集里的服务任务委托。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="assemblies">要扫描的程序集（通常传 <c>typeof(Program).Assembly</c> 与业务程序集）。</param>
    /// <returns><paramref name="services"/>，便于链式调用。</returns>
    /// <exception cref="InvalidOperationException">两个类型声明了同一个 <see cref="ServiceTaskNameAttribute"/> 名字。</exception>
    public static IServiceCollection AddServiceTaskDelegates(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return services.AddServiceTaskDelegates((IEnumerable<Assembly>)assemblies);
    }

    /// <summary>扫描并注册若干程序集里的服务任务委托。</summary>
    /// <param name="services">服务集合。</param>
    /// <param name="assemblies">要扫描的程序集序列。</param>
    /// <returns><paramref name="services"/>，便于链式调用。</returns>
    /// <exception cref="InvalidOperationException">两个类型声明了同一个 <see cref="ServiceTaskNameAttribute"/> 名字。</exception>
    public static IServiceCollection AddServiceTaskDelegates(this IServiceCollection services, IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var delegates = Discover(assemblies);

        // 短名只在「全程序集内唯一」时才有资格当名字，否则宁可解析不到（启动期自检会报出来），
        // 也不要让两个实现去抢同一个名字。
        var shortNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in delegates)
        {
            shortNameCounts[type.Name] = shortNameCounts.TryGetValue(type.Name, out var count) ? count + 1 : 1;
        }

        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);

        // 第一档：显式名。冲突 = 作者意图被破坏，直接抛。
        foreach (var type in delegates)
        {
            foreach (var attribute in type.GetCustomAttributes<ServiceTaskNameAttribute>(inherit: false))
            {
                Register(byName, attribute.Name, type, throwOnConflict: true);
            }
        }

        // 第二、三档：类型名。只是便利回退，撞上了就保留先登记的，不打断启动。
        foreach (var type in delegates)
        {
            Register(byName, type.FullName ?? type.Name, type, throwOnConflict: false);

            if (shortNameCounts[type.Name] == 1)
            {
                Register(byName, type.Name, type, throwOnConflict: false);
            }
        }

        foreach (var type in delegates)
        {
            services.TryAddSingleton(type);
        }

        foreach (var (name, type) in byName)
        {
            // 所有别名都指向同一个单例实例：keyed 登记只是起了个名字，不重新构造。
            services.TryAddKeyedSingleton<object>(name, (provider, _) => provider.GetRequiredService(type));
        }

        return services;
    }

    /// <summary>
    /// 找出程序集里所有可实例化的 <see cref="IServiceTaskDelegate"/> 实现。
    /// 抽象类、接口、开放泛型都不算 —— 它们没法作为委托实例被解析。
    /// </summary>
    private static List<Type> Discover(IEnumerable<Assembly> assemblies)
    {
        var delegates = new List<Type>();

        foreach (var assembly in assemblies)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                if (typeof(IServiceTaskDelegate).IsAssignableFrom(type))
                {
                    delegates.Add(type);
                }
            }
        }

        return delegates;
    }

    private static void Register(Dictionary<string, Type> byName, string name, Type type, bool throwOnConflict)
    {
        if (byName.TryGetValue(name, out var existing))
        {
            if (existing == type || !throwOnConflict)
            {
                return;
            }

            throw new InvalidOperationException(
                $"服务任务名字 '{name}' 同时被 {existing.FullName} 与 {type.FullName} 声明。"
                + "一个名字只能指向一个实现，请改 ServiceTaskNameAttribute 的取值或去掉重名的类型。");
        }

        byName.Add(name, type);
    }
}
