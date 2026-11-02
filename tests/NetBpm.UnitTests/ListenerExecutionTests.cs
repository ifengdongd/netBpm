using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Execution;
using NetBpm.Runtime;

// 这些用例全程跑在内存宿主上、同步完成，进程内取消没有可观测收益，
// 因此不逐处传 TestContext.Current.CancellationToken（避免把调用点淹没在样板里）。
#pragma warning disable xUnit1051

namespace NetBpm.UnitTests;

/// <summary>
/// 监听器执行回归：执行监听器（start / take / end）与流程级事件监听器
/// （TASK_CREATED / TASK_COMPLETED / PROCESS_COMPLETED）在引擎里真的被触发、
/// 且各自恰好一次。对应 Activiti 的 <c>ExecutionListener</c> / <c>ActivitiEventListener</c>。
/// </summary>
public sealed class ListenerExecutionTests
{
    /// <summary>记录监听器回调序列（断言的数据源）。</summary>
    internal sealed class RecordingListener : IExecutionListener, IEngineEventListener
    {
        public List<string> Events { get; } = [];

        public ValueTask NotifyAsync(IDelegateExecution execution, CancellationToken ct = default)
        {
            Events.Add($"execution:{execution.EventName}:{execution.CurrentActivityId ?? execution.CurrentFlowElement?.Id}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default)
        {
            Events.Add($"event:{engineEvent.Type}:{engineEvent.EntityId}");
            return ValueTask.CompletedTask;
        }
    }

    private const string BpmnXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="listener_process" isExecutable="true">
            <extensionElements>
              <activiti:eventListener events="TASK_COMPLETED,PROCESS_COMPLETED" delegateExpression="${rec.Listener}" />
            </extensionElements>
            <startEvent id="start" />
            <userTask id="task" name="审批" activiti:assignee="kermit">
              <extensionElements>
                <activiti:executionListener event="start" delegateExpression="${rec.Listener}" />
                <activiti:executionListener event="end" delegateExpression="${rec.Listener}" />
              </extensionElements>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task">
              <extensionElements>
                <activiti:executionListener event="take" delegateExpression="${rec.Listener}" />
              </extensionElements>
            </sequenceFlow>
            <sequenceFlow id="f2" sourceRef="task" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task Listeners_ShouldFireExactlyOnceAtEachHook()
    {
        var listener = new RecordingListener();

        using var provider = BuildProvider(listener);
        var engine = provider.GetRequiredService<IProcessEngine>();

        await using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(BpmnXml));
        var definition = await engine.DeployAsync(xml, "listeners.bpmn20.xml", "监听器测试", default).ConfigureAwait(false);

        var instanceId = await engine
            .StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-1", variables: null, ct: default)
            .ConfigureAwait(false);

        // 发起阶段：f1 的 take → task 的 start（TASK_CREATED 未被订阅，不通知）
        Assert.Equal(
            [
                "execution:take:f1",
                "execution:start:task",
            ],
            listener.Events);

        var taskId = Assert.Single(await engine.FindTasksAsync(new TaskQuery(Assignee: "kermit"), default).ConfigureAwait(false)).Id;

        await engine.CompleteTaskAsync(taskId, null, default).ConfigureAwait(false);

        // 完成阶段：TASK_COMPLETED 分发（删除任务前，实体 id 为任务 id）→ task 的 end → PROCESS_COMPLETED
        Assert.Equal(5, listener.Events.Count);
        Assert.Equal("execution:take:f1", listener.Events[0]);
        Assert.Equal("execution:start:task", listener.Events[1]);
        Assert.StartsWith("event:TASK_COMPLETED:", listener.Events[2]);
        Assert.Equal("execution:end:task", listener.Events[3]);
        Assert.Equal($"event:PROCESS_COMPLETED:" + instanceId, listener.Events[4]);
    }

    /// <summary>
    /// 事件监听器抛错、且 <c>throwOnFailure</c> 未声明：流程照走，但失败必须进指标。
    /// </summary>
    /// <remarks>
    /// 回归：这条路径原先只写一条 warning 日志，若宿主没注入 <c>ILogger</c>
    /// （<c>services.Logger?.LogWarning(...)</c> 的 <c>?.</c>）就<b>彻底静默</b> ——
    /// 业务侧「写审批记录」失败，流程依然推进，且事后查不到任何痕迹。
    /// 现在改为：计入 <c>netbpm.listener.failure</c>；没有日志设施时直接把异常抛出去。
    /// </remarks>
    [Fact]
    public async Task EventListenerFailure_ShouldContinueAndBeCounted_WhenThrowOnFailureOmitted()
    {
        var observed = new ListenerFailureProbe();

        using var provider = BuildThrowingProvider(withLogging: true);
        var engine = provider.GetRequiredService<IProcessEngine>();

        await using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(FailingBpmnXml(string.Empty)));
        var definition = await engine.DeployAsync(xml, "failing-listener.bpmn20.xml", "失败监听器", default);

