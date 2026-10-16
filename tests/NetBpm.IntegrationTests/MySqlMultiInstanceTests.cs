namespace NetBpm.IntegrationTests;

/// <summary>
/// 多实例用户任务（会签 / 串签，任务清单 B1.2）的库级验收。
/// <para>
/// 核心是<b>跨命令</b>：实例执行、循环变量（ACT_RU_VARIABLE）都要在下一个命令里恢复，
/// 完成任务后由 TriggerExecutionOperation 推进剩余实例 —— 这是多实例与 P7 用户任务
/// 同一类的「等待型节点跨命令」问题。集合变量（List）不支持落库，这里用 loopCardinality。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class MySqlMultiInstanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ParallelCountersign_ShouldSurviveAcrossCommands()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            ParallelCountersignProcess(),
            ct: Ct);

        // 命令 1 提交后：3 个任务同时在库（挂在 3 个实例执行上），循环变量已落库
        var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
        Assert.Equal(3, tasks.Count);

        using (var session = harness.SessionFactory.Create())
        {
            var variables = RowValues.Query(session, "select NAME_, TYPE_, LONG_ from ACT_RU_VARIABLE");
            var byName = variables.ToDictionary(row => RowValues.Text(row, "NAME_")!, StringComparer.Ordinal);

            Assert.Equal("integer", RowValues.Text(byName["nrOfInstances"], "TYPE_"));
            Assert.Equal(3L, RowValues.Long(byName["nrOfInstances"], "LONG_"));
            Assert.Equal(0L, RowValues.Long(byName["nrOfCompletedInstances"], "LONG_"));
        }

        // 命令 2 / 3 / 4：逐个完成任务（每个都是独立的跨命令事务），待办数逐轮递减
        for (var round = 0; round < 3; round++)
        {
            var remaining = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
            Assert.Equal(3 - round, remaining.Count);

            await harness.CompleteTaskAsync(remaining[0].Id, ct: Ct);
        }

        // 全部完成 → 实例执行清理、主执行离开、流程结束
        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_VARIABLE"));
    }

    [Fact]
    public async Task SequentialCountersign_ShouldCreateTasksOneByOneAcrossCommands()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            SequentialCountersignProcess(),
            ct: Ct);

        // 串签：任何时刻库里的待办只有 1 个
        for (var round = 1; round <= 3; round++)
        {
            var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
            var task = Assert.Single(tasks);

            await harness.CompleteTaskAsync(task.Id, ct: Ct);

            // 完成计数随命令推进（真库读回）
            using var session = harness.SessionFactory.Create();
            var rows = RowValues.Query(
                session,
                "select LONG_ from ACT_RU_VARIABLE where NAME_ = 'nrOfCompletedInstances'");

            if (round < 3)
            {
                Assert.Single(rows);
                Assert.Equal(round, RowValues.Long(Assert.Single(rows), "LONG_"));
            }
        }

        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
    }

    /// <summary>并行会签：3 个实例（cardinality），静态办理人，结束于单一出流。</summary>
    private static string ParallelCountersignProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="mi-parallel-db" name="并行会签（库级验证）" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="会签" activiti:assignee="kermit">
              <multiInstanceLoopCharacteristics isSequential="false">
                <loopCardinality>3</loopCardinality>
              </multiInstanceLoopCharacteristics>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>串签：3 个实例逐个办理。</summary>
    private static string SequentialCountersignProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="mi-sequential-db" name="串签（库级验证）" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="串签" activiti:assignee="kermit">
              <multiInstanceLoopCharacteristics isSequential="true">
                <loopCardinality>3</loopCardinality>
              </multiInstanceLoopCharacteristics>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;
}
