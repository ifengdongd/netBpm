using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.UnitTests;

/// <summary>
/// 服务任务委托的批量注册
/// （<see cref="ServiceTaskDelegateRegistrationExtensions.AddServiceTaskDelegates(Microsoft.Extensions.DependencyInjection.IServiceCollection, Assembly[])"/>）。
/// <para>
/// 背景：引擎解析 <c>activiti:delegateExpression</c> 的第一条路径是「按名字取 keyed 服务」
/// （见 <see cref="ServiceProviderActivityDelegateResolver"/>）。本扩展把
/// 「委托类自己声明名字」变成「扫一遍程序集自动登记」，于是 BPMN 里的名字与宿主登记的名字天然对齐，
/// 组合根也不必再逐个手写 <c>AddKeyedSingleton</c>。
/// </para>
/// <para>
/// 这里钉四件事：<b>声明名可解析</b>、<b>类型全名/短名可回退</b>、<b>多个名字共享同一实例</b>、
/// <b>声明名重名直接抛</b>。
/// </para>
/// </summary>
public sealed class ServiceTaskDelegateRegistrationTests
{
    [Fact]
    public void DeclaredName_ShouldResolveToDelegate()
    {
        using var provider = BuildProvider();

        var resolved = Resolve(provider, "leave.ApproveComplete");

        Assert.IsType<ApproveLikeDelegate>(resolved);
    }

    [Fact]
    public void TypeFullName_ShouldResolveWhenNoNameDeclared()
    {
        using var provider = BuildProvider();

        Assert.IsType<UndecoratedDelegate>(Resolve(provider, typeof(UndecoratedDelegate).FullName!));
    }

    [Fact]
    public void TypeShortName_ShouldResolveWhenUnique()
    {
        using var provider = BuildProvider();

        Assert.IsType<UndecoratedDelegate>(Resolve(provider, nameof(UndecoratedDelegate)));
    }

    [Fact]
    public void MultipleNames_ShouldShareSameInstance()
    {
        using var provider = BuildProvider();

        var byDeclaredName = Resolve(provider, "leave.ApproveComplete");
        var byTypeName = Resolve(provider, typeof(ApproveLikeDelegate).FullName!);

        // keyed 登记只是给同一个单例起别名，不该因为名字多就多构造几次
        Assert.Same(byDeclaredName, byTypeName);
    }

    [Fact]
    public async Task Delegate_ShouldGetDependenciesFromContainer()
    {
        using var provider = BuildProvider();
        var store = provider.GetRequiredService<CountingStore>();

        var delegateInstance = Assert.IsType<StoreDependentDelegate>(Resolve(provider, "leave.StoreDependent"));

        await delegateInstance.ExecuteAsync(null!, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public void UnknownName_ShouldResolveToNull()
    {
        using var provider = BuildProvider();

        Assert.Null(Resolve(provider, "no.such.delegate"));
    }

    [Fact]
    public void DuplicateDeclaredName_ShouldThrow()
    {
        var conflictingAssembly = EmitAssemblyWithConflictingNames();
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddServiceTaskDelegates(conflictingAssembly));

        Assert.Contains(DuplicateName, error.Message, StringComparison.Ordinal);
        Assert.Contains("FirstDuplicateDelegate", error.Message, StringComparison.Ordinal);
        Assert.Contains("SecondDuplicateDelegate", error.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<CountingStore>();
        services.AddServiceTaskDelegates(typeof(ServiceTaskDelegateRegistrationTests).Assembly);

        return services.BuildServiceProvider();
    }

    /// <summary>走引擎自己的解析器，验证「名字 → 实现」这条链真的通了。</summary>
    private static object? Resolve(IServiceProvider provider, string name) =>
        new ServiceProviderActivityDelegateResolver(provider).ResolveByExpressionName(name);

    private const string DuplicateName = "dup.name";

    /// <summary>
    /// 造一个只存在于内存里的程序集，里面两个类声明了同一个服务任务名字。
    /// <para>
    /// 为什么不用普通类型：重名是「注册时」才暴露的错误，冲突双方若写在测试程序集里，
    /// 所有（会扫描该程序集的）用例都会跟着抛。放进动态程序集才能让这条用例自成一体。
    /// </para>
    /// </summary>
    private static AssemblyBuilder EmitAssemblyWithConflictingNames()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NetBpm.UnitTests.DuplicateServiceTaskNames"),
            AssemblyBuilderAccess.Run);

        var module = assembly.DefineDynamicModule("Main");

        DefineConflictingDelegate(module, "FirstDuplicateDelegate");
        DefineConflictingDelegate(module, "SecondDuplicateDelegate");

        return assembly;
    }

    private static void DefineConflictingDelegate(ModuleBuilder module, string typeName)
    {
        var type = module.DefineType(
            typeName,
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            typeof(object));

        type.AddInterfaceImplementation(typeof(IServiceTaskDelegate));
        type.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(ServiceTaskNameAttribute).GetConstructor([typeof(string)])!,
            [DuplicateName]));

        var execute = type.DefineMethod(
            nameof(IServiceTaskDelegate.ExecuteAsync),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
            typeof(ValueTask),
            [typeof(IDelegateExecution), typeof(CancellationToken)]);

        // return default(ValueTask);
        var body = execute.GetILGenerator();
        var local = body.DeclareLocal(typeof(ValueTask));
        body.Emit(OpCodes.Ldloca, local);
        body.Emit(OpCodes.Initobj, typeof(ValueTask));
        body.Emit(OpCodes.Ldloc, local);
        body.Emit(OpCodes.Ret);

        type.CreateType();
    }
}

/// <summary>计一下被调用了几次，用来验证依赖确实是从容器注入进去的。</summary>
internal sealed class CountingStore
{
    public int Calls { get; private set; }

    public void Record() => Calls++;
}

/// <summary>名字由特性声明 —— BPMN 里就该写这个名字。</summary>
[ServiceTaskName("leave.ApproveComplete")]
internal sealed class ApproveLikeDelegate : IServiceTaskDelegate
{
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}

/// <summary>没声明名字 —— 应当能按类型全名 / 短名解析到。</summary>
internal sealed class UndecoratedDelegate : IServiceTaskDelegate
{
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}

/// <summary>依赖从容器取 —— 用来证明 keyed 登记共享的是同一个 DI 构造出来的实例。</summary>
[ServiceTaskName("leave.StoreDependent")]
internal sealed class StoreDependentDelegate : IServiceTaskDelegate
{
    private readonly CountingStore _store;

    public StoreDependentDelegate(CountingStore store) => _store = store;

    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        _store.Record();
        return ValueTask.CompletedTask;
    }
}
