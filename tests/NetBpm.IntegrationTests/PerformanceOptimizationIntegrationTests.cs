using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Persistence.Database;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P1 数据库与性能改动的回归（MySQL + SqlServer）：
/// <list type="bullet">
///   <item><description>运行期查询索引确实存在（0004_perf_indexes.sql）；</description></item>
///   <item><description>流程定义模型按 id 缓存，多次启动只解析一次 XML；</description></item>
///   <item><description>候选关系批量写入（20 人 = 20 候选 + 20 参与者，一次多行插入）；</description></item>
///   <item><description>历史行收口复用内存起始时间后，耗时列仍然正确。</description></item>
/// </list>
/// </summary>
[Collection("MySql")]
public sealed class PerformanceOptimizationIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static IEnumerable<TheoryDataRow<string>> Providers()
    {
        yield return new TheoryDataRow<string>("mysql");
        yield return new TheoryDataRow<string>("sqlserver");
    }

    // ─────────────────────────── 索引 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RuntimeQueryIndexes_ShouldExist(string provider)
    {
        var harness = await SetupAsync(provider);
        using var session = harness.SessionFactory.Create();

        foreach (var index in ExpectedIndexes(provider))
        {
            var exists = provider == "mysql"
                ? await session.Connection.ExecuteScalarAsync<long>(
                    "select count(*) from information_schema.statistics " +
                    "where table_schema = database() and index_name = @index",
                    new { index })
                : await session.Connection.ExecuteScalarAsync<long>(
                    "select count(*) from sys.indexes where name = @index",
                    new { index });

            Assert.True(
                exists > 0,
                $"缺少索引 {index}。已初始化的库请执行 db/{provider}/0004_perf_indexes.sql 后再跑本用例。");
        }
    }

    // ─────────────────────────── 定义模型缓存 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task BpmnModel_ShouldBeParsedOncePerDefinition(string provider)
    {
        var converter = new CountingXmlConverter(new BpmnXmlConverter());
        var harness = await SetupAsync(provider, xmlConverter: converter);

        var (definition, _) = await harness.DeployAndStartAsync(SingleUserTaskProcess(), Ct);

        // 部署（调用方解析）+ 启动过程中事件分发首次装载解析结果
        var afterStart = converter.ParseCalls;
        Assert.True(afterStart >= 1);

        // 之后所有读取都命中缓存：不再查 BYTES_、不再解析
        for (var i = 0; i < 5; i++)
        {
            var model = await harness.Definitions.GetBpmnModelAsync(definition.Id, Ct);
            Assert.NotNull(model);
            Assert.NotNull(await harness.Definitions.GetTenantIdAsync(definition.Id, Ct));
        }

        Assert.Equal(afterStart, converter.ParseCalls);
    }

    // ─────────────────────────── 候选关系批量化 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CandidateUsers_ShouldBePersistedAndQueryable(string provider)
    {
        const int candidateCount = 20;
        var harness = await SetupAsync(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            CandidateUsersProcess(candidateCount),
            Ct);

        var task = Assert.Single(
            await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        using var session = harness.SessionFactory.Create();

        var candidateLinks = await session.Connection.ExecuteScalarAsync<long>(
            "select count(*) from ACT_RU_IDENTITYLINK where TASK_ID_ = @taskId and TYPE_ = 'candidate'",
            new { taskId = task.Id });

        var participantLinks = await session.Connection.ExecuteScalarAsync<long>(
            "select count(*) from ACT_RU_IDENTITYLINK where PROC_INST_ID_ = @processInstanceId and TYPE_ = 'participant'",
            new { processInstanceId });

        Assert.Equal(candidateCount, candidateLinks);
        Assert.Equal(candidateCount, participantLinks);

        // 历史侧逐条照记（批量的是运行关系行，历史语义不变）
        var historicLinks = await session.Connection.ExecuteScalarAsync<long>(
            "select count(*) from ACT_HI_IDENTITYLINK where TASK_ID_ = @taskId",
            new { taskId = task.Id });

        Assert.Equal(candidateCount, historicLinks);

        // 候选人查询仍然可用
        var byCandidate = await harness.TaskManager.FindTasksByCandidateUserAsync(
            "u07",
            Array.Empty<string>(),
            Ct);

        Assert.Single(byCandidate);
    }

    // ─────────────────────────── 历史耗时 ───────────────────────────

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CompletedTask_ShouldStillRecordDurations(string provider)
    {
        var harness = await SetupAsync(provider);

        var (_, processInstanceId) = await harness.DeployAndStartAsync(SingleUserTaskProcess(), Ct);
        var task = Assert.Single(
            await harness.TaskManager.FindTasksByProcessInstanceIdAsync(processInstanceId, Ct));

        await harness.CompleteTaskAsync(task.Id, ct: Ct);

        using var session = harness.SessionFactory.Create();

        // 收口时不再 SELECT START_TIME_，但 DURATION_ 必须仍然是有效值
        await AssertDurationAsync(session, "select DURATION_ from ACT_HI_TASKINST");
        await AssertDurationAsync(session, "select DURATION_ from ACT_HI_ACTINST where ACT_ID_ = 'approve'");
        await AssertDurationAsync(session, "select DURATION_ from ACT_HI_PROCINST");
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async Task AssertDurationAsync(IDbSession session, string sql)
    {
        var duration = await session.Connection.ExecuteScalarAsync<long?>(sql);

        Assert.NotNull(duration);
        Assert.True(duration >= 0, $"耗时列不应为负：{duration}");
    }

    private static IReadOnlyList<string> ExpectedIndexes(string provider)
    {
        string[] common =
        [
            "ACT_IDX_TASK_ASSIGNEE",
            "ACT_IDX_VARIABLE_EXEC_NAME",
            "ACT_IDX_EXEC_ACT_PROCINST_ACTIVE",
        ];

        if (!string.Equals(provider, "mysql", StringComparison.Ordinal))
        {
            return common;
        }

        // MySQL 的 0001 索引清单比 SQL Server 精简，0009 补齐这些
        return
        [
            .. common,
            "ACT_IDX_EXEC_PROC_INST_PARENT",
            "ACT_IDX_EXEC_PARENT",
            "ACT_IDX_TASK_PROCINST",
            "ACT_IDX_TASK_EXEC",
            "ACT_IDX_VARIABLE_PROCINST",
            "ACT_IDX_IDENT_LNK_PROCINST",
        ];
    }

    private static Task<DatabaseEngineHarness> SetupAsync(
        string provider,
        IBpmnXmlConverter? xmlConverter = null)
    {
        var spec = provider switch
        {
            "mysql" => TestDatabases.MySql,
            "sqlserver" => TestDatabases.SqlServer,
            _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
        };

        Assert.SkipUnless(spec is not null, $"方言 {provider} 不可用。");

        var harness = new DatabaseEngineHarness(spec!, xmlConverter: xmlConverter);
        harness.Reset();

        return Task.FromResult(harness);
    }

    private static string SingleUserTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="perf-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" name="审批" activiti:assignee="kermit" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string CandidateUsersProcess(int candidateCount)
    {
        var users = string.Join(",", Enumerable.Range(1, candidateCount).Select(index => $"u{index:D2}"));

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="perf-candidates" isExecutable="true">
                <startEvent id="start" />
                <userTask id="approve" name="会签" activiti:candidateUsers="{{users}}" />
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
                <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
              </process>
            </definitions>
            """;
    }

    private sealed class CountingXmlConverter(IBpmnXmlConverter inner) : IBpmnXmlConverter
    {
        private int _parseCalls;

        public int ParseCalls => Volatile.Read(ref _parseCalls);

        public BpmnModel ConvertToBpmnModel(Stream xmlStream)
        {
            Interlocked.Increment(ref _parseCalls);
            return inner.ConvertToBpmnModel(xmlStream);
        }

        public void ConvertToXml(BpmnModel bpmnModel, Stream outputStream) =>
            inner.ConvertToXml(bpmnModel, outputStream);
    }
}
