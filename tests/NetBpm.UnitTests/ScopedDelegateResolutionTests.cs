using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

// 这些用例全程跑在内存宿主上、同步完成，进程内取消没有可观测收益，
// 因此不逐处传 TestContext.Current.CancellationToken（避免把调用点淹没在样板里）。
#pragma warning disable xUnit1051

namespace NetBpm.UnitTests;

/// <summary>
/// <c>AddScoped</c> 注册的服务任务委托必须能在<b>命令作用域</b>内解析，且命令结束即释放。
/// </summary>
/// <remarks>
/// <para>
/// 这条守的是平台化改造 §13.2 的 P1-7：解析器此前注入的是<b>根</b> <see cref="IServiceProvider"/>。
/// 后果有两种，取决于容器是否开作用域校验：开了（宿主在 Development 下就是这样）→ 直接
/// <c>InvalidOperationException: Cannot resolve scoped service from root provider</c>；
/// 没开 → 拿到一份「根作用域」实例被所有请求共享，委托里注入的 DbContext / 仓储就成了跨请求可变状态。
/// </para>
/// <para>
/// 断言刻意打在「<b>每次执行拿到的实例 id</b>」上，而不是「解析成功」上：只断言不抛异常的话，
/// 根容器返回同一个实例也能过 —— 那正是最危险的那种“修复”。
/// </para>
/// </remarks>
public sealed class ScopedDelegateResolutionTests
{
    private const string BpmnXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="scoped_process" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="svc1" activiti:delegateExpression="${scoped.counter}" />
            <serviceTask id="svc2" activiti:delegateExpression="${scoped.counter}" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="svc1" />
            <sequenceFlow id="f2" sourceRef="svc1" targetRef="svc2" />
            <sequenceFlow id="f3" sourceRef="svc2" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>Scoped 依赖：记下自己的 id 与释放时刻（单例 tracker 收口）。</summary>
    private sealed class ScopedProbe : IDisposable
    {
        private readonly ProbeTracker _tracker;

        public ScopedProbe(ProbeTracker tracker)
        {
            _tracker = tracker;
            Id = Guid.NewGuid();
            _tracker.Created.Enqueue(Id);
        }

        public Guid Id { get; }

        public void Dispose() => _tracker.Disposed.Enqueue(Id);
    }

    /// <summary>跨命令共享的观察者（单例）。</summary>
    private sealed class ProbeTracker
    {
        public ConcurrentQueue<Guid> Created { get; } = new();

        public ConcurrentQueue<Guid> Disposed { get; } = new();

        /// <summary>每次执行时用的 probe id，按执行顺序。</summary>
        public List<Guid> Executed { get; } = [];

        public void Record(Guid probeId) => Executed.Add(probeId);
    }

    /// <summary>Scoped 服务任务委托：自己也是 Scoped，依赖同样 Scoped 的 probe。</summary>
    private sealed class ScopedCountingDelegate : IServiceTaskDelegate
    {
        private readonly ScopedProbe _probe;
        private readonly ProbeTracker _tracker;

        public ScopedCountingDelegate(ScopedProbe probe, ProbeTracker tracker)
        {
            _probe = probe;
            _tracker = tracker;
        }

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
        {
            _tracker.Record(_probe.Id);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ScopedDelegate_ShouldResolvePerCommand_AndBeDisposedWithCommand()
    {
        var tracker = new ProbeTracker();

        using var provider = BuildProvider(tracker);
        var engine = provider.GetRequiredService<IProcessEngine>();

        await using var xml = new MemoryStream(Encoding.UTF8.GetBytes(BpmnXml));
        var definition = await engine.DeployAsync(xml, "scoped-delegate.bpmn20.xml", "作用域委托", default);

        // 两条命令；每条命令内跑两个 serviceTask
        await engine.StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-A", variables: null, ct: default);
        await engine.StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-B", variables: null, ct: default);

        var executed = tracker.Executed;

        // 只统计「真的被执行」的 4 次（部署期校验可能额外解析过实例，那些不进 Executed）
        Assert.Equal(4, executed.Count);

        // 同一条命令内的两次执行共用同一个作用域 → 同一个 probe
        Assert.Equal(executed[0], executed[1]);
        Assert.Equal(executed[2], executed[3]);

        // 跨命令必须换新实例：若解析仍走根容器，这里四条会全等
        Assert.NotEqual(executed[0], executed[2]);

        // 命令结束即释放：两次命令的 probe 都已 Dispose，没有泄漏
        var disposed = tracker.Disposed.ToArray();
        Assert.Contains(executed[0], disposed);
        Assert.Contains(executed[2], disposed);
    }

    private static ServiceProvider BuildProvider(ProbeTracker tracker)
    {
        var services = new ServiceCollection();
        services.AddNetBpm(options => options.UseInMemoryExecutionTree = true);

        services.AddSingleton(tracker);
        services.AddScoped<ScopedProbe>();
        services.AddScoped<ScopedCountingDelegate>();

        // delegateExpression 走「按名字取 keyed 服务」这条路径
        services.AddKeyedScoped<object>(
            "scoped.counter",
            (provider, _) => provider.GetRequiredService<ScopedCountingDelegate>());

        // ValidateScopes = true：与宿主在 Development 下的行为对齐，
        // 让「从根容器解析 scoped」这类回退直接炸出来，而不是悄悄返回根作用域实例。
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
