using NetBpm.Engine;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;

namespace NetBpm.UnitTests;

/// <summary>
/// P7 用户任务生命周期。验收口径见 §8：<c>userTask</c> 全生命周期可用、候选关系解析与 Activiti 一致。
/// <para>这里把两件最容易做错的事钉死：</para>
/// <list type="number">
///   <item><description>用户任务是<b>等待型节点</b>（启动后流程不结束，必须完成才继续）；</description></item>
///   <item><description>身份关系行的<b>类型与挂载点</b>（任务级 <c>candidate</c> / 流程实例级 <c>participant</c>）。</description></item>
/// </list>
/// </summary>
public sealed class UserTaskTests
{
    private static readonly string[] OnlyAfterDelegate = ["after"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UserTask_ShouldCreateTaskAndWait()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(UserTaskProcess());

        // 等待态：任务在、执行树在（根 + 走流程的子执行）
        Assert.Equal(1, engine.TaskManager.TaskCount);
        Assert.Equal(2, engine.ExecutionManager.ExecutionCount);

        var task = Assert.Single(engine.TaskManager.Tasks);

        Assert.Equal("approve", task.TaskDefinitionKey);
        Assert.Equal("审批", task.Name);
        Assert.Equal("kermit", task.Assignee);
        Assert.Equal("approveForm", task.FormKey);
        Assert.Equal(80, task.Priority);
        Assert.Equal("biz-key", task.BusinessKey);
        Assert.Equal(processInstanceId, task.ProcessInstanceId);
        Assert.NotNull(task.ExecutionId);
        Assert.NotNull(task.CreateTime);

        // 完成 → 继续走到结束事件 → 执行树与关联数据清空
        await engine.CompleteTaskAsync(task.Id);

        Assert.Equal(0, engine.TaskManager.TaskCount);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Empty(engine.TaskManager.IdentityLinks);
    }

