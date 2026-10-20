using Dapper;
using NetBpm.Engine.Command;
using NetBpm.Engine.Migration;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 实例迁移在真实数据库上的落库语义：执行树切定义、旧待办留原因、定时器重排、
/// 历史口径（HI_PROCINST 不变）与迁移审计。
/// </summary>
/// <remarks>
/// 必须加入 <c>MySql</c> 集合：这些用例会清空引擎表，若与其它数据库用例并行，
/// 彼此会把对方的部署 / 任务删掉（表现为随机的 FK 冲突或任务数为 0）。
/// </remarks>
[Collection("MySql")]
public sealed class ProcessInstanceMigrationPersistenceTests
{
    private const string V1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="migrate_db" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_approve" />
            <userTask id="task_approve" name="审批" activiti:assignee="alice" />
            <sequenceFlow id="f2" sourceRef="task_approve" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    private const string V2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="migrate_db" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_review" />
            <userTask id="task_review" name="复核" activiti:assignee="bob" />
            <boundaryEvent id="timeout" attachedToRef="task_review">
              <timerEventDefinition>
                <timeDuration>PT1H</timeDuration>
              </timerEventDefinition>
            </boundaryEvent>
            <sequenceFlow id="f2" sourceRef="task_review" targetRef="end" />
            <sequenceFlow id="f3" sourceRef="timeout" targetRef="end_timeout" />
            <endEvent id="end" />
            <endEvent id="end_timeout" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task Migration_ShouldPersistDefinitionTasksHistoryAndAudit()
    {
        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var v1Model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(V1)));
        var v1 = await harness.Definitions.DeployAsync(v1Model, "migrate-v1.bpmn20.xml");

        var v2Model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(V2)));
        var v2 = await harness.Definitions.DeployAsync(v2Model, "migrate-v2.bpmn20.xml");

        var processInstanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(v1Model.GetMainProcess()!, v1.Id, "biz-migrate", "alice"));

        var oldTask = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));

        var plan = await harness.Engine.MigrateProcessInstanceAsync(
            new ProcessInstanceMigrationRequest(
                processInstanceId,
                v2.Id,
                new Dictionary<string, string> { ["task_approve"] = "task_review" },
                "升级到 v2",
                "ops"),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.TasksToCancel);

        var newTask = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Equal("task_review", newTask.TaskDefinitionKey);
        Assert.Equal("bob", newTask.Assignee);
        Assert.Equal(v2.Id, newTask.ProcessDefinitionId);

        // 定时器按新定义重排
        var job = Assert.Single(await harness.TimerJobs.FindDueAsync(DateTime.UtcNow.AddHours(2), 10));
        Assert.Equal("timeout", job.BoundaryEventId);

        using var session = harness.SessionFactory.Create();

        // 执行树（根 + 子执行）全部切到目标定义
        var executionDefinitions = (await session.Connection.QueryAsync<string>(
            new CommandDefinition(
                "select distinct PROC_DEF_ID_ from ACT_RU_EXECUTION where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction))).ToList();

        Assert.Equal(new[] { v2.Id }, executionDefinitions);

        // 旧待办历史留删除原因；新待办已经是 v2
        var deleteReason = await session.Connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "select DELETE_REASON_ from ACT_HI_TASKINST where ID_ = @taskId",
                new { taskId = oldTask.Id },
                session.Transaction));

        Assert.Equal("升级到 v2", deleteReason);

        // 历史实例保持启动时的定义（迁移事实由 ACT_HI_MIGRATION 记录）
        var historicDefinition = await session.Connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "select PROC_DEF_ID_ from ACT_HI_PROCINST where PROC_INST_ID_ = @processInstanceId",
                new { processInstanceId },
                session.Transaction));

        Assert.Equal(v1.Id, historicDefinition);

        // 旧活动行已收口，新活动行落 v2
        var openActivities = await session.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                select count(*) from ACT_HI_ACTINST
                where PROC_INST_ID_ = @processInstanceId and ACT_ID_ = 'task_approve' and END_TIME_ is null
                """,
                new { processInstanceId },
                session.Transaction));

        Assert.Equal(0, openActivities);

        var newActivityDefinition = await session.Connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                """
                select PROC_DEF_ID_ from ACT_HI_ACTINST
                where PROC_INST_ID_ = @processInstanceId and ACT_ID_ = 'task_review'
                order by START_TIME_ desc
                """,
                new { processInstanceId },
                session.Transaction));

        Assert.Equal(v2.Id, newActivityDefinition);

        // 迁移审计
        var audit = Assert.Single(await harness.MigrationLog.FindByProcessInstanceAsync(
            processInstanceId,
            TestContext.Current.CancellationToken));

        Assert.Equal(v1.Id, audit.SourceProcessDefinitionId);
        Assert.Equal(v2.Id, audit.TargetProcessDefinitionId);
        Assert.Equal("ops", audit.UserId);
        Assert.Equal("升级到 v2", audit.Reason);
        Assert.Equal("task_review", audit.ActivityMappings["task_approve"]);

        // 迁移后的实例可以正常办结，且定时作业被清理
        await harness.Engine.CompleteTaskAsync(newTask.Id, null, null, TestContext.Current.CancellationToken);

        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Equal(0, await harness.TimerJobs.CountAsync());
    }

    [Fact]
    public async Task Migration_ShouldNotWriteAnything_WhenValidateOnly()
    {
        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var v1Model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(V1)));
        var v1 = await harness.Definitions.DeployAsync(v1Model, "migrate-v1.bpmn20.xml");

        var v2Model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(V2)));
        var v2 = await harness.Definitions.DeployAsync(v2Model, "migrate-v2.bpmn20.xml");

        var processInstanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(v1Model.GetMainProcess()!, v1.Id, "biz-preview", "alice"));

        var plan = await harness.Engine.ValidateMigrationAsync(
            new ProcessInstanceMigrationRequest(
                processInstanceId,
                v2.Id,
                new Dictionary<string, string> { ["task_approve"] = "task_review" }),
            TestContext.Current.CancellationToken);

        Assert.Single(plan.Activities);

        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId));
        Assert.Equal(v1.Id, task.ProcessDefinitionId);
        Assert.Empty(await harness.MigrationLog.FindByProcessInstanceAsync(processInstanceId));
    }
}
