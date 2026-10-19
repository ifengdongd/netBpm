using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Deployment;
using NetBpm.Runtime;

namespace NetBpm.UnitTests;

/// <summary>
/// B2.4（变量删除 / 任务局部变量）与 B2.3（多租户）的门面与命令语义。
/// 真库级验收（TENANT_ID_ 列 / TASK_ID_ 行 / 历史行保留）见 NetBpm.IntegrationTests。
/// </summary>
public sealed class VariableDeletionAndTenantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="var-del" isExecutable="true">
            <startEvent id="start" />
            <userTask id="wait" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="wait" />
            <sequenceFlow id="f2" sourceRef="wait" targetRef="end" />
          </process>
        </definitions>
        """;

    // ─────────────────────────── B2.4a 变量删除 ───────────────────────────

    [Fact]
    public async Task RemoveVariable_ShouldDeleteVariableAndReportMiss()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(
            UserTaskProcess,
            new Dictionary<string, object?> { ["days"] = 3 },
            Ct);

        var instance = (await engine.ExecutionManager.FindByIdAsync(processInstanceId, Ct).ConfigureAwait(false))!;

        var removed = await engine.CommandExecutor
            .ExecuteAsync(new DeleteVariableCommand(processInstanceId, "days"), Ct)
            .ConfigureAwait(false);

        Assert.True(removed);
        Assert.False(await instance.HasVariableAsync("days", Ct));

        // 删除不存在的变量：无操作、返回 false
        var removedAgain = await engine.CommandExecutor
            .ExecuteAsync(new DeleteVariableCommand(processInstanceId, "days"), Ct)
            .ConfigureAwait(false);

        Assert.False(removedAgain);
    }

    [Fact]
    public async Task RemoveVariable_OnUnknownInstance_ShouldFailLoudly()
    {
        var engine = TestEngine.Create();

        await Assert.ThrowsAnyAsync<EngineException>(
            () => engine.CommandExecutor
                .ExecuteAsync(new DeleteVariableCommand("no-such-instance", "days"), Ct)
                .AsTask());
    }

    // ─────────────────────────── B2.4b 任务局部变量 ───────────────────────────

    [Fact]
    public async Task TaskVariables_ShouldBeIsolatedFromProcessScope()
    {
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(UserTaskProcess, ct: Ct);
        var task = Assert.Single(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        var instance = (await engine.ExecutionManager.FindByIdAsync(processInstanceId, Ct).ConfigureAwait(false))!;

        // 写局部变量 + 同名流程实例变量：两个作用域互不可见
        await engine.CommandExecutor
            .ExecuteAsync(new SetTaskVariableCommand(task.Id, "comment", "请尽快"), Ct)
            .ConfigureAwait(false);
        await instance.SetVariableAsync("comment", "实例级", Ct).ConfigureAwait(false);

        Assert.Equal("请尽快", await engine.TaskManager.GetTaskVariableAsync(task.Id, "comment", Ct));

        var instanceVariables = await instance.GetVariablesAsync(Ct);
        Assert.Equal("实例级", instanceVariables["comment"]);

        // 局部变量列表不含实例作用域变量
        var taskVariables = await engine.TaskManager.GetTaskVariablesAsync(task.Id, Ct);
        Assert.Equal("请尽快", taskVariables["comment"]);
        Assert.Single(taskVariables);

        // 删除局部变量
        Assert.True(await engine.TaskManager.RemoveTaskVariableAsync(task.Id, "comment", Ct));
        Assert.Null(await engine.TaskManager.GetTaskVariableAsync(task.Id, "comment", Ct));
        Assert.False(await engine.TaskManager.RemoveTaskVariableAsync(task.Id, "comment", Ct));
    }

    [Fact]
    public async Task TaskVariables_ShouldDieWithTask()
    {
        // 任务完成 → 局部变量随之清理（真库级由 ACT_RU_VARIABLE.TASK_ID_ 级联保证）
        var engine = TestEngine.Create();

        var processInstanceId = await engine.StartProcessAsync(UserTaskProcess, ct: Ct);
        var task = Assert.Single(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await engine.CommandExecutor
            .ExecuteAsync(new SetTaskVariableCommand(task.Id, "memo", "临时数据"), Ct)
            .ConfigureAwait(false);

        await engine.CompleteTaskAsync(task.Id, ct: Ct);

        Assert.Equal(0, engine.ExecutionManager.ExecutionCount);
        Assert.Empty(engine.TaskManager.Tasks);
    }

    // ─────────────────────────── B2.3 多租户 ───────────────────────────

    [Fact]
    public async Task Tenant_ShouldFlowFromDeploymentToInstance()
    {
        var store = new InMemoryProcessDefinitionStore(
            new NetBpm.Engine.InMemoryIdGenerator(),
            new BpmnXmlConverter());

        await store.DeployAsync(
            Parse(UserTaskProcess),
            "tenant.bpmn20.xml",
            tenantId: "acme",
            ct: Ct);
        await store.DeployAsync(Parse(UserTaskProcess), "default.bpmn20.xml", ct: Ct);

        // 定义列表：租户过滤 + TenantId 透出
        var acmeDefinitions = await store.FindAllAsync("acme", Ct);
        var definition = Assert.Single(acmeDefinitions);
        Assert.Equal("acme", definition.TenantId);

        var allDefinitions = await store.FindAllAsync(ct: Ct);
        Assert.Equal(2, allDefinitions.Count);
        Assert.Equal(
            new[] { "acme", null },
            allDefinitions.Select(item => item.TenantId).OrderBy(item => item is null).ToArray());

        // 同一 key 在不同租户下版本独立（各自从 1 开始）
        Assert.Equal(1, acmeDefinitions.Single().Version);
        Assert.Equal(1, (await store.FindAllAsync(ct: Ct)).Count(item => item.TenantId is null));

        // 实例继承定义租户
        var engine = TestEngine.Create();
        var command = new StartProcessInstanceCommand(
            Parse(UserTaskProcess).GetMainProcess()!,
            definition.Id,
            tenantId: await store.GetTenantIdAsync(definition.Id, Ct));
        var processInstanceId = await engine.CommandExecutor.ExecuteAsync(command, Ct).ConfigureAwait(false);

        Assert.Equal("acme", (await engine.ExecutionManager.FindByIdAsync(processInstanceId, Ct))!.TenantId);

        // 任务继承执行租户
        var task = Assert.Single(await engine.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        Assert.Equal("acme", task.TenantId);
    }

    private static NetBpm.Bpmn.Model.BpmnModel Parse(string xml) =>
        new BpmnXmlConverter().ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
}
