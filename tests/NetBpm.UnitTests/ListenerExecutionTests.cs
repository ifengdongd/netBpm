using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Behavior;
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
