using System.Globalization;

using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Tasks;

namespace NetBpm.UnitTests;

/// <summary>
/// 运行期管理命令：改派（SetAssigneeCommand）与流程跳转（MoveActivityCommand）。
/// </summary>
public class RuntimeManagementTests
{
    /// <summary>两段用户任务：组长审批 → 经理审批 → 结束。</summary>
    private const string TwoStepProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="two-step" name="两级审批" isExecutable="true">
            <startEvent id="start" />
            <userTask id="leader_approve" name="组长审批" activiti:candidateGroups="leaders" />
            <userTask id="manager_approve" name="经理审批" activiti:candidateGroups="managers" />
            <endEvent id="end_approved" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="leader_approve" />
            <sequenceFlow id="f2" sourceRef="leader_approve" targetRef="manager_approve" />
            <sequenceFlow id="f3" sourceRef="manager_approve" targetRef="end_approved" />
          </process>
        </definitions>
        """;

    /// <summary>并行网关：fork → A/B 两个用户任务 → join → 结束（取消单分支用）。</summary>
    private const string ParallelProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="parallel-modify" name="并行取消" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <userTask id="review_a" name="A 审批" activiti:candidateGroups="group-a" />
            <userTask id="review_b" name="B 审批" activiti:candidateGroups="group-b" />
            <parallelGateway id="join" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="review_a" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="review_b" />
            <sequenceFlow id="f4" sourceRef="review_a" targetRef="join" />
            <sequenceFlow id="f5" sourceRef="review_b" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="join" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string FirstAssignee(TestEngine engine) =>
        engine.TaskManager.Tasks.Single().Assignee ?? string.Empty;

    [Fact]
    public async Task Reassign_ShouldChangeAssigneeWithoutClaimTime()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        var task = engine.TaskManager.Tasks.Single();
        var originalClaimTime = task.ClaimTime;

        await engine.SetAssigneeAsync(task.Id, "leader2");

        var reassigned = engine.TaskManager.Tasks.Single();

        Assert.Equal("leader2", reassigned.Assignee);
        Assert.Equal(instanceId, reassigned.ProcessInstanceId);

        // 认领痕迹不动：改派是管理动作，不是认领动作
        Assert.Equal(originalClaimTime, reassigned.ClaimTime);
    }

    [Fact]
    public async Task Reassign_ShouldMoveClaimedTaskToAnotherUser()
    {
        var engine = TestEngine.Create();
        await engine.StartProcessAsync(TwoStepProcess);

        var task = engine.TaskManager.Tasks.Single();
        await engine.ClaimTaskAsync(task.Id, "leader1");
        await engine.SetAssigneeAsync(task.Id, "manager1");

        Assert.Equal("manager1", FirstAssignee(engine));
    }

    [Fact]
    public async Task Reassign_UnknownTask_ShouldThrow()
    {
        var engine = TestEngine.Create();
        await engine.StartProcessAsync(TwoStepProcess);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            (() => engine.SetAssigneeAsync("no-such-task", "leader1").AsTask()));

        Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jump_ShouldSkipToTargetActivityAndCreateItsTask()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        // 停在组长审批 → 直接跳到经理审批
        var leaderTask = engine.TaskManager.Tasks.Single();
        await engine.MoveActivityToAsync(instanceId, "manager_approve", "管理页跳转");

        // 组长待办没了，经理待办出现（同一实例）
        Assert.Empty(engine.TaskManager.Tasks.Where(t => t.Id == leaderTask.Id));

        var managerTask = Assert.Single(engine.TaskManager.Tasks);
        Assert.Equal("manager_approve", managerTask.TaskDefinitionKey);
        Assert.Equal(instanceId, managerTask.ProcessInstanceId);

        // 从经理节点继续走完整个流程仍然可行
        await engine.CompleteTaskAsync(managerTask.Id);
        Assert.Empty(engine.TaskManager.Tasks);
    }

    [Fact]
    public async Task Jump_ToEndEvent_ShouldFinishTheInstance()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        await engine.MoveActivityToAsync(instanceId, "end_approved", "强制办结");

        Assert.Empty(engine.TaskManager.Tasks);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task Jump_ShouldPreserveVariables()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess, new Dictionary<string, object?>
        {
            ["days"] = 5,
        });

        await engine.MoveActivityToAsync(instanceId, "manager_approve");

        var instance = await engine.ExecutionManager.FindByIdAsync(instanceId);
        var variables = await instance!.GetVariablesAsync();
        Assert.Equal(5, Convert.ToInt32(variables["days"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Jump_UnknownTarget_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            (() => engine.MoveActivityToAsync(instanceId, "no_such_node").AsTask()));

        Assert.Contains("不在流程定义", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jump_AlreadyAtTarget_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.MoveActivityToAsync(instanceId, "leader_approve").AsTask());

        Assert.Contains("已停在目标节点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jump_FinishedInstance_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        await engine.MoveActivityToAsync(instanceId, "end_approved");

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.MoveActivityToAsync(instanceId, "leader_approve").AsTask());

        Assert.Contains("不存在或已结束", exception.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────── modify：取消活动 / 活动前启动 ───────────────────────────

    [Fact]
    public async Task CancelActivity_ShouldCloseTaskAndEndInstance()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        Assert.Single(engine.TaskManager.Tasks);

        // 唯一分支被取消 → 实例也随之结束（与正常走完同口径）
        await engine.CancelActivityAsync(instanceId, "leader_approve", "运维取消");

        Assert.Empty(engine.TaskManager.Tasks);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task CancelActivity_ShouldOnlyRemoveTargetBranch_AndKeepOthers()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(ParallelProcess);

        // 并行网关后两个用户任务各一个待办
        Assert.Equal(2, engine.TaskManager.Tasks.Count);

        await engine.CancelActivityAsync(instanceId, "review_a", "取消 A 分支");

        // A 分支的待办消失，B 分支还在；实例没有结束
        var remaining = Assert.Single(engine.TaskManager.Tasks);
        Assert.Equal("review_b", remaining.TaskDefinitionKey);

        var instance = await engine.ExecutionManager.FindByIdAsync(instanceId);
        Assert.NotNull(instance);
    }

    [Fact]
    public async Task CancelActivity_AlreadyFinished_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        await engine.CancelActivityAsync(instanceId, "leader_approve");

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CancelActivityAsync(instanceId, "leader_approve").AsTask());

        Assert.Contains("不存在或已结束", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelActivity_UnknownActivity_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CancelActivityAsync(instanceId, "no_such_node").AsTask());

        Assert.Contains("不在流程定义", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartBeforeActivity_ShouldAddBranchWithoutTouchingCurrentTask()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        var leaderTask = Assert.Single(engine.TaskManager.Tasks);
        Assert.Equal("leader_approve", leaderTask.TaskDefinitionKey);

        // 在经理审批之前再起一条令牌：原有组长待办保留，额外出现经理待办
        await engine.StartBeforeActivityAsync(instanceId, "manager_approve");

        Assert.Equal(2, engine.TaskManager.Tasks.Count);
        Assert.Contains(engine.TaskManager.Tasks, task => task.TaskDefinitionKey == "leader_approve");
        Assert.Contains(engine.TaskManager.Tasks, task => task.TaskDefinitionKey == "manager_approve");
    }

    // ─────────────────────────── modify：指令式修改（整批原子） ───────────────────────────

    [Fact]
    public async Task Modify_ShouldApplyAllInstructions()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        Assert.Equal("leader_approve", Assert.Single(engine.TaskManager.Tasks).TaskDefinitionKey);

        // 一组指令 = 一个决定：撤掉组长审批，改从经理审批继续
        var applied = await engine.CommandExecutor.ExecuteAsync(
            new ModifyProcessInstanceCommand(
                instanceId,
                [
                    new ModifyInstruction(ModifyInstructionType.Cancel, ActivityId: "leader_approve"),
                    new ModifyInstruction(ModifyInstructionType.StartBefore, ActivityId: "manager_approve"),
                    new ModifyInstruction(ModifyInstructionType.SetVariable, VariableName: "modifiedBy", VariableValue: "ops"),
                ],
                reason: "运维改派"));

        Assert.Equal(3, applied.Count);
        Assert.Equal(["cancel", "startBefore", "setVariable"], applied.Select(result => result.Type));
        Assert.Equal("manager_approve", Assert.Single(engine.TaskManager.Tasks).TaskDefinitionKey);

        var instance = await engine.ExecutionManager.FindByIdAsync(instanceId);

        Assert.Equal("ops", await instance!.GetVariableAsync("modifiedBy"));
    }

    [Fact]
    public async Task Modify_InvalidInstruction_ShouldRejectWholeBatch()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        // 第二条指令的活动 id 拼错：整批必须一条都不生效 ——
        // 这正是 modify 与「批量逐项执行」的分界线：先全量校验，再逐条执行
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CommandExecutor.ExecuteAsync(
                new ModifyProcessInstanceCommand(
                    instanceId,
                    [
                        new ModifyInstruction(ModifyInstructionType.StartBefore, ActivityId: "manager_approve"),
                        new ModifyInstruction(ModifyInstructionType.Cancel, ActivityId: "no_such_node"),
                    ])).AsTask());

        Assert.Contains("不在流程定义", exception.Message, StringComparison.Ordinal);

        // 第一条（合法）也没有生效
        Assert.Equal("leader_approve", Assert.Single(engine.TaskManager.Tasks).TaskDefinitionKey);
    }

    [Fact]
    public async Task Modify_StartAfter_ShouldFollowSingleOutgoingFlow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        // leader_approve 只有一条出流 → startAfter 落到 manager_approve
        var applied = await engine.CommandExecutor.ExecuteAsync(
            new ModifyProcessInstanceCommand(
                instanceId,
                [new ModifyInstruction(ModifyInstructionType.StartAfter, ActivityId: "leader_approve")]));

        Assert.Equal("manager_approve", Assert.Single(applied).Target);
        Assert.Equal(2, engine.TaskManager.Tasks.Count);
    }

    [Fact]
    public async Task Modify_StartAfter_MultipleOutgoing_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(ParallelProcess);

        // fork 有两条出流：目标不唯一，明确拒绝 —— 分支语义不该由运维命令替流程定义猜
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CommandExecutor.ExecuteAsync(
                new ModifyProcessInstanceCommand(
                    instanceId,
                    [new ModifyInstruction(ModifyInstructionType.StartAfter, ActivityId: "fork")])).AsTask());

        Assert.Contains("目标不唯一", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modify_TaskScopedVariable_WithoutWaitingTask_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        // leader_approve 上有待办，但 manager_approve 上没有 → task 作用域不适用，明确报错
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CommandExecutor.ExecuteAsync(
                new ModifyProcessInstanceCommand(
                    instanceId,
                    [
                        new ModifyInstruction(
                            ModifyInstructionType.SetVariable,
                            ActivityId: "manager_approve",
                            VariableName: "note",
                            VariableValue: "x",
                            VariableScope: ModifyVariableScope.Task),
                    ])).AsTask());

        Assert.Contains("没有等待中的待办", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Modify_EmptyInstructions_ShouldRejectUpfront()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        Assert.Throws<ArgumentException>(
            () => new ModifyProcessInstanceCommand(instanceId, []));
    }

    [Fact]
    public async Task StartBeforeActivity_NonFlowNode_ShouldThrow()
    {
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        // f1 是顺序流：没有行为，挂上去的执行永远推进不了
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.StartBeforeActivityAsync(instanceId, "f1").AsTask());

        Assert.Contains("不是可执行的流节点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jump_TaskWithoutExecution_ShouldThrowInsteadOfIndexOutOfRange()
    {
        // 回归用例：任务存在、但 EXECUTION_ID_ 为空（历史脏数据 / 并发删除执行）时，
        // 旧实现会走到 executions[0] 抛 ArgumentOutOfRangeException —— 必须是可读的 EngineException
        var engine = TestEngine.Create();
        var instanceId = await engine.StartProcessAsync(TwoStepProcess);

        engine.TaskManager.Tasks.Single().ExecutionId = null;

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.MoveActivityToAsync(instanceId, "manager_approve").AsTask());

        Assert.Contains("无法定位到等待中的执行", exception.Message, StringComparison.Ordinal);
    }
}
