using Dapper;
using NetBpm.Engine.Command;
using NetBpm.Engine.Events;
using NetBpm.Engine.Services;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 异步作业与 outbox 的真库语义：
/// 作业与业务变更同事务、JobExecutor 恢复执行、outbox 事件可查询且不阻塞主流程。
/// </summary>
[Collection("MySql")]
public sealed class AsyncJobAndOutboxPersistenceTests
{
    private const string AsyncBeforeProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="async_before" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="异步审批" activiti:assignee="alice"
                      activiti:asyncBefore="true" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private const string AsyncAfterProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="async_after" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="异步离开" activiti:assignee="alice"
                      activiti:asyncAfter="true" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task AsyncBefore_ShouldPersistJob_AndCreateTaskAfterJobRuns()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AsyncBeforeProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "async-before.bpmn20.xml");

        var instanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-async-before", "alice"));

        // 业务命令已提交：任务还没创建，作业已落库（同事务）
        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(instanceId));

        var job = Assert.Single(await harness.AsyncJobs.FindDueAsync(DateTime.UtcNow.AddMinutes(1), 10));
        Assert.Equal("approve", job.ActivityId);
        Assert.Equal("before", job.PhaseValue);

        Assert.Equal(1, await harness.Engine.ExecuteDueAsyncJobsAsync(DateTime.UtcNow.AddMinutes(1), 10));

        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(instanceId));
        Assert.Equal("alice", task.Assignee);
        Assert.Equal(0, await harness.AsyncJobs.CountAsync());
    }

    [Fact]
    public async Task AsyncAfter_ShouldDeferLeavingUntilJobRuns()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AsyncAfterProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "async-after.bpmn20.xml");

        var instanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-async-after", "alice"));

        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(instanceId));

        // 完成任务：任务删除与作业落库同事务；流程还没离开节点
        await harness.Executor.ExecuteAsync(new CompleteTaskCommand(task.Id));

        var job = Assert.Single(await harness.AsyncJobs.FindDueAsync(DateTime.UtcNow.AddMinutes(1), 10));
        Assert.Equal("after", job.PhaseValue);

        Assert.NotEmpty(await harness.Executions.FindAllByProcessInstanceIdAsync(instanceId));

        // 作业执行后：活动结束写入历史，流程结束
        Assert.Equal(1, await harness.Engine.ExecuteDueAsyncJobsAsync(DateTime.UtcNow.AddMinutes(1), 10));

        using (var session = harness.SessionFactory.Create())
        {
            var endTime = session.Connection.ExecuteScalar<DateTime?>(
                "select END_TIME_ from ACT_HI_ACTINST where PROC_INST_ID_ = @id and ACT_ID_ = 'approve'",
                new { id = instanceId });

            Assert.NotNull(endTime);
        }

        Assert.Empty(await harness.AsyncJobs.FindDueAsync(DateTime.UtcNow.AddDays(1), 10));
    }

    [Fact]
    public async Task Outbox_ShouldStoreEventsTransactionally()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AsyncBeforeProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "outbox.bpmn20.xml");

        var instanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-outbox", "alice"));

        // 用户任务是 asyncBefore：任务创建事件由作业执行时产生
        await harness.Engine.ExecuteDueAsyncJobsAsync(DateTime.UtcNow.AddMinutes(1), 10);

        var events = await harness.Outbox.FindAsync(
            new OutboxQuery(ProcessInstanceId: instanceId, MaxResults: 100));

        Assert.Contains(events, item => item.EventType == EngineEvents.ProcessStarted);
        Assert.Contains(events, item => item.EventType == EngineEvents.ActivityStarted);
        Assert.Contains(events, item => item.EventType == EngineEvents.TaskCreated);

        Assert.All(events, item =>
        {
            Assert.False(item.Published);
            Assert.NotNull(item.Payload);
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
        });

        // 至少一次：重复写入同一 id 不应产生第二行（订阅方幂等键）
        var first = events[0];
        await harness.Outbox.EnqueueAsync(first);

        Assert.Equal(events.Count, await harness.Outbox.CountAsync(
            new OutboxQuery(ProcessInstanceId: instanceId)));

        // 发布成功后从待发布队列消失
        Assert.True(await harness.Outbox.MarkPublishedAsync(first.Id, DateTime.UtcNow));

        var pending = await harness.Outbox.FindPendingAsync(DateTime.UtcNow.AddMinutes(1), 100);
        Assert.DoesNotContain(pending, item => item.Id == first.Id);
    }
}
