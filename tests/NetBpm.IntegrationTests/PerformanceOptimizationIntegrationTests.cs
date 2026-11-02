using System.Globalization;
using System.Text;
using Dapper;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Model;
using NetBpm.Engine;
using NetBpm.Engine.Command;
using NetBpm.Persistence;
using NetBpm.Persistence.Database;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P1 数据库与性能改动的回归（MySQL + SqlServer）：
/// <list type="bullet">
///   <item><description>运行期查询索引确实存在（netbpm_init.sql）；</description></item>
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
                $"缺少索引 {index}。请先执行 db/{provider}/netbpm_init.sql（幂等，可补齐索引）后再跑本用例。");
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

    // ─────────────────────────── SQL 语句数门禁 ───────────────────────────

    /// <summary>
    /// 跑完一个「含 N 个排他网关」实例的语句条数必须落在预算内，且<b>不随网关数量线性增长</b>。
    /// </summary>
    /// <remarks>
    /// 这条用例是变量 N+1 的回归防线。它盯的不是绝对值，而是「<b>增量</b>」：
    /// 把网关数量翻倍，语句数的增量应当是常数级（每个网关固定的几次往返），
    /// 而不是「每多一个网关就多一次变量往返」。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task SqlStatementBudget_ShouldNotGrowWithGatewayCount(string provider)
    {
        var counter = new SqlStatementCounter();
        var harness = await SetupAsync(provider, sqlCounter: counter);

        var small = await MeasureGatewayProcessAsync(harness, counter, gatewayCount: 2);
        var large = await MeasureGatewayProcessAsync(harness, counter, gatewayCount: 6);

        // 预算留了一定余量（不同驱动 / 版本的预编译语句探测行为略有差异），
        // 但足以拦住「从常量次往返退化成每网关 × 作用域深度次往返」这类回归。
        Assert.True(
            small <= SqlBudgetFor(gatewayCount: 2),
            $"2 个网关的语句数 {small} 超出预算 {SqlBudgetFor(gatewayCount: 2)}。");

        Assert.True(
            large <= SqlBudgetFor(gatewayCount: 6),
            $"6 个网关的语句数 {large} 超出预算 {SqlBudgetFor(gatewayCount: 6)}。");

        // 关键断言：增幅必须是线性小常数，不能随网关数 × 作用域深度膨胀
        var growth = large - small;

        Assert.True(
            growth <= 40,
            $"网关数从 2 增到 6，语句数却增加了 {growth} 条 —— 疑似变量作用域链上溯出现 N+1 回归。");
    }

    /// <summary>
    /// 预取本身必须是「一次往返」，不能每读一个变量就多一条 SQL。
    /// </summary>
    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PrefetchVariables_ShouldResolveFromMemoryAfterwards(string provider)
    {
        var counter = new SqlStatementCounter();
        var harness = await SetupAsync(provider, sqlCounter: counter);

        // 变量落在流程实例执行上，网关判定发生在 fork 出来的子执行上
        var (_, processInstanceId) = await harness.DeployAndStartAsync(
            GatewayChainProcess(gatewayCount: 3),
            new Dictionary<string, object?> { ["amount"] = 500, ["level"] = 3 },
            Ct);

        // 预取之后，连续读同一批变量必须全部内存命中，不再产生任何 SQL
        var execution = (await harness.Executions.FindByIdAsync(processInstanceId, Ct))!;

        await execution.PrefetchVariablesAsync(["amount", "level"], Ct)
            .ConfigureAwait(false);

        counter.Reset();

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(500, await execution.GetVariableAsync("amount", Ct));
            Assert.Equal(3, await execution.GetVariableAsync("level", Ct));
        }

        Assert.Equal(0, counter.Value);
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

    /// <summary>
    /// 建一个「网关跑在子执行上」的流程：
    /// <c>start → fork ┬→ gw_1 → … → gw_N → endA</c>
    /// <c>             └→ side（用户任务，永久等待，保证实例不结束）</c>
    /// </summary>
    /// <remarks>
    /// 关键是 fork：它会派生子执行，于是排他网关的判定发生在<b>子执行</b>上，
    /// 而流程变量落在<b>流程实例执行</b>上 —— 这正是变量作用域链必须上溯的场景。
    /// 每个网关配一真一假两个条件，确保两个条件都会被求值。
    /// </remarks>
    private static string GatewayChainProcess(int gatewayCount)
    {
        var xml = new StringBuilder();

        xml.AppendLine("""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                         xmlns:activiti="http://activiti.org/bpmn"
                         targetNamespace="http://netbpm/test">
              <process id="perf-gw-chain" isExecutable="true">
                <startEvent id="start" />
                <parallelGateway id="fork" />
                <sequenceFlow id="f_start" sourceRef="start" targetRef="fork" />
                <sequenceFlow id="f_branch" sourceRef="fork" targetRef="gw_1" />
                <sequenceFlow id="f_side" sourceRef="fork" targetRef="side" />
            """);

        for (var index = 1; index <= gatewayCount; index++)
        {
            var target = index == gatewayCount ? "endA" : $"gw_{index + 1}";

            xml.AppendLine(CultureInfo.InvariantCulture, $$"""
                    <exclusiveGateway id="gw_{{index}}" />
                    <sequenceFlow id="f_{{index}}_taken" sourceRef="gw_{{index}}" targetRef="{{target}}">
                      <conditionExpression xsi:type="tFormalExpression">${amount > 0}</conditionExpression>
                    </sequenceFlow>
                    <sequenceFlow id="f_{{index}}_halt" sourceRef="gw_{{index}}" targetRef="alt_{{index}}">
                      <conditionExpression xsi:type="tFormalExpression">${level > 99}</conditionExpression>
                    </sequenceFlow>
                    <userTask id="alt_{{index}}" activiti:assignee="nobody" />
                """);
        }

        xml.AppendLine("""
                <endEvent id="endA" />
                <userTask id="side" activiti:assignee="kermit" />
              </process>
            </definitions>
            """);

        return xml.ToString();
    }

    /// <summary>测一次「部署 + 启动」，返回<b>启动阶段</b>实际发出的语句条数。</summary>
    private static async Task<int> MeasureGatewayProcessAsync(
        DatabaseEngineHarness harness,
        SqlStatementCounter counter,
        int gatewayCount)
    {
        var model = harness.XmlConverter.ConvertToBpmnModel(
            new MemoryStream(Encoding.UTF8.GetBytes(GatewayChainProcess(gatewayCount))));
        var process = model.GetMainProcess()!;

        // 部署阶段不计入（它是一次性的、与网关判定无关的常量开销）
        var definition = await harness.Definitions.DeployAsync(model, "perf-gw.bpmn20.xml", ct: Ct);

        var variables = new Dictionary<string, object?>
        {
            ["amount"] = 500,
            ["level"] = 3,
        };

        counter.Reset();

        await harness.Executor.ExecuteAsync(
            new StartProcessInstanceCommand(process, definition.Id, "BIZ-PERF", "tester", variables),
            Ct);

        return counter.Value;
    }

    /// <summary>
    /// 语句数预算。刻意留宽（不同驱动 / 版本的语句探测次数有差异），
    /// 但在「每个网关 × 作用域深度」的 N+1 退化面前会明确失败。
    /// </summary>
    private static int SqlBudgetFor(int gatewayCount) => 40 + (gatewayCount * 12);

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
        IBpmnXmlConverter? xmlConverter = null,
        SqlStatementCounter? sqlCounter = null)
    {
        var spec = provider switch
        {
            "mysql" => TestDatabases.MySql,
            "sqlserver" => TestDatabases.SqlServer,
            _ => throw new InvalidOperationException($"未知方言 '{provider}'。"),
        };

        Assert.SkipUnless(spec is not null, $"方言 {provider} 不可用。");

        var harness = new DatabaseEngineHarness(spec!, xmlConverter: xmlConverter, sqlCounter: sqlCounter);
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