    [Fact]
    public async Task UserTask_ShouldCreateIdentityLinksExactlyLikeActiviti()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess());

        var task = Assert.Single(engine.TaskManager.Tasks);

        // 任务级：候选用户 alice/bob + 候选组 managers/hr（共 4 条 'candidate'）
        var taskLinks = await engine.TaskManager.FindIdentityLinksByTaskIdAsync(task.Id, Ct);

        Assert.Equal(4, taskLinks.Count);
        Assert.All(taskLinks, link => Assert.Equal(IdentityLinkType.Candidate, link.Type));
        Assert.Contains(taskLinks, link => link.UserId == "alice");
        Assert.Contains(taskLinks, link => link.UserId == "bob");
        Assert.Contains(taskLinks, link => link.GroupId == "managers");
        Assert.Contains(taskLinks, link => link.GroupId == "hr");

        // 流程实例级：办理人 kermit + 候选用户 alice/bob（共 3 条 'participant'，按用户去重）
        // ⚠️ 办理人只写 'participant'，不写任务级 'assignee' —— 这是 Activiti 的实际行为
        var instanceLinks = await engine.TaskManager
            .FindIdentityLinksByProcessInstanceIdAsync(task.ProcessInstanceId!, Ct);

        Assert.Equal(3, instanceLinks.Count);
        Assert.All(instanceLinks, link =>
        {
            Assert.Equal(IdentityLinkType.Participant, link.Type);
            Assert.Null(link.TaskId);
        });
        Assert.Contains(instanceLinks, link => link.UserId == "kermit");
        Assert.Contains(instanceLinks, link => link.UserId == "alice");
        Assert.Contains(instanceLinks, link => link.UserId == "bob");
    }

    [Fact]
    public async Task UserTask_ShouldResolveAssigneeFromVariable()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            UserTaskProcess(assignee: "${approver}"),
            new Dictionary<string, object?> { ["approver"] = "alice" });

        var task = Assert.Single(engine.TaskManager.Tasks);

        Assert.Equal("alice", task.Assignee);
    }

    [Fact]
    public async Task UserTask_ShouldSetDueDateFromIsoString()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess(dueDate: "2030-01-02T03:04:05"));

        var task = Assert.Single(engine.TaskManager.Tasks);

        Assert.Equal(new DateTime(2030, 1, 2, 3, 4, 5), task.DueDate);
    }

    [Fact]
    public async Task UserTask_WithSkipExpressionTrue_ShouldNotCreateTask()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            UserTaskProcess(skipExpression: "${skip}"),
            new Dictionary<string, object?> { ["skip"] = true });

        // 跳过 ⇒ 不产生待办，流程直接跑完
        Assert.Equal(0, engine.TaskManager.TaskCount);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Empty(engine.TaskManager.IdentityLinks);
    }

    [Fact]
    public async Task UserTask_WithSkipExpressionFalse_ShouldCreateTask()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(
            UserTaskProcess(skipExpression: "${skip}"),
            new Dictionary<string, object?> { ["skip"] = false });

        Assert.Equal(1, engine.TaskManager.TaskCount);
        Assert.Equal(2, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task CompleteTask_ShouldWriteVariablesToProcessScope()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(TestEngine.UserTaskThenServiceProcess("after"));
        var task = Assert.Single(engine.TaskManager.Tasks);

        await engine.CompleteTaskAsync(task.Id, new Dictionary<string, object?> { ["approved"] = true });

        // 后续服务任务应当看得见完成时写入的变量（经作用域链落到流程实例）
        var captured = Assert.Single(engine.DelegateResolver.CapturedVariables);
        Assert.True(captured["approved"] is true);

        Assert.Equal(OnlyAfterDelegate, engine.ExecutedDelegates);
        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
    }

    [Fact]
    public async Task CompleteTask_WhenTaskDoesNotExist_ShouldThrow()
    {
        var engine = TestEngine.Create();

        var exception = await Assert.ThrowsAnyAsync<EngineException>(async () =>
        {
            await engine.CompleteTaskAsync("no-such-task");
        });

        Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimTask_ShouldSetAssigneeAndClaimTime()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess(assignee: null));
        var task = Assert.Single(engine.TaskManager.Tasks);

        Assert.Null(task.Assignee);
        Assert.Null(task.ClaimTime);

        await engine.ClaimTaskAsync(task.Id, "dave");

        Assert.Equal("dave", task.Assignee);
        Assert.NotNull(task.ClaimTime);
    }

    [Fact]
    public async Task ClaimTask_WhenAlreadyClaimedByAnotherUser_ShouldThrow()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess(assignee: "kermit"));
        var task = Assert.Single(engine.TaskManager.Tasks);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(async () =>
        {
            await engine.ClaimTaskAsync(task.Id, "dave");
        });

        Assert.Contains("already claimed", exception.Message, StringComparison.Ordinal);
        Assert.Equal("kermit", task.Assignee);
    }

    [Fact]
    public async Task ClaimTask_WithNullUser_ShouldUnclaim()
    {
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess(assignee: "kermit"));
        var task = Assert.Single(engine.TaskManager.Tasks);

        await engine.ClaimTaskAsync(task.Id, null);

        Assert.Null(task.Assignee);
        Assert.Null(task.ClaimTime);
    }

    [Fact]
    public async Task TriggerExecution_WhenTaskStillExists_ShouldThrow()
    {
        // 保证「未完成就发信号」会被拦住（对应 Activiti 的
        // "UserTask should not be signalled before complete"）
        var engine = TestEngine.Create();

        await engine.StartProcessAsync(UserTaskProcess());

        var execution = engine.ExecutionManager.Executions
            .Single(candidate => candidate.CurrentActivityId == "approve");

        engine.Services.Agenda.PlanTriggerExecutionOperation(execution);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(async () =>
        {
            await engine.Services.Agenda.DrainAsync(Ct);
        });

        Assert.Contains("should not be signalled before complete", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>用户任务流程；各属性可参数化以便逐项验证。</summary>
    private static string UserTaskProcess(
        string? assignee = "kermit",
        string? candidateUsers = "alice,bob",
        string? candidateGroups = "managers, hr",
        string? dueDate = null,
        string? skipExpression = null)
    {
        var assigneeAttribute = assignee is null ? string.Empty : $" activiti:assignee=\"{assignee}\"";
        var candidateUsersAttribute = candidateUsers is null ? string.Empty : $" activiti:candidateUsers=\"{candidateUsers}\"";
        var candidateGroupsAttribute = candidateGroups is null ? string.Empty : $" activiti:candidateGroups=\"{candidateGroups}\"";
        var dueDateAttribute = dueDate is null ? string.Empty : $" activiti:dueDate=\"{dueDate}\"";
        var skipExpressionAttribute = skipExpression is null ? string.Empty : $" activiti:skipExpression=\"{skipExpression}\"";

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="user-task" name="用户任务" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" name="审批"{{assigneeAttribute}}{{candidateUsersAttribute}}{{candidateGroupsAttribute}}{{dueDateAttribute}}{{skipExpressionAttribute}} activiti:formKey="approveForm" activiti:priority="80" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;
    }
}
