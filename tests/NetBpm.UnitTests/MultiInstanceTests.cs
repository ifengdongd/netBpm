using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;

namespace NetBpm.UnitTests;

/// <summary>
/// 多实例用户任务（会签 / 串签，任务清单 B1.2）的运行期与部署期语义。
/// <para>
/// 运行期断言三个层面：任务数量与顺序（待办查询）、循环变量（后接捕获委托观察）、
/// 执行树收口（实例结束后 <c>ExecutionCount == 0</c>）。
/// </para>
/// </summary>
public sealed class MultiInstanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ─────────────────────────── 串行（串签）───────────────────────────

    [Fact]
    public async Task Sequential_ShouldCreateOneTaskAtATime()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            SequentialCardinalityProcess(loopCounterCapture: true),
            ct: Ct);

        // 第 1 个任务
        var tasks = await CurrentTasksAsync(engine, processInstanceId);
        var first = Assert.Single(tasks);

        await engine.CompleteTaskAsync(first.Id, ct: Ct);

        // 第 2 个任务（同一节点再次等待）
        tasks = await CurrentTasksAsync(engine, processInstanceId);
        var second = Assert.Single(tasks);
        Assert.NotEqual(first.Id, second.Id);

        await engine.CompleteTaskAsync(second.Id, ct: Ct);
        tasks = await CurrentTasksAsync(engine, processInstanceId);
        Assert.Single(tasks);

        // 全部完成 → 流程实例结束、执行树清空
        await engine.CompleteTaskAsync((await CurrentTasksAsync(engine, processInstanceId)).Single().Id, ct: Ct);

        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Empty(await CurrentTasksAsync(engine, processInstanceId));
    }

    [Fact]
    public async Task Sequential_ShouldExposeLoopVariables()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            SequentialCardinalityProcess(loopCounterCapture: true),
            ct: Ct);

        // 完成全部 3 个实例，随后的捕获委托观察循环变量终值
        while ((await CurrentTasksAsync(engine, processInstanceId)).FirstOrDefault() is { } task)
        {
            await engine.CompleteTaskAsync(task.Id, ct: Ct);
        }

        var captured = engine.DelegateResolver.CapturedVariables.Single();

        Assert.Equal(3, captured["nrOfInstances"]);
        Assert.Equal(3, captured["nrOfCompletedInstances"]);
        Assert.Equal(0, captured["nrOfActiveInstances"]);
        Assert.Equal(3, captured["loopCounter"]);
    }

    [Fact]
    public async Task Sequential_WithCollection_ShouldAssignElementVariablePerInstance()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            SequentialCollectionProcess(),
            new Dictionary<string, object?>
            {
                ["approvers"] = new List<string> { "alice", "bob", "carol" },
            },
            Ct);

        // 每个实例的 assignee = 集合元素（elementVariable="approver"，assignee="${approver}"）
        var seen = new List<string?>();

        while ((await CurrentTasksAsync(engine, processInstanceId)).FirstOrDefault() is { } task)
        {
            seen.Add(task.Assignee);
            await engine.CompleteTaskAsync(task.Id, ct: Ct);
        }

        Assert.Equal(["alice", "bob", "carol"], seen);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    // ─────────────────────────── 并行（会签）───────────────────────────

    [Fact]
    public async Task Parallel_ShouldCreateAllTasksAtOnce()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            ParallelCollectionProcess(),
            new Dictionary<string, object?>
            {
                ["approvers"] = new List<string> { "alice", "bob", "carol" },
            },
            Ct);

        // 3 个任务同时存在，办理人各为集合元素
        var tasks = await CurrentTasksAsync(engine, processInstanceId);

        Assert.Equal(3, tasks.Count);
        Assert.Equal(
            ["alice", "bob", "carol"],
            tasks.Select(task => task.Assignee).OrderBy(assignee => assignee, StringComparer.Ordinal));

        // 逐个完成 → 实例结束
        foreach (var task in tasks)
        {
            await engine.CompleteTaskAsync(task.Id, ct: Ct);
        }

        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task Parallel_ShouldKeepWaitingUntilAllInstancesCompleted()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            ParallelCollectionProcess(),
            new Dictionary<string, object?>
            {
                ["approvers"] = new List<string> { "alice", "bob", "carol" },
            },
            Ct);

        var tasks = await CurrentTasksAsync(engine, processInstanceId);

        // 完成前 2 个：节点必须继续等待（不能提前推进）
        await engine.CompleteTaskAsync(tasks[0].Id, ct: Ct);
        await engine.CompleteTaskAsync(tasks[1].Id, ct: Ct);

        Assert.Single(await CurrentTasksAsync(engine, processInstanceId));

        // 执行树仍未收口：流程实例根执行 + 主执行 + 3 个实例执行
        Assert.Equal(5, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task Parallel_CompletionCondition_ShouldFinishEarly()
    {
        // 「2 人通过即通过」的会签规则：完成条件 ${nrOfCompletedInstances == 2}
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            ParallelCollectionProcess(completionCondition: "${nrOfCompletedInstances == 2}"),
            new Dictionary<string, object?>
            {
                ["approvers"] = new List<string> { "alice", "bob", "carol" },
            },
            Ct);

        var tasks = await CurrentTasksAsync(engine, processInstanceId);
        Assert.Equal(3, tasks.Count);

        await engine.CompleteTaskAsync(tasks[0].Id, ct: Ct);
        await engine.CompleteTaskAsync(tasks[1].Id, ct: Ct);

        // 剩余实例被取消（任务删除），流程继续推进
        Assert.Empty(await CurrentTasksAsync(engine, processInstanceId));
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    // ─────────────────────────── 部署期校验 ───────────────────────────

    [Fact]
    public void Validator_ValidMultiInstanceUserTask_ShouldPass()
    {
        var errors = Validate(SequentialCardinalityProcess());

        Assert.DoesNotContain(errors, error => error.Rule == "multi-instance");
    }

    [Fact]
    public void Validator_MultiInstanceOnServiceTask_ShouldBeRejected()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="mi-service" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="work" activiti:delegateExpression="work">
                  <multiInstanceLoopCharacteristics>
                    <loopCardinality>3</loopCardinality>
                  </multiInstanceLoopCharacteristics>
                </serviceTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
                <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validate(xml);

        var error = Assert.Single(errors);
        Assert.Equal("multi-instance", error.Rule);
        Assert.Equal("work", error.ElementId);
    }

    [Fact]
    public void Validator_MultiInstanceWithoutSource_ShouldBeRejected()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="mi-empty" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve">
                  <multiInstanceLoopCharacteristics isSequential="true" />
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validate(xml);

        var error = Assert.Single(errors);
        Assert.Equal("multi-instance", error.Rule);
        Assert.Contains("loopCardinality", error.Message);
    }

    [Fact]
    public void Validator_MultiInstanceWithSkipExpression_ShouldBeRejected()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="mi-skip" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" activiti:skipExpression="${skip}">
                  <multiInstanceLoopCharacteristics>
                    <loopCardinality>3</loopCardinality>
                  </multiInstanceLoopCharacteristics>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;

        var errors = Validate(xml);

        Assert.Contains(
            errors,
            error => error.Rule == "multi-instance" && error.Message.Contains("skipExpression"));
    }

    // ─────────────────────────── 往返保真 ───────────────────────────

    [Fact]
    public void RoundTrip_ShouldPreserveMultiInstanceConfiguration()
    {
        var xml = ParallelCollectionProcess(completionCondition: "${nrOfCompletedInstances == 2}");
        var converter = new BpmnXmlConverter();

        var model = converter.ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        using var exportStream = new MemoryStream();
        converter.ConvertToXml(model, exportStream);
        var exported = Encoding.UTF8.GetString(exportStream.ToArray());

        Assert.Contains("<multiInstanceLoopCharacteristics", exported, StringComparison.Ordinal);
        Assert.Contains("isSequential=\"false\"", exported, StringComparison.Ordinal);
        Assert.Contains("activiti:collection=\"${approvers}\"", exported, StringComparison.Ordinal);
        Assert.Contains("activiti:elementVariable=\"approver\"", exported, StringComparison.Ordinal);
        Assert.Contains("<completionCondition>", exported, StringComparison.Ordinal);

        // 再解析回来：模型上的多实例配置等价
        var model2 = converter.ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(exported)));
        var userTask = model2.GetMainProcess()!.FindFlowElementsOfType<NetBpm.Bpmn.Model.UserTask>().Single();
        var mi = userTask.LoopCharacteristics;

        Assert.NotNull(mi);
        Assert.False(mi.IsSequential);
        Assert.Equal("${approvers}", mi.Collection);
        Assert.Equal("approver", mi.ElementVariable);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async ValueTask<IReadOnlyList<NetBpm.Engine.Tasks.TaskEntity>> CurrentTasksAsync(
        TestEngine engine,
        string processInstanceId)
    {
        var tasks = await engine.TaskManager
            .FindTasksByProcessInstanceIdAsync(processInstanceId, Ct)
            .ConfigureAwait(false);

        return [.. tasks.OrderBy(task => task.Id, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<NetBpm.Bpmn.Validation.ValidationError> Validate(string bpmnXml)
    {
        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        var process = model.GetMainProcess()!;

        return new ProcessValidator().Validate(process);
    }

    private static string DefinitionsOpening =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="mi" name="多实例" isExecutable="true">
            <startEvent id="start" />
        """;

    private const string DefinitionsClosing =
        """
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="after" />
            <sequenceFlow id="f3" sourceRef="after" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>串行 + cardinality=3，后接捕获委托（观察循环变量）。</summary>
    private static string SequentialCardinalityProcess(bool loopCounterCapture = false) =>
        DefinitionsOpening + $$"""

            <userTask id="approve">
              <multiInstanceLoopCharacteristics isSequential="true">
                <loopCardinality>3</loopCardinality>
              </multiInstanceLoopCharacteristics>
            </userTask>
            {{(loopCounterCapture ? @"<serviceTask id=""after"" activiti:delegateExpression=""capture"" />" : @"<manualTask id=""after"" />")}}
        """ + DefinitionsClosing;

    /// <summary>串行 + 集合（elementVariable 驱动办理人）。</summary>
    private static string SequentialCollectionProcess() =>
        DefinitionsOpening + """

            <userTask id="approve" activiti:assignee="${approver}">
              <multiInstanceLoopCharacteristics isSequential="true"
                                                activiti:collection="${approvers}"
                                                activiti:elementVariable="approver" />
            </userTask>
            <manualTask id="after" />
        """ + DefinitionsClosing;

    /// <summary>并行 + 集合，可带完成条件。</summary>
    private static string ParallelCollectionProcess(string? completionCondition = null)
    {
        var condition = completionCondition is null
            ? string.Empty
            : $"\n    <completionCondition>{completionCondition}</completionCondition>";

        return DefinitionsOpening + $$"""

            <userTask id="approve" activiti:assignee="${approver}">
              <multiInstanceLoopCharacteristics isSequential="false"
                                                activiti:collection="${approvers}"
                                                activiti:elementVariable="approver">{{condition}}
              </multiInstanceLoopCharacteristics>
            </userTask>
            <manualTask id="after" />
        """ + DefinitionsClosing;
    }
}
