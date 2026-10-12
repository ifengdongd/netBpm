namespace NetBpm.UnitTests;

/// <summary>
/// 历史写入的<b>时机</b>（不是行内容 —— 那部分由真库集成测试覆盖）。
/// <para>
/// 为什么单独测时机：Activiti 在「活动进出、任务创建/指派/结束、变量增改、关系创建、实例起止」
/// 这些点上都会调 <c>HistoryManager</c>；漏掉或多出一次调用，真库里的 <c>ACT_HI_ACTINST</c>
/// 就会多行/少行，而这在纯行内容断言里不一定看得出来（比如两行同名活动）。
/// </para>
/// </summary>
public sealed class HistoryRecordingTests
{
    private static readonly string[] ExpectedActivityStarts =
    [
        "ActivityStart:start",
        "ActivityStart:task",
        "ActivityStart:end",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SequentialProcess_ShouldRecordLifecycleInExpectedOrder()
    {
        var history = new RecordingHistoryManager();
        var engine = TestEngine.Create(history);

        await engine.StartProcessAsync(TestEngine.SequentialProcess("work"));

        // 首尾必须是流程实例起止，且 START_ACT_ID_ 取开始事件
        Assert.Equal("ProcessInstanceStart:start", history.Calls[0]);
        Assert.StartsWith("ProcessInstanceEnd:", history.Calls[^1], StringComparison.Ordinal);

        // 三个节点各记录一次活动开始（顺序即执行顺序）
        Assert.Equal(
            ExpectedActivityStarts,
            history.Calls.Where(call => call.StartsWith("ActivityStart:", StringComparison.Ordinal)));

        // 每个活动开始都必须有对应的结束
        foreach (var activityId in new[] { "start", "task", "end" })
        {
            Assert.Contains($"ActivityEnd:{activityId}", history.Calls);
        }

        // 结束节点 id 落到 END_ACT_ID_
        Assert.Equal("ProcessInstanceEnd:end", history.Calls[^1]);
    }

    [Fact]
    public async Task UserTask_ShouldRecordTaskAndIdentityLinkHistory()
    {
        var history = new RecordingHistoryManager();
        var engine = TestEngine.Create(history);

        var processInstanceId = await engine.StartProcessAsync(UserTaskProcess());
        var task = Assert.Single(engine.TaskManager.Tasks);

        // 任务创建后立即回填 ACT_HI_ACTINST.TASK_ID_
        Assert.Contains($"TaskCreated:{task.Id}", history.Calls);
        Assert.Contains($"TaskId:{task.Id}", history.Calls);

        // 指派与候选关系：办理人回填 + 身份关系（kermit/alice/bob 为 participant，组为 candidate）
        Assert.Contains($"TaskAssignment:{task.Id}:kermit", history.Calls);
        Assert.Contains("IdentityLinkCreated:participant:kermit", history.Calls);
        Assert.Contains("IdentityLinkCreated:candidate:alice", history.Calls);
        Assert.Contains("IdentityLinkCreated:candidate:managers", history.Calls);

        // 完成 → 历史任务行收口
        await engine.CompleteTaskAsync(task.Id);

        Assert.Contains($"TaskEnd:{task.Id}", history.Calls);

        // 流程实例结束，且 END_ACT_ID_ 取结束事件
        Assert.Equal("ProcessInstanceEnd:end", history.Calls[^1]);
        Assert.NotNull(processInstanceId);
    }

    [Fact]
    public async Task Variables_ShouldNotRecordHistoryInInMemoryHost()
    {
        // ⚠️ 已登记的行为差异：变量历史由「变量实例仓储」写入（Activiti 在
        // VariableScopeImpl.createVariableInstance 里 insert 变量后紧接着 recordVariableCreate），
        // 而内存宿主没有变量仓储（VariableInstanceManager == null），因此不写变量历史。
        // 生产上历史必须搭配数据库宿主（NetBpmOptions 会拒绝「内存宿主 + EnableHistory」），
        // 真实行为由 MySqlVariableAndHistoryTests 覆盖。
        var history = new RecordingHistoryManager();
        var engine = TestEngine.Create(history);

        await engine.StartProcessAsync(
            TestEngine.UserTaskThenServiceProcess("after"),
            new Dictionary<string, object?> { ["approved"] = false });

        var task = Assert.Single(engine.TaskManager.Tasks);
        await engine.CompleteTaskAsync(task.Id, new Dictionary<string, object?> { ["approved"] = true });

        Assert.DoesNotContain(history.Calls, call => call.StartsWith("Variable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkippedUserTask_ShouldCreateThenDeleteTaskWithoutAssignment()
    {
        // 跳过的用户任务仍然「先建后删」（Activiti: insert(task) → 判定 skip → deleteTask → leave），
        // 但不做办理关系分配。把它钉死是因为「建了又删」会留下两条历史记录。
        var history = new RecordingHistoryManager();
        var engine = TestEngine.Create(history);

        await engine.StartProcessAsync(
            SkipUserTaskProcess(),
            new Dictionary<string, object?> { ["skip"] = true });

        var created = Assert.Single(
            history.Calls,
            call => call.StartsWith("TaskCreated:", StringComparison.Ordinal));

        var taskId = created["TaskCreated:".Length..];

        Assert.Contains($"TaskEnd:{taskId}", history.Calls);
        Assert.DoesNotContain(history.Calls, call => call.StartsWith("TaskAssignment:", StringComparison.Ordinal));

        // 跳过意味着流程直接跑完，且不留下运行期任务
        Assert.Equal(0, engine.TaskManager.TaskCount);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    private static string UserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="history-user-task" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" activiti:candidateUsers="alice,bob" activiti:candidateGroups="managers" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string SkipUserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="history-skip" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" activiti:assignee="kermit" activiti:skipExpression="${skip}" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;
}
