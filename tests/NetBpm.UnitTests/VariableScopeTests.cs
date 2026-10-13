using NetBpm.Engine.Execution;

namespace NetBpm.UnitTests;

/// <summary>
/// 变量作用域与「同名变量二次写入」。
/// <para>
/// 回归背景：内存宿主下 <see cref="IExecutionEntity"/> 的 <c>VariableInstanceManager</c> 为
/// <see langword="null"/>（见 <see cref="IVariableInstanceManager"/> 的类注释：变量只活在执行对象上）。
/// 早期实现里 <c>SetLocalVariableAsync</c> 只在「有数据管理器」时把新值写回，
/// 内存宿主下对<b>已存在</b>的变量就成了空操作 —— 表现为「第二次提交表单的值被静默丢弃」，
/// 网关仍读到上一次的旧值（审批场景里就是「经理选了拒绝、流程却按通过走」）。
/// </para>
/// <para>
/// 这里同时钉两件事：<b>变量落在最高作用域</b>（Activiti 的 setVariable 语义），
/// 以及<b>覆盖后的新值必须被网关看到</b>。
/// </para>
/// </summary>
public sealed class VariableScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 两个连续用户任务 → 排他网关（<c>${result == 1}</c> 走 matched，否则走默认流 fallback）。
    /// 两次任务完成写的是<b>同一个</b>变量名 <c>result</c>。
    /// </summary>
    private static string OverwriteProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     targetNamespace="http://netbpm/test">
          <process id="variable-overwrite" isExecutable="true">
            <startEvent id="start" />
            <userTask id="first" name="第一次" activiti:assignee="kermit" />
            <userTask id="second" name="第二次" activiti:assignee="kermit" />
            <exclusiveGateway id="gw" default="flowFallback" />
            <serviceTask id="matched" activiti:delegateExpression="matched" />
            <serviceTask id="fallback" activiti:delegateExpression="fallback" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="first" />
            <sequenceFlow id="f2" sourceRef="first" targetRef="second" />
            <sequenceFlow id="f3" sourceRef="second" targetRef="gw" />
            <sequenceFlow id="flowMatched" sourceRef="gw" targetRef="matched">
              <conditionExpression xsi:type="tFormalExpression">${result == 1}</conditionExpression>
            </sequenceFlow>
            <sequenceFlow id="flowFallback" sourceRef="gw" targetRef="fallback" />
            <sequenceFlow id="f4" sourceRef="matched" targetRef="end" />
            <sequenceFlow id="f5" sourceRef="fallback" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task SetVariable_SecondWriteToSameName_ShouldOverwriteValue()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(OverwriteProcess());

        var first = Assert.Single(engine.TaskManager.Tasks);
        await engine.CompleteTaskAsync(first.Id, new Dictionary<string, object?> { ["result"] = "1" });

        var afterFirst = await engine.ExecutionManager.FindByIdAsync(processInstanceId, Ct);
        var variables = await afterFirst!.GetVariablesAsync(Ct);
        Assert.Equal("1", variables["result"]);

        // 第二次写同名变量：必须覆盖，而不是被丢弃
        var second = Assert.Single(engine.TaskManager.Tasks);
        await engine.CompleteTaskAsync(second.Id, new Dictionary<string, object?> { ["result"] = "2" });

        // process 已走完，执行树被清理；用服务任务「看到」的变量快照落证据
        var captured = Assert.Single(engine.DelegateResolver.CapturedVariables);
        Assert.Equal("2", captured["result"]);
    }

    [Fact]
    public async Task Gateway_ShouldRouteByTheOverwrittenValue()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(OverwriteProcess());

        // 第一次选 1（通过），第二次改成 2（拒绝）⇒ 必须走默认流
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("1"));
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("2"));

        Assert.Equal(["fallback"], engine.ExecutedDelegates);
    }

    [Fact]
    public async Task Gateway_ShouldRouteByTheOverwrittenValue_InTheOtherDirection()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(OverwriteProcess());

        // 反向：第一次 2，第二次改回 1 ⇒ 必须命中条件流。
        // 少了这条，只覆盖「旧值恰好也满足条件」的退化实现也能蒙混过关。
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("2"));
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("1"));

        Assert.Equal(["matched"], engine.ExecutedDelegates);
    }

    [Fact]
    public async Task SetVariable_FirstWrite_ShouldLandOnProcessInstanceScope()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(OverwriteProcess());

        var task = Assert.Single(engine.TaskManager.Tasks);
        Assert.NotNull(task.ExecutionId);

        // 在任务执行的子作用域上写变量：
        // Activiti 的 setVariable 会上溯到「已存在该变量的最高作用域」，都没有才建在当前作用域。
        // 首次写入 ⇒ 落流程实例作用域（这样并行的兄弟分支才能互相看到）。
        await engine.CompleteTaskAsync(task.Id, ToVariables("1"));

        var processInstance = await engine.ExecutionManager.FindByIdAsync(processInstanceId, Ct);
        Assert.NotNull(processInstance);

        var instanceVariables = await processInstance.GetVariablesAsync(Ct);
        Assert.Equal("1", instanceVariables["result"]);
    }

    [Fact]
    public async Task SetVariable_SameValueRewritten_ShouldStayReadable()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(OverwriteProcess());

        // 值没变也必须走一次更新路径：内存宿主下若把它当「无变化」跳过，
        // 一旦将来引入脏检查就又会退化成「丢更新」。
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("1"));
        await engine.CompleteTaskAsync(Assert.Single(engine.TaskManager.Tasks).Id, ToVariables("1"));

        Assert.Equal(["matched"], engine.ExecutedDelegates);
    }

    private static Dictionary<string, object?> ToVariables(string result) =>
        new(StringComparer.Ordinal) { ["result"] = result };
}
