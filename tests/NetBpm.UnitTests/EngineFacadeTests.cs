using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Identity;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;

// Bpmn.Model 里的 Task（BPMN 任务节点）与 System.Threading.Tasks.Task 同名，
// 因此只按需别名引入需要的类型
using Process = NetBpm.Bpmn.Model.Process;

namespace NetBpm.UnitTests;

/// <summary>
/// 引擎门面（<see cref="IProcessEngine"/>）的仓库与查询能力：
/// 流程定义挂起 / 激活、待办分页与总数。
/// </summary>
/// <remarks>
/// 这里用<b>内存宿主 + 真实门面</b>（<see cref="ProcessEngine"/>）：挂起位、分页切片都发生在门面层，
/// 直接调仓储或命令会跳过被验证的那一层。
/// </remarks>
public sealed class EngineFacadeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>单个用户任务：start → approve → end。</summary>
    private const string SingleTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="facade-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>并行 fork 出三个用户任务：同一实例上稳定有 3 个待办（分页用例）。</summary>
    private const string ThreeParallelTasksProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="facade-parallel" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <userTask id="t1" activiti:assignee="u1" />
            <userTask id="t2" activiti:assignee="u2" />
            <userTask id="t3" activiti:assignee="u3" />
            <parallelGateway id="join" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="t1" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="t2" />
            <sequenceFlow id="f4" sourceRef="fork" targetRef="t3" />
            <sequenceFlow id="f5" sourceRef="t1" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="t2" targetRef="join" />
            <sequenceFlow id="f7" sourceRef="t3" targetRef="join" />
            <sequenceFlow id="f8" sourceRef="join" targetRef="end" />
          </process>
        </definitions>
        """;

    /// <summary>候选组任务：无人办理人，只有组可见（候选组查询用例）。</summary>
    private const string CandidateGroupProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="facade-candidate-group" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" activiti:candidateGroups="managers" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    // ─────────────────────────── 流程定义挂起 / 激活 ───────────────────────────

    [Fact]
    public async Task Suspend_ShouldBlockNewInstancesAndResumeAfterActivate()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);

        var firstInstanceId = await engine.StartProcessInstanceAsync(definition.Id, "biz-1", ct: Ct);
        var firstTask = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: firstInstanceId), Ct));

        await engine.SuspendProcessDefinitionAsync(definition.Id, ct: Ct);

        var suspended = Assert.Single(await engine.FindProcessDefinitionsAsync(ct: Ct));
        Assert.True(suspended.IsSuspended);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.StartProcessInstanceAsync(definition.Id, ct: Ct).AsTask());
        Assert.Contains("已挂起", exception.Message, StringComparison.Ordinal);

        // 定义级挂起不影响运行中的实例：待办照常可办，实例能正常结束
        await engine.CompleteTaskAsync(firstTask.Id, ct: Ct);
        Assert.Empty(host.TaskManager.Tasks);

        // 激活后恢复「可启动新实例」
        await engine.ActivateProcessDefinitionAsync(definition.Id, ct: Ct);
        Assert.False((await engine.FindProcessDefinitionsAsync(ct: Ct)).Single().IsSuspended);

        var secondInstanceId = await engine.StartProcessInstanceAsync(definition.Id, "biz-2", ct: Ct);
        Assert.NotEqual(firstInstanceId, secondInstanceId);
    }

    [Fact]
    public async Task Suspend_UnknownDefinition_ShouldThrow()
    {
        var (engine, _) = Create();

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.SuspendProcessDefinitionAsync("no-such-definition", ct: Ct).AsTask());

        Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────── 待办分页 ───────────────────────────

    [Fact]
    public async Task FindTasks_ShouldPageWithStableOrderAndReportTotal()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, ThreeParallelTasksProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var all = await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, await engine.CountTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        var firstPage = await engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, MaxResults: 2), Ct);
        var secondPage = await engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, FirstResult: 2, MaxResults: 2), Ct);

        Assert.Equal(2, firstPage.Count);
        Assert.Single(secondPage);

        // 两页拼起来必须等于「不分页」的结果，且顺序一致 —— 否则分页会重复/漏掉待办
        var pagedIds = firstPage.Concat(secondPage).Select(task => task.Id).ToArray();
        Assert.Equal(all.Select(task => task.Id), pagedIds);

        // 越界页返回空而不是报错
        Assert.Empty(await engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, FirstResult: 10), Ct));
    }

    [Fact]
    public async Task FindTasks_InvalidPaging_ShouldThrow()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, FirstResult: -1), Ct).AsTask());

        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, MaxResults: 0), Ct).AsTask());
    }

    [Fact]
    public async Task FindTasks_WithoutCriteria_ShouldStillThrow()
    {
        var (engine, _) = Create();

        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindTasksAsync(new TaskQuery(), Ct).AsTask());
    }

    [Fact]
    public async Task FindTasks_ShouldNarrowByDefinitionAndTaskKey()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, ThreeParallelTasksProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        // 节点 id 收窄：只剩 t2 那一条
        var onlyT2 = await engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, TaskDefinitionKey: "t2"), Ct);
        var t2 = Assert.Single(onlyT2);
        Assert.Equal("t2", t2.TaskDefinitionKey);

        // 流程定义收窄：三条都命中（同一实例、同一版本）
        Assert.Equal(3, (await engine.FindTasksAsync(
            new TaskQuery(ProcessInstanceId: instanceId, ProcessDefinitionId: definition.Id), Ct)).Count);

        // 收窄条件不匹配时返回空，且 CountTasksAsync 同口径
        var none = new TaskQuery(ProcessInstanceId: instanceId, TaskDefinitionKey: "no-such-node");
        Assert.Empty(await engine.FindTasksAsync(none, Ct));
        Assert.Equal(0, await engine.CountTasksAsync(none, Ct));

        // 收窄条件不能单独当查询条件用（否则等于全表扫描）
        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindTasksAsync(
            new TaskQuery(TaskDefinitionKey: "t1"), Ct).AsTask());
    }

    // ─────────────────────────── 删除流程实例 ───────────────────────────

    [Fact]
    public async Task DeleteProcessInstance_ShouldClearRuntimeAndCloseHistory()
    {
        var history = new RecordingHistoryManager();
        var (engine, host) = Create(history);

        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, "biz-del", ct: Ct);

        await engine.DeleteProcessInstanceAsync(instanceId, "发起人撤回", Ct);

        // 运行态：待办与执行树都清干净
        Assert.Empty(host.TaskManager.Tasks);
        Assert.Null(await host.ExecutionManager.FindByIdAsync(instanceId, Ct));

        // 历史：流程实例收口（END_ACT_ID_ 为 null —— 与 Activiti 的删除命令同口径），
        // 等待节点的历史行也收口（只收口，不产生「活动完成」语义）
        Assert.Contains("ProcessInstanceEnd:", history.Calls);
        Assert.Contains("ActivityEndQuiet:approve", history.Calls);

        // 已结束的实例不能再删
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.DeleteProcessInstanceAsync(instanceId, ct: Ct).AsTask());
        Assert.Contains("不存在或已结束", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteProcessInstance_WithExecutionId_ShouldThrow()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, ThreeParallelTasksProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        // fork 出了三条分支，随便取一条子执行
        var child = (await host.ExecutionManager
            .FindActiveChildExecutionsByProcessInstanceIdAsync(instanceId, Ct))[0];

        // 传子执行 id：只删一条分支会留下永远收不了口的实例，必须明确拒绝
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.DeleteProcessInstanceAsync(child.Id, ct: Ct).AsTask());

        Assert.Contains("不是流程实例", exception.Message, StringComparison.Ordinal);

        // 拒绝时不能有任何副作用
        Assert.Equal(3, host.TaskManager.Tasks.Count);
    }

    // ─────────────────────────── 按 key 启动 ───────────────────────────

    [Fact]
    public async Task StartByKey_ShouldStartLatestVersion()
    {
        var (engine, host) = Create();

        var first = await DeployAsync(engine, host, SingleTaskProcess);
        var second = await DeployAsync(engine, host, SingleTaskProcess);

        Assert.True(second.Version > first.Version);

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            first.Key, "biz-latest", startUserId: "kermit", ct: Ct);

        var instance = await host.ExecutionManager.FindByIdAsync(instanceId, Ct);

        Assert.Equal(second.Id, instance!.ProcessDefinitionId);
        Assert.Equal("biz-latest", instance.BusinessKey);
    }

    [Fact]
    public async Task StartByKey_SuspendedLatestVersion_ShouldThrow()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);

        await engine.SuspendProcessDefinitionAsync(definition.Id, ct: Ct);

        // 按 key 启动同样要受挂起位约束（它最终走 StartProcessInstanceAsync）
        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.StartProcessInstanceByKeyAsync(definition.Key, ct: Ct).AsTask());

        Assert.Contains("已挂起", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartByKey_UnknownKey_ShouldThrow()
    {
        var (engine, _) = Create();

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.StartProcessInstanceByKeyAsync("no-such-key", ct: Ct).AsTask());

        Assert.Contains("不存在", exception.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────── 实例级挂起 ───────────────────────────

    [Fact]
    public async Task SuspendProcessInstance_ShouldBlockCompleteAndClaimUntilActivated()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var task = Assert.Single(
            await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        await engine.SuspendProcessInstanceAsync(instanceId, Ct);

        // 待办仍可查询，但两个入口都被挂起位拦住（Activiti 的原文消息）
        Assert.Equal(TaskEntity.SuspendedSuspensionState, task.SuspensionState);
        Assert.Single(host.TaskManager.Tasks);

        var completeException = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CompleteTaskAsync(task.Id, ct: Ct).AsTask());
        Assert.Contains("Cannot complete a suspended task", completeException.Message, StringComparison.Ordinal);

        var claimException = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.ClaimTaskAsync(task.Id, "kermit", Ct).AsTask());
        Assert.Contains("Cannot claim a suspended task", claimException.Message, StringComparison.Ordinal);

        // 激活后恢复
        await engine.ActivateProcessInstanceAsync(instanceId, Ct);
        Assert.Equal(TaskEntity.ActiveSuspensionState, task.SuspensionState);

        await engine.CompleteTaskAsync(task.Id, ct: Ct);
        Assert.Empty(host.TaskManager.Tasks);
    }

    [Fact]
    public async Task SuspendProcessInstance_FinishedInstance_ShouldThrow()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));
        await engine.CompleteTaskAsync(task.Id, ct: Ct);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.SuspendProcessInstanceAsync(instanceId, Ct).AsTask());

        Assert.Contains("不存在或已结束", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuspendDefinition_WithInstances_ShouldSuspendRunningTasks()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        // suspendProcessInstances = true：定义与运行实例一起挂起
        await engine.SuspendProcessDefinitionAsync(definition.Id, suspendProcessInstances: true, ct: Ct);

        Assert.Equal(TaskEntity.SuspendedSuspensionState, task.SuspensionState);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CompleteTaskAsync(task.Id, ct: Ct).AsTask());
        Assert.Contains("Cannot complete a suspended task", exception.Message, StringComparison.Ordinal);

        // activateProcessInstances = true：定义与实例一起恢复，待办可以继续办
        await engine.ActivateProcessDefinitionAsync(definition.Id, activateProcessInstances: true, ct: Ct);
        Assert.Equal(TaskEntity.ActiveSuspensionState, task.SuspensionState);

        await engine.CompleteTaskAsync(task.Id, ct: Ct);
        Assert.Empty(host.TaskManager.Tasks);
    }

    [Fact]
    public async Task SuspendDefinition_WithoutInstances_ShouldLeaveRunningTasksActive()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        await engine.SuspendProcessDefinitionAsync(definition.Id, ct: Ct);

        // 定义级挂起不波及运行中的实例（Activiti 默认行为）
        Assert.Equal(TaskEntity.ActiveSuspensionState, task.SuspensionState);
        await engine.CompleteTaskAsync(task.Id, ct: Ct);
    }

    // ─────────────────────────── 删除部署 ───────────────────────────

    [Fact]
    public async Task DeleteDeployment_WithRunningInstances_ShouldRefuseWithoutCascade()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.DeleteDeploymentAsync(definition.DeploymentId, ct: Ct).AsTask());

        Assert.Contains("cascade=true", exception.Message, StringComparison.Ordinal);

        // 拒绝时不能有副作用
        Assert.Single(await engine.FindProcessDefinitionsAsync(ct: Ct));
        Assert.Single(host.TaskManager.Tasks);
        Assert.NotNull(await host.ExecutionManager.FindByIdAsync(instanceId, Ct));
    }

    [Fact]
    public async Task DeleteDeployment_Cascade_ShouldRemoveDefinitionAndInstances()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        await engine.DeleteDeploymentAsync(definition.DeploymentId, cascade: true, Ct);

        // 定义 / 运行态都清干净
        Assert.Empty(await engine.FindProcessDefinitionsAsync(ct: Ct));
        Assert.Empty(host.TaskManager.Tasks);
        Assert.Null(await host.ExecutionManager.FindByIdAsync(instanceId, Ct));

        // 已删的定义不能再用来启动（内存宿主没有模型缓存，这条同时守住「不存在」的可读报错）
        var startException = await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.StartProcessInstanceAsync(definition.Id, ct: Ct).AsTask());
        Assert.Contains("不存在", startException.Message, StringComparison.Ordinal);

        // 再删一次：不存在是 404 语义，用专门的异常类型
        await Assert.ThrowsAsync<EngineObjectNotFoundException>(
            () => engine.DeleteDeploymentAsync(definition.DeploymentId, ct: Ct).AsTask());
    }

    // ─────────────────────────── 历史任务分页 ───────────────────────────

    [Fact]
    public async Task FindHistoricTasks_ShouldPageAndFilter()
    {
        var history = new RecordingHistoryManager();
        var (engine, host) = Create(history);

        var definition = await DeployAsync(engine, host, ThreeParallelTasksProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var tasks = await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct);
        Assert.Equal(3, tasks.Count);

        // 完成两条，留一条在办 —— 历史里应有 3 条（2 条已结束）
        await engine.CompleteTaskAsync(tasks[0].Id, ct: Ct);
        await engine.CompleteTaskAsync(tasks[1].Id, ct: Ct);

        var all = await engine.FindHistoricTasksAsync(new HistoricTaskQuery(instanceId), Ct);
        Assert.Equal(3, all.Count);

        var finished = new HistoricTaskQuery(instanceId, OnlyFinished: true);
        Assert.Equal(2, (await engine.FindHistoricTasksAsync(finished, Ct)).Count);
        Assert.Equal(2, await engine.CountHistoricTasksAsync(finished, Ct));

        // 办理人收窄
        var byAssignee = await engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, Assignee: "u2"), Ct);
        Assert.Equal("u2", Assert.Single(byAssignee).Assignee);

        // 分页：逐页拼起来必须等于全量顺序（跨页不重不漏）
        var page1 = await engine.FindHistoricTasksAsync(new HistoricTaskQuery(instanceId, MaxResults: 2), Ct);
        var page2 = await engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, FirstResult: 2, MaxResults: 2), Ct);

        Assert.Equal(2, page1.Count);
        Assert.Single(page2);
        Assert.Equal(all.Select(task => task.Id), page1.Concat(page2).Select(task => task.Id));

        // 越界页返回空
        Assert.Empty(await engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, FirstResult: 10), Ct));
    }

    [Fact]
    public async Task FindHistoricTasks_InvalidPagingOrMissingInstance_ShouldThrow()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, MaxResults: 0), Ct).AsTask());

        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, FirstResult: -1), Ct).AsTask());

        // 旧版要求「实例 id 必填」→ ArgumentException；现在放开跨实例查询，
        // 但「一个条件都不给」仍然被门面拒绝（防止全表扫描）
        await Assert.ThrowsAnyAsync<EngineException>(() => engine.FindHistoricTasksAsync(
            new HistoricTaskQuery("  "), Ct).AsTask());
    }

    // ─────────────────────────── 补充查询（P0）───────────────────────────

    [Fact]
    public async Task FindProcessInstances_ShouldFilterAndPage()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);

        var first = await engine.StartProcessInstanceAsync(definition.Id, "biz-1", "alice", ct: Ct);
        var second = await engine.StartProcessInstanceAsync(definition.Id, "biz-2", "bob", ct: Ct);

        Assert.Equal(2, await engine.CountProcessInstancesAsync(new ProcessInstanceQuery(), Ct));

        var byBusinessKey = await engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(BusinessKey: "biz-2"), Ct);
        Assert.Equal(second, Assert.Single(byBusinessKey).Id);

        var byStartUser = await engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(StartUserId: "alice"), Ct);
        Assert.Equal(first, Assert.Single(byStartUser).Id);

        // 按 key（全部版本）过滤
        var byKey = await engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(ProcessDefinitionKey: "facade-single"), Ct);
        Assert.Equal(2, byKey.Count);

        // 分页不重不漏
        var page1 = await engine.FindProcessInstancesAsync(new ProcessInstanceQuery(MaxResults: 1), Ct);
        var page2 = await engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(FirstResult: 1, MaxResults: 1), Ct);

        Assert.Single(page1);
        Assert.Single(page2);
        Assert.NotEqual(page1[0].Id, page2[0].Id);

        // 越界页为空
        Assert.Empty(await engine.FindProcessInstancesAsync(
            new ProcessInstanceQuery(FirstResult: 10), Ct));
    }

    [Fact]
    public async Task GetTask_ShouldReturnDetailAndIdentityLinks()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, CandidateGroupProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        var detail = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(detail);
        Assert.Equal("approve", detail!.TaskDefinitionKey);
        Assert.Null(detail.Assignee);
        Assert.Equal(TaskEntity.ActiveSuspensionState, detail.SuspensionState);

        var links = await engine.FindTaskIdentityLinksAsync(task.Id, Ct);
        Assert.Contains(
            links,
            link => string.Equals(link.GroupId, "managers", StringComparison.Ordinal)
                && string.Equals(link.Type, IdentityLinkType.Candidate, StringComparison.Ordinal));

        Assert.Null(await engine.GetTaskAsync("missing-task", Ct));

        var byGroup = await engine.FindTasksAsync(new TaskQuery(CandidateGroup: "managers"), Ct);
        Assert.Equal(task.Id, Assert.Single(byGroup).Id);

        Assert.Empty(await engine.FindTasksAsync(new TaskQuery(CandidateGroup: "nobody"), Ct));
    }

    [Fact]
    public async Task SetVariable_ShouldWriteProcessScopeAndRemove()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(
            definition.Id,
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = 3 },
            ct: Ct);

        await engine.SetVariableAsync(instanceId, "days", 5L, Ct);
        await engine.SetVariablesAsync(
            instanceId,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["approved"] = true,
                ["reason"] = "ok",
            },
            Ct);

        var variables = await engine.GetVariablesAsync(instanceId, Ct);
        Assert.Equal(5L, variables["days"]);
        Assert.Equal(true, variables["approved"]);
        Assert.Equal("ok", variables["reason"]);

        Assert.True(await engine.RemoveVariableAsync(instanceId, "reason", Ct));
        Assert.DoesNotContain("reason", (await engine.GetVariablesAsync(instanceId, Ct)).Keys);
    }

    [Fact]
    public async Task FindHistoricTasks_AcrossInstances_ShouldFilterByAssignee()
    {
        var history = new RecordingHistoryManager();
        var (engine, host) = Create(history);
        var definition = await DeployAsync(engine, host, SingleTaskProcess);

        var first = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var second = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);

        var firstTask = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: first), Ct));
        await engine.CompleteTaskAsync(firstTask.Id, ct: Ct);

        // 「我的已办」：不再要求 processInstanceId
        var finished = await engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(Assignee: "kermit", OnlyFinished: true), Ct);
        Assert.Equal(firstTask.Id, Assert.Single(finished).Id);

        // 按定义跨实例
        var all = await engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(ProcessDefinitionId: definition.Id), Ct);
        Assert.Equal(2, all.Count);

        // 仍可按实例收窄
        var byInstance = await engine.FindHistoricTasksAsync(new HistoricTaskQuery(second), Ct);
        Assert.Equal(second, Assert.Single(byInstance).ProcessInstanceId);
    }

    [Fact]
    public async Task FindProcessDefinitions_ShouldFilterByKeyAndPage()
    {
        var (engine, host) = Create();
        await DeployAsync(engine, host, SingleTaskProcess);
        await DeployAsync(engine, host, CandidateGroupProcess);
        await DeployAsync(engine, host, SingleTaskProcess);

        var single = await engine.FindProcessDefinitionsAsync(
            new ProcessDefinitionQuery(Key: "facade-single"), Ct);

        Assert.Equal(2, single.Count);
        Assert.Equal([1, 2], single.Select(definition => definition.Version).Order());

        Assert.Equal(3, await engine.CountProcessDefinitionsAsync(new ProcessDefinitionQuery(), Ct));

        var page = await engine.FindProcessDefinitionsAsync(
            new ProcessDefinitionQuery(FirstResult: 1, MaxResults: 1), Ct);
        Assert.Single(page);
    }

    [Fact]
    public async Task UpdateTask_ShouldChangeFieldsAndCandidates()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, CandidateGroupProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        var dueDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        await engine.UpdateTaskAsync(
            task.Id,
            new TaskUpdate(
                Name: "审批（加急）",
                Description: "请优先处理",
                Priority: 50,
                DueDate: dueDate,
                Category: "urgent",
                Owner: "boss"),
            Ct);

        var updated = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(updated);
        Assert.Equal("审批（加急）", updated!.Name);
        Assert.Equal("请优先处理", updated.Description);
        Assert.Equal(50, updated.Priority);
        Assert.Equal(dueDate, updated.DueDate);
        Assert.Equal("urgent", updated.Category);
        Assert.Equal("boss", updated.Owner);

        // 清空到期时间 / 描述：显式清空标志（null 表示「不修改」，无法表达清空）
        await engine.UpdateTaskAsync(
            task.Id,
            new TaskUpdate(ClearDueDate: true, ClearDescription: true),
            Ct);

        var cleared = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.DueDate);
        Assert.Null(cleared.Description);
        Assert.Equal("审批（加急）", cleared.Name);

        // 加候选用户 → 候选组任务之外，alice 也能看到
        await engine.AddTaskCandidateUsersAsync(task.Id, ["alice"], Ct);
        var byAlice = await engine.FindTasksAsync(new TaskQuery(CandidateUser: "alice"), Ct);
        Assert.Equal(task.Id, Assert.Single(byAlice).Id);

        await engine.RemoveTaskCandidateUserAsync(task.Id, "alice", Ct);
        Assert.Empty(await engine.FindTasksAsync(new TaskQuery(CandidateUser: "alice"), Ct));

        // 候选组同样可增删
        await engine.AddTaskCandidateGroupsAsync(task.Id, ["finance"], Ct);
        Assert.Equal(task.Id, Assert.Single(await engine.FindTasksAsync(
            new TaskQuery(CandidateGroup: "finance"), Ct)).Id);

        await engine.RemoveTaskCandidateGroupAsync(task.Id, "finance", Ct);
        Assert.Empty(await engine.FindTasksAsync(new TaskQuery(CandidateGroup: "finance"), Ct));
    }

    [Fact]
    public async Task UnclaimTask_ShouldClearAssignee()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));
        Assert.Equal("kermit", task.Assignee);

        await engine.UnclaimTaskAsync(task.Id, ct: Ct);

        var unclaimed = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(unclaimed);
        Assert.Null(unclaimed!.Assignee);
        Assert.Null(unclaimed.ClaimTime);
    }

    [Fact]
    public async Task DelegateAndResolve_ShouldPendThenRestoreOwner()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, SingleTaskProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        Assert.Equal("kermit", task.Assignee);

        await engine.DelegateTaskAsync(task.Id, "gonzo", Ct);

        var delegated = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(delegated);
        Assert.Equal("gonzo", delegated!.Assignee);
        Assert.Equal("kermit", delegated.Owner);
        Assert.Equal("PENDING", delegated.DelegationStateText);

        // 委派中的任务不能直接完成（与 Activiti 一致）
        await Assert.ThrowsAnyAsync<EngineException>(() => engine.CompleteTaskAsync(task.Id, ct: Ct).AsTask());

        // 重复委派被拒绝
        await Assert.ThrowsAnyAsync<EngineException>(() => engine.DelegateTaskAsync(task.Id, "fozzie", Ct).AsTask());

        await engine.ResolveTaskAsync(task.Id, Ct);

        var resolved = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(resolved);
        Assert.Equal("kermit", resolved!.Assignee);
        Assert.Null(resolved.Owner);
        Assert.Equal("RESOLVED", resolved.DelegationStateText);

        // 解决后可以正常完成
        await engine.CompleteTaskAsync(task.Id, ct: Ct);
        Assert.Null(await engine.GetTaskAsync(task.Id, Ct));
    }

    [Fact]
    public async Task ValidateAsync_ShouldReportProblemsWithoutDeploying()
    {
        var (engine, host) = Create();

        IReadOnlyList<ValidationError> errors;

        using (var valid = new MemoryStream(Encoding.UTF8.GetBytes(SingleTaskProcess)))
        {
            errors = await engine.ValidateAsync(valid, Ct);
        }

        Assert.DoesNotContain(errors, error => error.IsError);

        // 没有结束事件 → 提示级或阻断级，总之必须报出来
        const string missingEnd = """
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="facade-no-end" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" activiti:assignee="kermit" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
              </process>
            </definitions>
            """;

        using (var invalid = new MemoryStream(Encoding.UTF8.GetBytes(missingEnd)))
        {
            var results = await engine.ValidateAsync(invalid, Ct);
            Assert.NotEmpty(results);
        }

        // 试校验不落库
        Assert.Empty(await engine.FindProcessDefinitionsAsync(ct: Ct));
    }

    [Fact]
    public async Task ExpectedRevision_ShouldRejectStaleWrites()
    {
        var (engine, host) = Create();
        var definition = await DeployAsync(engine, host, CandidateGroupProcess);
        var instanceId = await engine.StartProcessInstanceAsync(definition.Id, ct: Ct);
        var task = Assert.Single(await engine.FindTasksAsync(new TaskQuery(ProcessInstanceId: instanceId), Ct));

        // 内存实现返回的是同一个实体引用，认领会就地改 Revision，所以先快照
        var initialRevision = task.Revision;

        // 认领：过期版本被拒
        await Assert.ThrowsAsync<OptimisticLockingException>(() =>
            engine.ClaimTaskAsync(task.Id, "alice", initialRevision - 1, Ct).AsTask());

        // 正确版本可以认领
        await engine.ClaimTaskAsync(task.Id, "alice", initialRevision, Ct);

        var claimed = await engine.GetTaskAsync(task.Id, Ct);
        Assert.NotNull(claimed);
        Assert.True(claimed!.Revision > initialRevision);

        // 完成：再次用过期的旧版本（认领前）被拒
        await Assert.ThrowsAsync<OptimisticLockingException>(() =>
            engine.CompleteTaskAsync(task.Id, null, initialRevision, Ct).AsTask());

        // 用当前版本可以完成
        await engine.CompleteTaskAsync(task.Id, null, claimed!.Revision, Ct);
        Assert.Null(await engine.GetTaskAsync(task.Id, Ct));
    }

    // ─────────────────────────── 装配 ───────────────────────────

    private static (IProcessEngine Engine, TestEngine Host) Create(IHistoryManager? history = null)
    {
        var host = TestEngine.Create(history);

        var store = new InMemoryProcessDefinitionStore(new InMemoryIdGenerator(), new BpmnXmlConverter());

        // 门面与测试宿主共用同一套执行树 / 任务表：命令与查询看到的是同一份运行态
        var engine = new ProcessEngine(
            host.CommandExecutor,
            new BpmnXmlConverter(),
            new ProcessValidator(),
            store,
            host.TaskManager,
            new EmptyUserGroupManager(),
            host.ExecutionManager,
            history ?? new NullHistoryManager());

        return (engine, host);
    }

    /// <summary>部署，并把模型登记到引擎侧的解析器（门面启动实例时按定义 id 找模型）。</summary>
    private static async Task<ProcessDefinition> DeployAsync(IProcessEngine engine, TestEngine host, string xml)
    {
        var definition = await engine
            .DeployAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "facade.bpmn20.xml", ct: Ct)
            .ConfigureAwait(false);

        host.Resolver.Register(definition.Id, Parse(xml));

        return definition;
    }

    private static Process Parse(string xml) =>
        new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            .GetMainProcess()!;
}
