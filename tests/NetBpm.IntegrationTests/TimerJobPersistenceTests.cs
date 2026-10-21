using Dapper;
using NetBpm.Bpmn.Model;
using NetBpm.Engine.Command;
using NetBpm.Engine.Jobs;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 定时作业的持久化与真实数据库语义：写入 <c>ACT_RU_TIMER_JOB</c>、到点抢占、
/// 取消任务并把执行推进到边界分支。
/// </summary>
/// <remarks>
/// 必须加入 <c>MySql</c> 集合：这些用例会清空引擎表，若与其它数据库用例并行，
/// 彼此会把对方的部署 / 任务删掉（表现为随机的 FK 冲突或任务数为 0）。
/// </remarks>
[Collection("MySql")]
public sealed class TimerJobPersistenceTests
{
    private const string TimerProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="timer_db" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <userTask id="approve" name="审批" activiti:assignee="alice" />
            <boundaryEvent id="timeout" attachedToRef="approve">
              <timerEventDefinition>
                <timeDuration>PT2H</timeDuration>
              </timerEventDefinition>
            </boundaryEvent>
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end_ok" />
            <sequenceFlow id="f3" sourceRef="timeout" targetRef="end_timeout" />
            <endEvent id="end_ok" />
            <endEvent id="end_timeout" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task TimerJob_ShouldPersistAndFire_OnRealDatabase()
    {
        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TimerProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "timer-process.bpmn20.xml");

        var processInstanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-1", "alice"));

        var jobs = await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(3), 10);

        var job = Assert.Single(jobs);
        Assert.Equal(processInstanceId, job.ProcessInstanceId);
        Assert.Equal("timeout", job.BoundaryEventId);
        Assert.Equal("approve", job.ActivityId);

        var fired = await harness.Engine.TriggerDueTimersAsync(DateTime.UtcNow.AddHours(3), 10);

        Assert.Equal(1, fired);
        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Null(await harness.Executions.FindByIdAsync(processInstanceId));
        Assert.Equal(0, await harness.TimerJobs.CountAsync());

        // 历史：任务被取消（delete reason）、流程实例被正常结束
        using var session = harness.SessionFactory.Create();

        var taskDeleteReason = await session.Connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "select DELETE_REASON_ from ACT_HI_TASKINST where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction));

        Assert.Equal(TriggerTimerJobCommand.DeleteReason, taskDeleteReason);

        var instanceEnd = await session.Connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition(
                "select END_TIME_ from ACT_HI_PROCINST where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction));

        Assert.NotNull(instanceEnd);
    }

    [Fact]
    public async Task TimerJob_ShouldEnterDeadLetter_AndBeRetryable()
    {
        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TimerProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "timer-dead-letter.bpmn20.xml");

        await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-dl", "alice"));

        var job = Assert.Single(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(3), 10));

        // 连续失败 3 次（默认上限）→ 进死信，不再参与到期轮询
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var remaining = Math.Max(0, job.Retries - 1);
            await harness.TimerJobs.MarkFailureAsync(
                job.Id,
                $"boom-{attempt}",
                DateTime.UtcNow,
                remaining == 0 ? DateTime.UtcNow : DateTime.UtcNow.AddMinutes(attempt));
        }

        Assert.Empty(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(3), 10));

        var dead = Assert.Single(await harness.TimerJobs.FindDeadLettersAsync(10));
        Assert.True(dead.DeadLetter);
        Assert.Equal(0, dead.Retries);
        Assert.Equal("boom-3", dead.ExceptionMessage);

        // 人工重试：复位重试次数、立即到期
        Assert.True(await harness.TimerJobs.RetryDeadLetterAsync(job.Id, DateTime.UtcNow));

        var revived = Assert.Single(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow, 10));
        Assert.False(revived.DeadLetter);
        Assert.Equal(TimerJobRetry.DefaultMaxRetries, revived.Retries);
    }

    [Fact]
    public async Task SuspendedInstanceJob_ShouldNotOccupyDueBatch()
    {
        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TimerProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "timer-suspended.bpmn20.xml");

        var processInstanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-suspend", "alice"));

        await harness.Engine.SuspendProcessInstanceAsync(processInstanceId);

        // 挂起实例的作业被 SQL 过滤掉，不会反复占据批次头部
        Assert.Empty(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(3), 10));
        Assert.Equal(0, await harness.Engine.TriggerDueTimersAsync(DateTime.UtcNow.AddHours(3), 10));

        await harness.Engine.ActivateProcessInstanceAsync(processInstanceId);

        Assert.Single(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(3), 10));
    }

    [Fact]
    public async Task TerminateEndEvent_ShouldPersistDeleteReason_OnRealDatabase()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="terminate_db" isExecutable="true">
                <startEvent id="start" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <userTask id="approve" activiti:assignee="alice" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="term" />
                <endEvent id="term">
                  <terminateEventDefinition />
                </endEvent>
              </process>
            </definitions>
            """;

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));

        var definition = await harness.Definitions.DeployAsync(model, "terminate-process.bpmn20.xml");

        var processInstanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(model.GetMainProcess()!, definition.Id, "biz-2", "alice"));

        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        await harness.Executor.ExecuteAsync(new CompleteTaskCommand(task.Id));

        using var session = harness.SessionFactory.Create();

        IDictionary<string, object?> row = await session.Connection.QuerySingleAsync(
            new CommandDefinition(
                "select DELETE_REASON_, END_ACT_ID_ from ACT_HI_PROCINST where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction));

        Assert.Equal("terminate end event", row["DELETE_REASON_"] as string);
        Assert.Equal("term", row["END_ACT_ID_"] as string);
    }
}
