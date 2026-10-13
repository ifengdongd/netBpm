namespace NetBpm.Runtime;

/// <summary>
/// 声明服务任务委托在流程定义里的「逻辑名」。
/// <para>
/// <c>activiti:delegateExpression</c> 的取值是<b>流程里的名字</b>，不要求等于 C# 类型名 ——
/// 例如 <c>leave.ApproveComplete</c> 表达的是业务语义，而不是某个类。
/// 把这个名字标在实现类上，再交给
/// <see cref="ServiceTaskDelegateRegistrationExtensions.AddServiceTaskDelegates(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Reflection.Assembly[])"/>
/// 扫描注册，宿主就不必在组合根里逐个写 keyed 注册。
/// </para>
/// </summary>
/// <example>
/// <code>
/// [ServiceTaskName("leave.ApproveComplete")]
/// public sealed class ApproveCompleteDelegate : IServiceTaskDelegate { /* ... */ }
/// </code>
/// </example>
/// <remarks>
/// 一个类可以有多个名字（同一实现在流程里以不同语义出现时用得上），但<b>一个名字只能对应一个类</b>：
/// 重名会在注册时直接抛异常，不会静默覆盖。
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ServiceTaskNameAttribute : Attribute
{
    /// <summary>声明一个服务任务逻辑名。</summary>
    /// <param name="name">
    /// 与 BPMN 里 <c>activiti:delegateExpression</c> 完全一致的字符串（按序数比较，大小写敏感）。
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为 <see langword="null"/>、空或纯空白。</exception>
    public ServiceTaskNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    /// <summary>流程定义里引用该实现时使用的名字。</summary>
    public string Name { get; }
}
