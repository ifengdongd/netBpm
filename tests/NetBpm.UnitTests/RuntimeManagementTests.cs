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
        Assert.Equal(5, Convert.ToInt32(variables["days"]));
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
