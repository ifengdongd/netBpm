using System.Text;
using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 实例 modify（取消活动 / 活动前启动）× 双库：MySQL 与 SqlServer 各跑一遍。
/// <para>
/// 关注点是「运行态与历史必须一起收口」：取消活动后
/// <c>ACT_RU_EXECUTION</c> / <c>ACT_RU_TASK</c> 不能留孤儿行，任务历史要带删除原因，
/// 实例结束要写 <c>END_TIME_</c>；并发取消同一活动只能有一方真正生效。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class ActivityModificationDualDatabaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>两个方言都参与；不可用的在用例内动态跳过。</summary>
    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    private const string TwoStepProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="modify-two-step" name="modify 两级审批" isExecutable="true">
            <startEvent id="start" />
            <userTask id="leader_approve" name="组长审批" activiti:candidateGroups="leaders" />
            <userTask id="manager_approve" name="经理审批" activiti:candidateGroups="managers" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="leader_approve" />
            <sequenceFlow id="f2" sourceRef="leader_approve" targetRef="manager_approve" />
            <sequenceFlow id="f3" sourceRef="manager_approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private static TestDatabaseSpec? ResolveProvider(string provider) => provider switch
    {
        "mysql" => TestDatabases.MySql,
        "sqlserver" => TestDatabases.SqlServer,
        _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
    };

    private static DatabaseEngineHarness Setup(string provider)
    {
        var spec = ResolveProvider(provider);
        Assert.SkipUnless(
            spec is not null,
            TestDatabases.UnavailableReason(provider));

        var harness = new DatabaseEngineHarness(spec!);
        harness.Reset();

        return harness;
    }

    private static async Task<(DatabaseEngineHarness Harness, string InstanceId)> StartInstanceAsync(
        string provider)
    {
        var harness = Setup(provider);
        var model = new BpmnXmlConverter()
            .ConvertToBpmnModel(new MemoryStream(Encoding.UTF8.GetBytes(TwoStepProcess)));

        var definition = await harness.Definitions.DeployAsync(model, "modify.bpmn20.xml", ct: Ct);

        var instanceId = await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(
                model.GetMainProcess()!,
                definition.Id,
                "BIZ-MODIFY",
                "tester"),
            Ct);

        return (harness, instanceId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancelActivity_ShouldCleanRuntime_AndCloseHistory(string provider)
    {
        var (harness, instanceId) = await StartInstanceAsync(provider);

        Assert.Equal(1, harness.CountRows("ACT_RU_TASK"));

        await harness.Executor.ExecuteAsync(
            new CancelActivityCommand(instanceId, "leader_approve", "集成测试取消"),
            Ct);

        // 运行态：执行树与待办都不能留孤儿行
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));

        using var session = harness.SessionFactory.Create();

        // 历史：任务被删除带原因、实例已结束。
        // 刻意不在 SQL 里 LIKE 中文：无 N 前缀的 '中文' 字面量在非中文 collation
        // （CI 容器的默认 SQL_Latin1_General_CP1_CI_AS）下会被当 varchar 转成 ?，
        // pattern 变成 %?????%，永远匹配不到；而本机 Chinese_PRC_CI_AS（代码页 936）
        // 恰好能表示中文，所以这个坑只在 CI 暴露。读回值在 C# 里判断，
        // 断言语义也就不再取决于数据库 collation。
        var deleteReasons = session.Connection
            .Query<string>("select DELETE_REASON_ from ACT_HI_TASKINST where DELETE_REASON_ is not null")
            .ToList();

        Assert.Single(deleteReasons, reason => reason.Contains("集成测试取消", StringComparison.Ordinal));

        var endedInstances = session.Connection.ExecuteScalar<long>(
            "select count(*) from ACT_HI_PROCINST where ID_ = @id and END_TIME_ is not null",
            new { id = instanceId });
        Assert.Equal(1, endedInstances);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StartBeforeActivity_ShouldAddTaskBranch_AndKeepInstanceRunning(string provider)
    {
        var (harness, instanceId) = await StartInstanceAsync(provider);

        await harness.Executor.ExecuteAsync(
            new StartBeforeActivityCommand(instanceId, "manager_approve"),
            Ct);

        var tasks = await harness.TaskManager.FindTasksByProcessInstanceIdAsync(instanceId, Ct);

        // 原组长待办保留，新增一条经理待办（两条分支并存）
        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, task => task.TaskDefinitionKey == "leader_approve");
        Assert.Contains(tasks, task => task.TaskDefinitionKey == "manager_approve");

        // 实例仍在运行（start-before 不结束实例）
        Assert.Equal(2, harness.CountRows("ACT_RU_TASK"));
        Assert.NotNull(await harness.Executions.FindByIdAsync(instanceId, Ct));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CancelActivity_Concurrently_ShouldApplyExactlyOnce(string provider)
    {
        var (harness, instanceId) = await StartInstanceAsync(provider);

        async Task<bool> TryCancelAsync()
        {
            try
            {
                await harness.Executor.ExecuteAsync(
                    new CancelActivityCommand(instanceId, "leader_approve", "并发取消"),
                    Ct);

                return true;
            }
            catch (EngineException)
            {
                // 输给并发的另一方：实例/活动已不存在，属于预期结果
                return false;
            }
        }

        var results = await Task.WhenAll(TryCancelAsync(), TryCancelAsync());

        // 恰好一方生效：不会重复收口历史，也不会重复删待办
        Assert.Equal(1, results.Count(success => success));
        Assert.Equal(0, harness.CountRows("ACT_RU_EXECUTION"));
        Assert.Equal(0, harness.CountRows("ACT_RU_TASK"));

        using var session = harness.SessionFactory.Create();

        // 同上：不在 SQL 里 LIKE 中文，否则断言结论取决于数据库 collation
        var deleteReasons = session.Connection
            .Query<string>("select DELETE_REASON_ from ACT_HI_TASKINST where DELETE_REASON_ is not null")
            .ToList();

        Assert.Single(deleteReasons, reason => reason.Contains("并发取消", StringComparison.Ordinal));

        var endedInstances = session.Connection.ExecuteScalar<long>(
            "select count(*) from ACT_HI_PROCINST where ID_ = @id and END_TIME_ is not null",
            new { id = instanceId });
        Assert.Equal(1, endedInstances);
    }
}
