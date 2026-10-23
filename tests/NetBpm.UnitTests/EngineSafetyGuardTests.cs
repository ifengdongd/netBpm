using NetBpm.El;
using NetBpm.Engine;

namespace NetBpm.UnitTests;

/// <summary>
/// 引擎的「响亮失败」护栏：死循环模型、终止结束事件后的残留操作、过深表达式。
/// <para>
/// 这三条的共性：出问题时<b>不会报错</b> —— 死循环表现为 OOM / 请求挂死，
/// 残留操作表现为「终止了但另一个分支的委托还在跑」，深嵌套表现为进程直接退出。
/// 所以必须由引擎主动判定并抛错。
/// </para>
/// </summary>
public sealed class EngineSafetyGuardTests
{
    /// <summary>回退边：serviceTask → 网关（条件恒真）→ serviceTask，单命令内永不停止。</summary>
    private const string LoopProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="loop_process" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="step" activiti:delegateExpression="step" />
            <exclusiveGateway id="gw" default="fEnd" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="step" />
            <sequenceFlow id="f2" sourceRef="step" targetRef="gw" />
            <sequenceFlow id="fLoop" sourceRef="gw" targetRef="step">
              <conditionExpression xsi:type="tFormalExpression">${1 == 1}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="fEnd" sourceRef="gw" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>
    /// 并行分叉：A 分支直达终止结束事件，B 分支是一个服务任务。
    /// <para>
    /// 🔑 分叉时两条 <c>ContinueProcessOperation</c> 一起入队（A 在前）。A 执行到终止事件时，
    /// B 的操作<b>还在队列里</b> —— 这正是修复前「终止了但另一边还在跑」的场景。
    /// </para>
    /// </summary>
    private const string ParallelTerminateProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="parallel_terminate" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <serviceTask id="sideEffect" activiti:delegateExpression="sideEffect" />
            <endEvent id="end" />
            <endEvent id="terminate">
              <terminateEventDefinition />
            </endEvent>
            <sequenceFlow id="f0" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="fa" sourceRef="fork" targetRef="terminate" />
            <sequenceFlow id="fb" sourceRef="fork" targetRef="sideEffect" />
            <sequenceFlow id="fb2" sourceRef="sideEffect" targetRef="end" />
          </process>
        </definitions>
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agenda_ShouldFailLoudlyWhenOperationBudgetIsExceeded()
    {
        var engine = TestEngine.Create(maxOperationsPerCommand: 20);

        var exception = await Assert.ThrowsAsync<EngineException>(
            async () => await engine.StartProcessAsync(LoopProcess, ct: Ct));

        // 必须指出「模型有环」，而不是让宿主看到一个看不出原因的失败
        Assert.Contains("死循环", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxOperationsPerCommand", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminate_ShouldDiscardQueuedOperationsOfOtherBranches()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(ParallelTerminateProcess, ct: Ct);

        // B 分支的 ContinueProcessOperation 在终止发生时还在队列里。
        // 若删除路径不置 IsEnded（或不清议程），它会在已删除的执行上继续跑 —— 这正是修复前的行为。
        Assert.DoesNotContain("sideEffect", engine.ExecutedDelegates);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public void ElParser_ShouldRejectDeeplyNestedExpression()
    {
        var expression = "${" + new string('(', 500) + "1" + new string(')', 500) + "}";

        var exception = Assert.Throws<ElSyntaxException>(
            () => new ExpressionManager().CreateExpression(expression));

        Assert.Contains("嵌套", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElParser_ShouldStillAcceptModeratelyNestedExpression()
    {
        var expression = "${" + new string('(', 10) + "1" + new string(')', 10) + " == 1}";

        // 不抛异常即可（求值结果由 ExpressionEvaluationTests 覆盖）
        var parsed = new ExpressionManager().CreateExpression(expression);

        Assert.Equal(expression, parsed.ExpressionText);
    }
}