        // 不抛：默认语义是「记录并继续」（对齐 Activiti 的 isFailOnException() = false）
        var instanceId = await engine
            .StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-F", variables: null, ct: default);

        Assert.False(string.IsNullOrEmpty(instanceId));
        Assert.True(observed.Count >= 1, $"期望 netbpm.listener.failure 至少计数 1 次，实际 {observed.Count}。");
    }

    /// <summary>声明 <c>throwOnFailure="true"</c> 时，监听器失败必须让整条命令失败。</summary>
    [Fact]
    public async Task EventListenerFailure_ShouldFailCommand_WhenThrowOnFailureTrue()
    {
        using var provider = BuildThrowingProvider(withLogging: true);
        var engine = provider.GetRequiredService<IProcessEngine>();

        await using var xml = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(FailingBpmnXml("throwOnFailure=\"true\"")));
        var definition = await engine.DeployAsync(xml, "failing-listener-strict.bpmn20.xml", "严格监听器", default);

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-S", variables: null, ct: default).AsTask());

        Assert.Contains("监听器写失败", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 没有日志设施时不能静默：异常必须抛出来。
    /// </summary>
    /// <remarks>
    /// 原先的写法是 <c>services.Logger?.LogWarning(...)</c> —— <c>?.</c> 一短路，异常就被吃干抹净，
    /// 与 <c>IEngineServices.Logger</c> 注释承诺的「未注入时响亮抛出」正好相反。
    /// </remarks>
    [Fact]
    public async Task EventListenerFailure_ShouldThrow_WhenNoLoggerConfigured()
    {
        using var provider = BuildThrowingProvider(withLogging: false);
        var engine = provider.GetRequiredService<IProcessEngine>();

        await using var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(FailingBpmnXml(string.Empty)));
        var definition = await engine.DeployAsync(xml, "failing-no-logger.bpmn20.xml", "无日志监听器", default);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.StartProcessInstanceAsync(definition.Id, businessKey: "BIZ-N", variables: null, ct: default).AsTask());
    }

    /// <summary>抛错的事件监听器（模拟「业务侧写记录失败」）。</summary>
    internal sealed class ThrowingListener : IEngineEventListener
    {
        public ValueTask OnEventAsync(WorkflowEngineEvent engineEvent, CancellationToken ct = default) =>
            throw new InvalidOperationException($"监听器写失败：{engineEvent.Type}");
    }

    /// <summary>捕获 <c>netbpm.listener.failure</c> 的计数（只订阅本条指标，不碰进程级共享状态）。</summary>
    private sealed class ListenerFailureProbe : IDisposable
    {
        private readonly MeterListener _listener;
        private long _count;

        public ListenerFailureProbe()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == EngineDiagnostics.MeterName
                        && instrument.Name == "netbpm.listener.failure")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == "netbpm.listener.failure")
                {
                    Interlocked.Add(ref _count, measurement);
                }
            });

            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    private static string FailingBpmnXml(string throwOnFailureAttribute) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="failing_listener_process" isExecutable="true">
            <extensionElements>
              <activiti:eventListener events="PROCESS_STARTED" delegateExpression="${boom.Listener}" {{throwOnFailureAttribute}} />
            </extensionElements>
            <startEvent id="start" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
          </process>
        </definitions>
        """;

    private static ServiceProvider BuildThrowingProvider(bool withLogging)
    {
        var services = new ServiceCollection();

        if (withLogging)
        {
            // 有日志设施时走「写 warning + 继续」；没有时应当抛出（见 IEngineServices.Logger 的契约）
            services.AddLogging();
        }

        services.AddNetBpm(options => options.UseInMemoryExecutionTree = true);
        services.AddKeyedSingleton<object>(
            "boom.Listener",
            (_, _) => new ThrowingListener());

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildProvider(RecordingListener listener)
    {
        var services = new ServiceCollection();
        services.AddNetBpm(options => options.UseInMemoryExecutionTree = true);

        // 记录监听器：具体类型单例 + keyed 别名（delegateExpression="${rec.Listener}" 的解析路径）
        services.RemoveAll<RecordingListener>();
        services.AddSingleton(listener);
        services.AddKeyedSingleton<object>("rec.Listener", (provider, _) => provider.GetRequiredService<RecordingListener>());

        return services.BuildServiceProvider();
    }
}
