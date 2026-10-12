using NetBpm.Engine;
using NetBpm.Engine.Command;

namespace NetBpm.UnitTests;

/// <summary>
/// P0 验收与 P6 核心语义。
/// <para>P0：工程底座就绪后，「空引擎能通过一个 NoOpCommand」（开发计划 §8 阶段总览）。</para>
/// <para>P6：顺序 / 排他 / 并行三类语义，对应风险 R1 的最高优先级项。</para>
/// </summary>
public sealed class EngineCommandTests
{
    private static readonly string[] OnlyDoWork = ["doWork"];
    private static readonly string[] OnlyApproved = ["approved"];
    private static readonly string[] OnlyRejected = ["rejected"];
    private static readonly string[] BothBranches = ["branchA", "branchB"];
    private static readonly string[] OnlyUnconditional = ["unconditional"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoOpCommand_ShouldRunThroughInterceptorChain()
    {
        var engine = TestEngine.Create();

        var result = await engine.CommandExecutor.ExecuteAsync(new NoOpCommand(), Ct);

        Assert.Equal(Unit.Value, result);
    }

    [Fact]
    public async Task CommandExecutor_ShouldDrainAgendaBeforeReturning()
    {
        // 议程必须在命令（= 事务）内排空（§5.3 / 风险 R4）
        var engine = TestEngine.Create();

        await engine.CommandExecutor.ExecuteAsync(new NoOpCommand(), Ct);

        Assert.True(engine.Services.Agenda.IsEmpty);
    }

    [Fact]
    public async Task SequenceFlowProcess_ShouldCompleteAndDeleteProcessInstance()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(TestEngine.SequentialProcess("doWork"));

        Assert.False(string.IsNullOrWhiteSpace(processInstanceId));
        Assert.Equal(OnlyDoWork, engine.ExecutedDelegates);

        // 顺序流跑通后，流程实例的执行树应被全部清理（§5.5 的收口判定）
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task SequenceFlow_WithoutCondition_ShouldAlwaysBeTrue()
    {
        // §5.2：无条件顺序流恒为 true —— 这是排他网关「取第一个条件为真的流」的语义基础。
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(TestEngine.SequentialProcess("unconditional"));

        Assert.Equal(OnlyUnconditional, engine.ExecutedDelegates);
    }

    [Fact]
    public async Task ExclusiveGateway_WhenConditionTrue_ShouldTakeConditionalFlow()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            TestEngine.ExclusiveGatewayProcess(),
            new Dictionary<string, object?> { ["approved"] = true });

        Assert.Equal(OnlyApproved, engine.ExecutedDelegates);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task ExclusiveGateway_WhenConditionFalse_ShouldTakeDefaultFlow()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            TestEngine.ExclusiveGatewayProcess(),
            new Dictionary<string, object?> { ["approved"] = false });

        // 条件为假 ⇒ 无出流被选中 ⇒ 走默认流（§5.4 之一）
        Assert.Equal(OnlyRejected, engine.ExecutedDelegates);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task ExclusiveGateway_WhenNoFlowSelected_ShouldThrowExactlyLikeActiviti()
    {
        // 排他网关无默认流且条件全假 ⇒ 必须抛错，且错误文案与 Activiti 一致
        // （对拍时错误信息也属于可比对输出，见 §9.4）。
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="noDefault" isExecutable="true">
                <startEvent id="start" />
                <exclusiveGateway id="gw" />
                <serviceTask id="only" activiti:delegateExpression="only" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="gw" />
                <sequenceFlow id="f2" sourceRef="gw" targetRef="only">
                  <conditionExpression xsi:type="tFormalExpression">${approved}</conditionExpression>
                </sequenceFlow>
                <sequenceFlow id="f3" sourceRef="only" targetRef="end" />
              </process>
            </definitions>
            """;

        var engine = TestEngine.Create();

        var exception = await Assert.ThrowsAsync<EngineException>(async () =>
        {
            await engine.StartProcessAsync(xml, new Dictionary<string, object?> { ["approved"] = false });
        });

        Assert.Contains(
            "No outgoing sequence flow of the exclusive gateway 'gw'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelGateway_ShouldForkAndSynchronizeAllBranches()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(TestEngine.ParallelGatewayProcess());

        // 两条分支都必须跑到（Fork），且 Join 必须等全部到达后才继续（同步合并）
        var executed = engine.ExecutedDelegates.OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(BothBranches, executed);

        // Join 完成后流程实例收口
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }
}
