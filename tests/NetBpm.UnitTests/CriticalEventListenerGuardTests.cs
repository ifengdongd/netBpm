using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Behavior;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;
using NetBpm.Workflow;

// 这些用例全程跑在内存宿主上、同步完成，进程内取消没有可观测收益，
// 因此不逐处传 TestContext.Current.CancellationToken（避免把调用点淹没在样板里）。
#pragma warning disable xUnit1051

namespace NetBpm.UnitTests;

/// <summary>
/// 关键事件监听器（<see cref="CriticalEventListenerAttribute"/>）的部署期强制：
/// 实现类型声明了「失败即丢数据」，BPMN 就必须写 <c>throwOnFailure="true"</c>。
/// </summary>
/// <remarks>
/// <para>
/// 判据按<b>实现类型</b>而不是监听器名字：同一个实现可能被不同流程用 <c>class</c> 或
/// <c>delegateExpression</c> 引用，「失败了算不算丢数据」是实现的固有属性。
/// </para>
/// <para>
/// 三条边界都要钉住：关键 + 未声明 → 拒绝部署；关键 + 声明 → 通过；
/// 非关键 + 未声明 → 通过（不能误伤那些「失败也无所谓」的观测型监听器）。
/// </para>
/// </remarks>
public sealed class CriticalEventListenerGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CriticalListener_WithoutThrowOnFailure_ShouldBeRejected()
    {
        using var provider = BuildProvider();
        var engine = provider.GetRequiredService<IProcessEngine>();
        var definition = await DeployAsync(engine, "critical_missing", throwOnFailure: null, key: "critical.Listener");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceTaskDelegateGuard
                .VerifyAsync(provider, engine, definition.Id, NullLogger.Instance, Ct)
                .AsTask());

        Assert.Contains("throwOnFailure", exception.Message, StringComparison.Ordinal);
        Assert.Contains("写审批记录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalListener_WithThrowOnFailure_ShouldPass()
    {
        using var provider = BuildProvider();
        var engine = provider.GetRequiredService<IProcessEngine>();
        var definition = await DeployAsync(engine, "critical_declared", throwOnFailure: "true", key: "critical.Listener");

        await ServiceTaskDelegateGuard.VerifyAsync(provider, engine, definition.Id, NullLogger.Instance, Ct);
    }

    [Fact]
    public async Task OrdinaryListener_WithoutThrowOnFailure_ShouldPass()
    {
        using var provider = BuildProvider();
        var engine = provider.GetRequiredService<IProcessEngine>();
        var definition = await DeployAsync(engine, "ordinary_listener", throwOnFailure: null, key: "ordinary.Listener");

        await ServiceTaskDelegateGuard.VerifyAsync(provider, engine, definition.Id, NullLogger.Instance, Ct);
    }

    /// <summary>标了「关键」的监听器：失败即等于审批记录丢失。</summary>
    [CriticalEventListener(Reason = "写审批记录，失败即丢失")]
    internal sealed class CriticalRecordingListener : IEngineEventListener
    {
        public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    /// <summary>普通观测型监听器：失败不影响业务。</summary>
    internal sealed class OrdinaryRecordingListener : IEngineEventListener
    {
        public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    private static async Task<ProcessDefinition> DeployAsync(
        IProcessEngine engine,
        string processKey,
        string? throwOnFailure,
        string key)
    {
        var strict = throwOnFailure is null ? string.Empty : $" throwOnFailure=\"{throwOnFailure}\"";
        var listener = $"<activiti:eventListener events=\"TASK_COMPLETED\" delegateExpression=\"${{{key}}}\"{strict} />";

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="{{processKey}}" isExecutable="true">
                <extensionElements>
                  {{listener}}
                </extensionElements>
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await engine.DeployAsync(stream, $"{processKey}.bpmn20.xml", deploymentName: "关键监听器测试", ct: Ct);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNetBpm(options => options.UseInMemoryExecutionTree = true);

        services.AddKeyedSingleton<object>("critical.Listener", (_, _) => new CriticalRecordingListener());
        services.AddKeyedSingleton<object>("ordinary.Listener", (_, _) => new OrdinaryRecordingListener());

        return services.BuildServiceProvider();
    }
}
