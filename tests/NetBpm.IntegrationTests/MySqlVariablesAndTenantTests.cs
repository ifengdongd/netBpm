using System.Text;
using NetBpm.Bpmn.Converter;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence.Database;

namespace NetBpm.IntegrationTests;

/// <summary>
/// B2.4（变量删除 / 任务局部变量）与 B2.3（多租户）的库级验收。
/// <para>
/// 历史语义（有意为之）：变量删除 / 任务级联只删运行行与字节行，<c>ACT_HI_VARINST</c> 行保留。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class MySqlVariablesAndTenantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string UserTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="b24" isExecutable="true">
            <startEvent id="start" />
            <userTask id="wait" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="wait" />
            <sequenceFlow id="f2" sourceRef="wait" targetRef="end" />
          </process>
        </definitions>
        """;

    [Fact]
    public async Task RemoveVariable_ShouldCascadeBytesButKeepHistoryRow()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        // longString 变量（> 4000 字符触发降级）：删除时字节行必须一并清理
        var longText = new string('长', 2500) + new string('x', 2000);
        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            UserTaskProcess,
            new Dictionary<string, object?> { ["memo"] = longText },
            Ct);

        await harness.Executor
            .ExecuteAsync(new DeleteVariableCommand(processInstanceId, "memo"), Ct)
            .ConfigureAwait(false);

        using (var session = harness.SessionFactory.Create())
        {
            // 运行行与字节行都消失
            Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_VARIABLE"));
            Assert.Empty(RowValues.Query(
                session,
                "select ID_ from ACT_GE_BYTEARRAY where DEPLOYMENT_ID_ is null"));

            // 历史行保留（只增不删），且仍指向已删除的字节行（审计痕迹）
            var historyRows = RowValues.Query(session, "select VAR_TYPE_, BYTEARRAY_ID_ from ACT_HI_VARINST");
            var historyRow = Assert.Single(historyRows);

            Assert.Equal("longString", RowValues.Text(historyRow, "VAR_TYPE_"));
            Assert.False(string.IsNullOrEmpty(RowValues.Text(historyRow, "BYTEARRAY_ID_")));
        }

        // 实例仍在运行（变量删除不影响执行树）
        Assert.NotNull(await harness.Executions.FindByIdAsync(processInstanceId, Ct));
    }

    [Fact]
    public async Task TaskVariables_ShouldLiveAndDieWithTaskRow()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var (_, processInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess, ct: Ct);
        var task = Assert.Single(await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await harness.Executor
            .ExecuteAsync(new SetTaskVariableCommand(task.Id, "comment", "请尽快"), Ct)
            .ConfigureAwait(false);

        using (var session = harness.SessionFactory.Create())
        {
            // TASK_ID_ 行存在；EXECUTION_ID_ 变量查询（TASK_ID_ is null）不含它
            var rows = RowValues.Query(session, "select NAME_, TEXT_, TASK_ID_ from ACT_RU_VARIABLE");
            var row = Assert.Single(rows);

            Assert.Equal("comment", RowValues.Text(row, "NAME_"));
            Assert.Equal("请尽快", RowValues.Text(row, "TEXT_"));
            Assert.Equal(task.Id, RowValues.Text(row, "TASK_ID_"));

            var executionVariables = await harness.VariableInstances
                .FindByExecutionIdAsync(processInstanceId, Ct);
            Assert.Empty(executionVariables);
        }

        // 完成任务：局部变量行级联消失，历史行保留
        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        using (var session = harness.SessionFactory.Create())
        {
            Assert.Empty(RowValues.Query(session, "select ID_ from ACT_RU_VARIABLE"));
            Assert.Single(RowValues.Query(session, "select ID_ from ACT_HI_VARINST"));
        }

        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
    }

    [Fact]
    public async Task Tenant_ShouldPropagateToAllEngineRows()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        var harness = new DatabaseEngineHarness();
        harness.Reset();

        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(UserTaskProcess)));

        var definition = await harness.Definitions.DeployAsync(
            model,
            "tenant.bpmn20.xml",
            tenantId: "acme",
            ct: Ct);

        var process = model.GetMainProcess()!;
        var processInstanceId = await harness.Executor
            .ExecuteAsync(
                new StartProcessInstanceCommand(process, definition.Id, "BIZ-TENANT", "tester", tenantId: "acme"),
                Ct)
            .ConfigureAwait(false);

        // 定义列表按租户过滤
        var acmeDefinitions = await harness.Definitions.FindAllAsync("acme", Ct);
        Assert.Equal(definition.Id, Assert.Single(acmeDefinitions).Id);
        Assert.Empty(await harness.Definitions.FindAllAsync("other", Ct));

        using (var session = harness.SessionFactory.Create())
        {
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_RE_DEPLOYMENT").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_RE_PROCDEF").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_RU_EXECUTION").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_RU_TASK").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_HI_PROCINST").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_HI_ACTINST").Single());
            Assert.Equal("acme", TenantsOf(session, "select TENANT_ID_ from ACT_HI_TASKINST").Single());
        }

        // 无租户部署：全链路仍是 ''（对拍兼容的缺省路径）
        var (_, defaultInstanceId) = await harness.DeployAndStartAsync(UserTaskProcess, ct: Ct);

        using (var session = harness.SessionFactory.Create())
        {
            Assert.Equal(string.Empty, TenantsOf(
                session,
                "select TENANT_ID_ from ACT_RU_EXECUTION where ID_ = '" + defaultInstanceId + "'").Single());
        }
    }

    private static IReadOnlyList<string?> TenantsOf(IDbSession session, string sql) =>
        RowValues.Query(session, sql)
            .Select(row => RowValues.Text(row, "TENANT_ID_"))
            .Distinct()
            .ToList();
}
