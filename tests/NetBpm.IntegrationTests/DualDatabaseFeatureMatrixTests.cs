using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Engine;
using NetBpm.Engine.Behavior;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Persistence;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <b>B1/B2 功能矩阵 × 双库</b>：把任务清单 B1/B2 的全部功能在 MySQL（localhost）与
/// SqlServer（Testcontainers 的 2022 镜像，或 <c>NETBPM_SQLSERVER_CONNECTION</c> 指向的实例）
/// 上各跑一遍 —— 同一组用例、同一套断言，差异只来自 <see cref="TestDatabaseSpec"/>。
/// <para>
/// 每个方言不可用时对应行<b>动态跳过</b>。行级语义的细粒度真值仍由
/// 既有 MySQL 专项测试 + 差分对拍负责；本套件回答「每个功能在两种真库上都成立」。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class DualDatabaseFeatureMatrixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        // 两个方言都参与矩阵；不可用的在用例内动态跳过（探测是惰性的）
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    private static TestDatabaseSpec? ResolveProvider(string provider) => provider switch
    {
        "mysql" => TestDatabases.MySql,
        "sqlserver" => TestDatabases.SqlServer,
        _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
    };

    // ─────────────────────────── B2.5 部署原始字节 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_5_DeployRawBytes_ShouldStoreOriginalXml(string provider)
    {
        var harness = Setup(provider);

        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- 双库矩阵：这段注释只在 BYTES_ 存原文时能存活 -->
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         targetNamespace="http://netbpm/test">
              <process id="raw-bytes" isExecutable="true">
                <startEvent id="start" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="end" />
              </process>
            </definitions>
            """;

        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var definition = await harness.Definitions.DeployAsync(
            model,
            "matrix.bpmn20.xml",
            rawResourceBytes: Encoding.UTF8.GetBytes(xml),
            ct: Ct);

        using var session = harness.SessionFactory.Create();
        var rows = RowValues.Query(
            session,
            "select BYTES_ from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is not null");

        Assert.Equal(xml, RowValues.TextFromBytes(Assert.Single(rows), "BYTES_"));
        Assert.Equal(xml, (await harness.Definitions.GetResourceXmlAsync(definition.Id, Ct))!.TrimEnd());
    }

    // ─────────────────────────── P5 + B2.4a 变量与删除 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_4_Variables_LongStringAndRemoval(string provider)
    {
        var harness = Setup(provider);

        var longText = new string('长', 2500) + new string('x', 2000); // > 4000 ⇒ longString
        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            UserTaskProcess(),
            new Dictionary<string, object?> { ["memo"] = longText, ["days"] = 3 },
            Ct);

        // 读回 longString（跨命令，字节行解码）
        var variables = await harness.VariableInstances.FindByExecutionIdAsync(processInstanceId, Ct);
        Assert.Equal(2, variables.Count);
        Assert.Equal(longText, Assert.Single(variables, variable => variable.Name == "memo").Value);

        // 删除：运行行 + 字节行消失，历史行保留
        await harness.Executor
            .ExecuteAsync(new DeleteVariableCommand(processInstanceId, "memo"), Ct)
            .ConfigureAwait(false);

        using (var session = harness.SessionFactory.Create())
        {
            var remaining = RowValues.Query(session, "select NAME_ from ACT_RU_VARIABLE");
            var remainingRow = Assert.Single(remaining);
            Assert.Equal("days", RowValues.Text(remainingRow, "NAME_"));
            Assert.Empty(RowValues.Query(
                session,
                "select ID_ from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null"));

            // 历史行只增不删：两个变量各一行（memo 的行仍指向已删除的字节行）
            Assert.Equal(2, RowValues.Query(session, "select ID_ from ACT_HI_VARINST").Count);
        }
    }

    // ─────────────────────────── B2.4b 任务局部变量 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_4_TaskLocalVariables_ShouldLiveAndDieWithTask(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);
        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await harness.Executor
            .ExecuteAsync(new SetTaskVariableCommand(task.Id, "comment", "请尽快"), Ct)
            .ConfigureAwait(false);

        Assert.Equal("请尽快", await harness.TaskManager.GetTaskVariableAsync(task.Id, "comment", Ct));
        Assert.Empty(await harness.VariableInstances.FindByExecutionIdAsync(processInstanceId, Ct));

        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        using var session = harness.SessionFactory.Create();
        Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_VARIABLE"));
        Assert.Single(RowValues.Query(session, "select ID_ from ACT_HI_VARINST"));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
    }

    // ─────────────────────────── B2.3 多租户 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_3_Tenant_ShouldPropagateToAllEngineRows(string provider)
    {
        var harness = Setup(provider);

        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(UserTaskProcess())));

        var definition = await harness.Definitions.DeployAsync(
            model,
            "tenant.bpmn20.xml",
            tenantId: "acme",
            ct: Ct);

        var processInstanceId = await harness.Executor
            .ExecuteAsync(
                new StartProcessInstanceCommand(
                    model.GetMainProcess()!,
                    definition.Id,
                    "BIZ-TENANT",
                    "tester",
                    tenantId: "acme"),
                Ct)
            .ConfigureAwait(false);

        Assert.Equal(definition.Id, Assert.Single(await harness.Definitions.FindAllAsync("acme", Ct)).Id);

        using var session = harness.SessionFactory.Create();

        foreach (var table in new[]
                 {
                     "ACT_RE_DEPLOYMENT", "ACT_RE_PROCDEF", "ACT_RU_EXECUTION", "ACT_RU_TASK",
                     "ACT_HI_PROCINST", "ACT_HI_ACTINST", "ACT_HI_TASKINST",
                 })
        {
            var tenants = RowValues.Query(session, $"select distinct TENANT_ID_ from {table}")
                .Select(row => RowValues.Text(row, "TENANT_ID_"))
                .ToList();

            Assert.True(
                tenants.Count == 1 && tenants[0] == "acme",
                $"{table} 的租户列应为 'acme'，实际：[{string.Join(", ", tenants)}]");
        }
    }

    // ─────────────────────────── B1.2 多实例（串签）───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B1_2_SequentialCountersign_ShouldCreateTasksOneByOne(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            CountersignProcess(isSequential: true),
            ct: Ct);

        for (var round = 1; round <= 3; round++)
        {
            var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
            var task = Assert.Single(tasks);

            await harness.CompleteTaskAsync(task.Id, ct: Ct);
        }

        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));

        // 完成计数已落库（3 次完成之后实例结束，行已清 —— 用历史活动行数佐证推进了 3 个实例）
        using var session = harness.SessionFactory.Create();
        Assert.Equal(3, RowValues.Query(session, "select ID_ from ACT_HI_ACTINST").Count);
    }

    // ─────────────────────────── B1.2 多实例（会签 + 完成条件）───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B1_2_ParallelCountersign_CompletionCondition_ShouldFinishEarly(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            CountersignProcess(isSequential: false, completionCondition: "${nrOfCompletedInstances == 2}"),
            ct: Ct);

        // 3 个实例任务同时在库
        var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct);
        Assert.Equal(3, tasks.Count);

        // 完成前 2 个 → 剩余实例取消、流程收口
        await harness.CompleteTaskAsync(tasks[0].Id, ct: Ct);
        await harness.CompleteTaskAsync(tasks[1].Id, ct: Ct);

        Assert.Empty(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
    }

    // ─────────────────────────── B1.1 脚本任务 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B1_1_ScriptTask_ResultVariable_ShouldPersist(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(ScriptTaskProcess(), ct: Ct);

        var variables = await harness.VariableInstances.FindByExecutionIdAsync(processInstanceId, Ct);
        Assert.Equal(42, Assert.Single(variables, variable => variable.Name == "answer").Value);
    }

    // ─────────────────────────── B2.1 字段注入 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_1_FieldInjection_ShouldInjectExpressionValue(string provider)
    {
        var harness = Setup(provider);
        var injectable = new MatrixInjectableDelegate();
        harness.Delegates["matrixInjectable"] = injectable;

        // serviceTask 之后留一个 userTask：实例不结束，注入结果可以从 ACT_RU_VARIABLE 断言
        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            FieldInjectionProcess(),
            new Dictionary<string, object?> { ["days"] = 5 },
            Ct);

        var variables = await harness.VariableInstances.FindByExecutionIdAsync(processInstanceId, Ct);
        Assert.Equal("你好:5", Assert.Single(variables, variable => variable.Name == "injected").Value);
    }

    /// <summary>字段注入的观察委托：把注入结果写进流程变量（落库可断言）。</summary>
    internal sealed class MatrixInjectableDelegate : IServiceTaskDelegate
    {
        public string? Prefix { get; set; }

        public int Count { get; set; }

        public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
            execution.SetVariableAsync("injected", $"{Prefix}:{Count}", ct);
    }

    // ─────────────────────────── B2.6 组合查询 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task B2_6_CombinedQuery_InstancePlusCandidate(string provider)
    {
        var harness = Setup(provider);

        // 会签产生的任务带候选组（identity link 落库），同一实例上有两个节点的待办
        var (_, firstInstanceId) = await harness.DeployAndStartAsync(CandidateTaskProcess(), ct: Ct);
        var (_, secondInstanceId) = await harness.DeployAndStartAsync(CandidateTaskProcess(), ct: Ct);

        var firstTasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(firstInstanceId, Ct);
        var secondTasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(secondInstanceId, Ct);
        Assert.NotEmpty(firstTasks);
        Assert.NotEmpty(secondTasks);

        // 「按实例 + 候选人」组合：实例过滤后再过候选（kermit 不在任何候选名单里 → 空）
        var candidatesOfKermit = await harness.TaskManager.FindTasksByCandidateUserAsync("kermit", ["leaders"], Ct);
        var combined = candidatesOfKermit
            .Where(task => string.Equals(task.ProcessInstanceId, firstInstanceId, StringComparison.Ordinal))
            .ToList();
        var combinedSecond = candidatesOfKermit
            .Where(task => string.Equals(task.ProcessInstanceId, secondInstanceId, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(firstTasks.Count, combined.Count);
        Assert.Equal(secondTasks.Count, combinedSecond.Count);

        // 非候选人组合查询为空
        var none = (await harness.TaskManager.FindTasksByCandidateUserAsync("nobody", ["nogroup"], Ct))
            .Where(task => string.Equals(task.ProcessInstanceId, firstInstanceId, StringComparison.Ordinal));
        Assert.Empty(none);
    }

    // ─────────────────────────── P7 核心：用户任务 + 历史 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task P7_UserTask_CompleteAndHistory(string provider)
    {
        var harness = Setup(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            UserTaskProcess(),
            new Dictionary<string, object?> { ["reason"] = "双库验收" },
            Ct);

        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await harness.ClaimTaskAsync(task.Id, "kermit", Ct);
        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));

        using var session = harness.SessionFactory.Create();

        // 历史任务行收口（END_TIME_ 已写）
        var historyTasks = RowValues.Query(session, "select ASSIGNEE_, END_TIME_ from ACT_HI_TASKINST");
        var historyTask = Assert.Single(historyTasks);
        Assert.Equal("kermit", RowValues.Text(historyTask, "ASSIGNEE_"));
        Assert.NotNull(RowValues.Raw(historyTask, "END_TIME_"));

        // 历史活动行：start + userTask + end = 3 行，全部收口
        var activities = RowValues.Query(session, "select END_TIME_ from ACT_HI_ACTINST");
        Assert.Equal(3, activities.Count);
        Assert.All(activities, row => Assert.NotNull(RowValues.Raw(row, "END_TIME_")));
    }

    // ─────────────────────────── 流程定义：最新版本 / 挂起 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Definition_LatestVersionAndSuspension_ShouldPersist(string provider)
    {
        var harness = Setup(provider);

        // 同一 KEY_ 部署两次 = 两个版本
        var (v1, _) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);
        var (v2, _) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        Assert.True(v2.Version > v1.Version);

        // max(VERSION_) 子查询必须是两方言通用写法（不能用 limit / top）
        var latest = await harness.Definitions.FindLatestByKeyAsync(v1.Key, ct: Ct);
        Assert.Equal(v2.Id, latest!.Id);

        // 挂起位落库：换会话读回（ACT_RE_PROCDEF.SUSPENSION_STATE_ = 2）
        Assert.True(await harness.Definitions.SetSuspensionStateAsync(v2.Id, suspended: true, Ct));
        Assert.True(await harness.Definitions.IsSuspendedAsync(v2.Id, Ct));

        var listed = (await harness.Definitions.FindAllAsync(ct: Ct)).Single(row => row.Id == v2.Id);
        Assert.True(listed.IsSuspended);

        // 定义级挂起：旧版本不受影响
        Assert.False(await harness.Definitions.IsSuspendedAsync(v1.Id, Ct));

        // 激活后就恢复
        Assert.True(await harness.Definitions.SetSuspensionStateAsync(v2.Id, suspended: false, Ct));
        Assert.False(await harness.Definitions.IsSuspendedAsync(v2.Id, Ct));

        // 不存在的定义：返回 false（由门面转成 EngineException），不能静默成功
        Assert.False(await harness.Definitions.SetSuspensionStateAsync("no-such-definition", true, Ct));
    }

    // ─────────────────────────── 删除流程实例 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteProcessInstance_ShouldPurgeRuntimeAndKeepHistory(string provider)
    {
        var harness = Setup(provider);
        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        await harness.Executor.ExecuteAsync(
            new DeleteProcessInstanceCommand(processInstanceId, "双库删除验收"), Ct);

        // 运行态：执行树与待办清空；历史仍在
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));

        using var session = harness.SessionFactory.Create();

        var instance = Assert.Single(RowValues.Query(
            session, "select DELETE_REASON_, END_ACT_ID_ from ACT_HI_PROCINST"));
        Assert.Equal("双库删除验收", RowValues.Text(instance, "DELETE_REASON_"));

        // Activiti 的删除命令传 currentFlowElementId = null ⇒ END_ACT_ID_ 为空
        Assert.Null(RowValues.Raw(instance, "END_ACT_ID_"));

        // 被删待办的历史行同样带删除原因
        var taskReasons = RowValues.Column(
            RowValues.Query(session, "select DELETE_REASON_ from ACT_HI_TASKINST"),
            "DELETE_REASON_");
        Assert.Contains("双库删除验收", taskReasons);
    }

    // ─────────────────────────── 实例级挂起 / 定义挂起带实例 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ProcessInstanceSuspension_ShouldSuspendExecutionAndTaskRows(string provider)
    {
        var harness = Setup(provider);
        var (_, instanceId) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        await harness.Engine.SuspendProcessInstanceAsync(instanceId, Ct);

        string taskId;

        using (var session = harness.SessionFactory.Create())
        {
            // 执行树与任务行都必须挂起（漏一张表就是「实例已挂起、待办却能完成」）
            var executions = RowValues.Query(session, "select SUSPENSION_STATE_ from ACT_RU_EXECUTION");
            Assert.NotEmpty(executions);
            Assert.All(executions, row => Assert.Equal(2, RowValues.Int(row, "SUSPENSION_STATE_")));

            var task = Assert.Single(RowValues.Query(session, "select ID_, SUSPENSION_STATE_ from ACT_RU_TASK"));
            Assert.Equal(2, RowValues.Int(task, "SUSPENSION_STATE_"));
            taskId = RowValues.Text(task, "ID_")!;
        }

        var exception = await Assert.ThrowsAnyAsync<EngineException>(
            () => harness.CompleteTaskAsync(taskId, ct: Ct));
        Assert.Contains("Cannot complete a suspended task", exception.Message, StringComparison.Ordinal);

        await harness.Engine.ActivateProcessInstanceAsync(instanceId, Ct);
        await harness.CompleteTaskAsync(taskId, ct: Ct);

        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SuspendDefinition_WithInstances_ShouldBlockStartAndCompletion(string provider)
    {
        var harness = Setup(provider);
        var (definition, _) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        await harness.Engine.SuspendProcessDefinitionAsync(definition.Id, suspendProcessInstances: true, Ct);

        using (var session = harness.SessionFactory.Create())
        {
            var definitionRow = Assert.Single(RowValues.Query(
                session, "select SUSPENSION_STATE_ from ACT_RE_PROCDEF"));
            Assert.Equal(2, RowValues.Int(definitionRow, "SUSPENSION_STATE_"));

            var taskRow = Assert.Single(RowValues.Query(session, "select SUSPENSION_STATE_ from ACT_RU_TASK"));
            Assert.Equal(2, RowValues.Int(taskRow, "SUSPENSION_STATE_"));
        }

        // 定义挂起后不能再按 key 启动
        var startException = await Assert.ThrowsAnyAsync<EngineException>(
            () => harness.Engine.StartProcessInstanceByKeyAsync(definition.Key, ct: Ct).AsTask());
        Assert.Contains("已挂起", startException.Message, StringComparison.Ordinal);

        await harness.Engine.ActivateProcessDefinitionAsync(definition.Id, activateProcessInstances: true, Ct);

        string taskId;

        using (var session = harness.SessionFactory.Create())
        {
            var taskRow = Assert.Single(RowValues.Query(session, "select ID_, SUSPENSION_STATE_ from ACT_RU_TASK"));
            Assert.Equal(1, RowValues.Int(taskRow, "SUSPENSION_STATE_"));
            taskId = RowValues.Text(taskRow, "ID_")!;
        }

        await harness.CompleteTaskAsync(taskId, ct: Ct);
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));
    }

    // ─────────────────────────── 删除部署 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteDeployment_WithoutCascade_ShouldRefuse_WithCascade_ShouldPurge(string provider)
    {
        var harness = Setup(provider);
        var (definition, _) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        // 先预热模型缓存：验证删除部署时缓存被清（否则按已删 id 启动会命中缓存、最终撞外键）
        Assert.NotNull(await harness.Definitions.GetBpmnModelAsync(definition.Id, Ct));

        // 非级联：仍有运行实例 → 拒绝，且不能有副作用
        var refuse = await Assert.ThrowsAnyAsync<EngineException>(
            () => harness.Engine.DeleteDeploymentAsync(definition.DeploymentId, ct: Ct).AsTask());
        Assert.Contains("cascade=true", refuse.Message, StringComparison.Ordinal);
        Assert.Equal(1, harness.CountRows("ACT_RE_PROCDEF"));

        await harness.Engine.DeleteDeploymentAsync(definition.DeploymentId, cascade: true, Ct);

        Assert.Equal(0, harness.CountRows("ACT_RE_PROCDEF"));
        Assert.Equal(0, harness.CountRows("ACT_RE_DEPLOYMENT"));
        Assert.Equal(0, harness.CountRows("ACT_GE_BYTEARRAY"));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));

        // 历史保留（与 Activiti 的有意差异）：实例行带删除原因
        using (var session = harness.SessionFactory.Create())
        {
            var instance = Assert.Single(RowValues.Query(session, "select DELETE_REASON_ from ACT_HI_PROCINST"));
            Assert.Equal("删除部署", RowValues.Text(instance, "DELETE_REASON_"));
        }

        // 已删的定义不能再启动（缓存已清 → 报「不存在」而不是外键错误）
        var startException = await Assert.ThrowsAnyAsync<EngineException>(
            () => harness.Engine.StartProcessInstanceAsync(definition.Id, ct: Ct).AsTask());
        Assert.Contains("不存在", startException.Message, StringComparison.Ordinal);

        // 再删一次 → 专门的「不存在」异常（REST 映射 404 的依据）
        await Assert.ThrowsAsync<EngineObjectNotFoundException>(
            () => harness.Engine.DeleteDeploymentAsync(definition.DeploymentId, ct: Ct).AsTask());
    }

    // ─────────────────────────── 历史任务分页 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HistoricTasks_ShouldPageAndFilter(string provider)
    {
        var harness = Setup(provider);
        var (_, instanceId) = await harness.DeployAndStartAsync(UserTaskProcess(), ct: Ct);

        string taskId;

        using (var session = harness.SessionFactory.Create())
        {
            taskId = RowValues.Text(Assert.Single(RowValues.Query(session, "select ID_ from ACT_RU_TASK")), "ID_")!;
        }

        await harness.CompleteTaskAsync(taskId, ct: Ct);

        var all = await harness.Engine.FindHistoricTasksAsync(new HistoricTaskQuery(instanceId), Ct);
        Assert.Equal(taskId, Assert.Single(all).Id);

        var finished = await harness.Engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, OnlyFinished: true), Ct);
        Assert.NotNull(Assert.Single(finished).EndTime);

        Assert.Equal(1, await harness.Engine.CountHistoricTasksAsync(new HistoricTaskQuery(instanceId), Ct));

        // 越界页返回空；办理人过滤按历史行取值
        Assert.Empty(await harness.Engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, FirstResult: 1), Ct));
        Assert.Single(await harness.Engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, Assignee: "kermit"), Ct));
        Assert.Empty(await harness.Engine.FindHistoricTasksAsync(
            new HistoricTaskQuery(instanceId, Assignee: "someone-else"), Ct));
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static DatabaseEngineHarness Setup(string provider)
    {
        var spec = ResolveProvider(provider);
        Assert.SkipUnless(
            spec is not null,
            $"方言 {provider} 不可用（NETBPM_MYSQL_CONNECTION / NETBPM_SQLSERVER_CONNECTION 或默认目标："
            + "MySQL=localhost，SqlServer=Testcontainers mssql/server:2022；需要 Docker 正在运行）。"
            + "库与表会自动创建。");

        var harness = new DatabaseEngineHarness(spec!);
        harness.Reset();

        return harness;
    }

    private static string UserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="matrix-user" isExecutable="true">
            <startEvent id="start" />
            <userTask id="wait" name="等待" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="wait" />
            <sequenceFlow id="f2" sourceRef="wait" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string CountersignProcess(bool isSequential, string? completionCondition = null)
    {
        var condition = completionCondition is null
            ? string.Empty
            : $"\n        <completionCondition>{completionCondition}</completionCondition>";

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="matrix-mi" isExecutable="true">
                <startEvent id="start" />
                <userTask id="sign" name="会签" activiti:assignee="kermit">
                  <multiInstanceLoopCharacteristics isSequential="{{(isSequential ? "true" : "false")}}">
                    <loopCardinality>3</loopCardinality>{{condition}}
                  </multiInstanceLoopCharacteristics>
                </userTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="sign" />
                <sequenceFlow id="f2" sourceRef="sign" targetRef="end" />
              </process>
            </definitions>
            """;
    }

    private static string ScriptTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="matrix-script" isExecutable="true">
            <startEvent id="start" />
            <scriptTask id="calc" scriptFormat="csharp" activiti:resultVariable="answer">
              <script><![CDATA[var answer = 6 * 7;
        answer]]></script>
            </scriptTask>
            <userTask id="wait" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="calc" />
            <sequenceFlow id="f2" sourceRef="calc" targetRef="wait" />
            <sequenceFlow id="f3" sourceRef="wait" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string FieldInjectionProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="matrix-field" isExecutable="true">
            <startEvent id="start" />
            <serviceTask id="work" activiti:delegateExpression="matrixInjectable">
              <extensionElements>
                <activiti:field name="Prefix" stringValue="你好" />
                <activiti:field name="Count" expression="${days}" />
              </extensionElements>
            </serviceTask>
            <userTask id="wait" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
            <sequenceFlow id="f2" sourceRef="work" targetRef="wait" />
            <sequenceFlow id="f3" sourceRef="wait" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string CandidateTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="matrix-candidate" isExecutable="true">
            <startEvent id="start" />
            <userTask id="review" name="评审" activiti:candidateGroups="leaders" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="review" />
            <sequenceFlow id="f2" sourceRef="review" targetRef="end" />
          </process>
        </definitions>
        """;
}
