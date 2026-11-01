using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Tasks;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Rest.Security;
using NetBpm.Runtime;
using System.Text;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 环节聚合（<c>nodeOutcomes</c>）的端到端验收：会签 / 或签 / 多数通过三种口径，
/// 对着<b>真库 + 引擎历史</b>跑。
/// </summary>
/// <remarks>
/// <para>
/// 为什么必须真库：聚合要数「这个环节应该来几个人」，而那件事只有
/// <c>ACT_HI_ACTINST</c>（每个多实例一行）知道。内存宿主在 <c>EnableHistory=false</c> 下没有历史行，
/// 或签提前结束时剩余任务已被引擎删除 —— 总人数会退化成「已投的票数」，
/// 于是「3 人里 1 人批了就通过」看起来像全票通过。这正是要钉住的地方。
/// </para>
/// <para>
/// 每个用例用<b>各自的流程 key</b>：平台表（<c>nb_*</c>）不被 <c>EngineTableCleaner</c> 清理，
/// 复用同一个 key 会让上一轮的定义物化残留混进来。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class NodeOutcomeAggregationTests : IClassFixture<RestEngineFactory>
{
    private const string Approver = "lisi";

    private readonly RestEngineFactory _factory;

    public NodeOutcomeAggregationTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Countersign_ShouldCountPending_AndOnlySatisfyWhenEveryoneApproved()
    {
        SkipUnlessReady();

        var key = NewKey("countersign");
        await DeployAsync(key, completionCondition: null);

        var (instanceId, tasks) = await StartAsync(key);
        Assert.Equal(3, tasks.Count);

        // 第 1 票：3 人里还有 2 人没批 —— 环节不能算通过
        await SubmitAsync(tasks[0].Id, "1");

        var halfway = await GetOutcomeAsync(instanceId);

        Assert.Equal("countersign", halfway.ActivityId);
        Assert.Equal(NodeOutcomeAggregation.AllApproved, halfway.Aggregation);
        Assert.False(halfway.Satisfied);
        Assert.Equal(3, halfway.Total);
        Assert.Equal(1, halfway.Positive);
        Assert.Equal(2, halfway.Pending);
        Assert.Null(halfway.DecidedAt);

        // 第 2 / 3 票：全部同意后环节才达成
        foreach (var task in await FindTasksAsync(instanceId))
        {
            await SubmitAsync(task.Id, "1");
        }

        var done = await GetOutcomeAsync(instanceId);

        Assert.Equal(NodeOutcomeAggregation.AllApproved, done.Aggregation);
        Assert.True(done.Satisfied);
        Assert.Equal(3, done.Total);
        Assert.Equal(3, done.Positive);
        Assert.Equal(0, done.Pending);
        Assert.NotNull(done.DecidedAt);
    }

    [Fact]
    public async Task AnyApproved_ShouldSatisfyOnFirstVote_ButStillReportThreePeople()
    {
        SkipUnlessReady();

        var key = NewKey("anyapproved");
        await DeployAsync(key, completionCondition: "${nrOfCompletedInstances == 1}");

        var (instanceId, tasks) = await StartAsync(key);
        Assert.Equal(3, tasks.Count);

        await SubmitAsync(tasks[0].Id, "1");

        var outcome = await GetOutcomeAsync(instanceId);

        // 或签：1 票即通过，但「应到 3 人」不能因此缩成 1 —— 那会把「另外 2 人没发表意见」抹掉
        Assert.Equal(NodeOutcomeAggregation.AnyApproved, outcome.Aggregation);
        Assert.True(outcome.Satisfied);
        Assert.Equal(3, outcome.Total);
        Assert.Equal(1, outcome.Positive);
        Assert.Equal(2, outcome.Pending);
        Assert.NotNull(outcome.DecidedAt);
    }

    [Fact]
    public async Task Countersign_ShouldNotSatisfy_WhenSomeoneRejected()
    {
        SkipUnlessReady();

        var key = NewKey("countersign-reject");
        await DeployAsync(key, completionCondition: null);

        var (instanceId, tasks) = await StartAsync(key);

        await SubmitAsync(tasks[0].Id, "2");

        var outcome = await GetOutcomeAsync(instanceId);

        // 会签里一票否决：即便另外两人后来同意，这个环节也不是「通过着离开」的
        Assert.Equal(NodeOutcomeAggregation.AllApproved, outcome.Aggregation);
        Assert.False(outcome.Satisfied);
        Assert.Equal(1, outcome.Negative);
        Assert.Equal(2, outcome.Pending);
        Assert.Null(outcome.DecidedAt);
    }

    [Fact]
    public async Task Majority_ShouldRequireMoreThanHalf()
    {
        SkipUnlessReady();

        var key = NewKey("majority");
        await DeployAsync(key, completionCondition: "${nrOfCompletedInstances/nrOfInstances >= 0.6}");

        var (instanceId, tasks) = await StartAsync(key);

        // 3 人的 60% = 1.8 → 需要 2 票；1 票还不够
        await SubmitAsync(tasks[0].Id, "1");

        var first = await GetOutcomeAsync(instanceId);

        Assert.Equal(NodeOutcomeAggregation.Majority, first.Aggregation);
        Assert.False(first.Satisfied);
        Assert.Equal(1, first.Positive);

        // 第 2 票达成多数 → 节点离开，剩余实例被取消，但「应到 3 人」仍然如实报告
        await SubmitAsync((await FindTasksAsync(instanceId))[0].Id, "1");

        var second = await GetOutcomeAsync(instanceId);

        Assert.Equal(NodeOutcomeAggregation.Majority, second.Aggregation);
        Assert.True(second.Satisfied);
        Assert.Equal(3, second.Total);
        Assert.Equal(2, second.Positive);
        Assert.Equal(1, second.Pending);
    }

    [Fact]
    public async Task SingleTask_ShouldStaySingle()
    {
        SkipUnlessReady();

        var key = NewKey("single");
        await DeployAsync(key, completionCondition: null, cardinality: null);

        var (instanceId, tasks) = await StartAsync(key);
        var task = Assert.Single(tasks);

        await SubmitAsync(task.Id, "1");

        var outcome = await GetOutcomeAsync(instanceId);

        // 单签不因为「有一条完成记录」被误标成会签
        Assert.Equal(NodeOutcomeAggregation.Single, outcome.Aggregation);
        Assert.True(outcome.Satisfied);
        Assert.Equal(1, outcome.Total);
        Assert.Equal(1, outcome.Positive);
    }

    [Fact]
    public async Task InstanceView_ShouldAgreeWithHistory()
    {
        SkipUnlessReady();

        var key = NewKey("parity");
        await DeployAsync(key, completionCondition: null);

        var (instanceId, tasks) = await StartAsync(key);
        await SubmitAsync(tasks[0].Id, "1");

        await using var scope = _factory.Services.CreateAsyncScope();

        var catalog = scope.ServiceProvider.GetRequiredService<IInstanceCatalog>();
        var view = await catalog.FindAsync(instanceId, Caller(Approver), Ct);

        Assert.NotNull(view);

        // 实例详情与审批记录页必须给同一个数字：两处各算一遍是「同一单两种人数」的来源
        var outcome = Assert.Single(view.NodeOutcomes);

        Assert.Equal("countersign", outcome.ActivityId);
        Assert.Equal(3, outcome.Total);
        Assert.Equal(1, outcome.Positive);
        Assert.False(outcome.Satisfied);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static string NewKey(string scenario) =>
        $"nodeoutcome_{scenario}_{Guid.NewGuid().ToString("N")[..8]}";

    private static RequestContext Caller(string subject) => new()
    {
        SubjectId = subject,
        AuthMode = RequestAuthMode.None,
        Scopes = new HashSet<string>(StringComparer.Ordinal) { TaskScopes.Admin },
        RequestId = Guid.NewGuid().ToString("N")[..8],
    };

    /// <summary>
    /// 部署「start → 会签 → end」。<paramref name="cardinality"/> 为 <see langword="null"/>
    /// 时不写多实例配置（单签对照用例）。
    /// </summary>
    private async Task DeployAsync(string key, string? completionCondition, int? cardinality = 3)
    {
        // 多实例块用拼接而不是嵌套 raw string：里面的 ${...} 与 C# 插值语法极易混淆，
        // 拼错的表现是「BPMN 解析成功但会签配置丢了」，排查成本远高于这几行拼接。
        var loop = cardinality is null
            ? string.Empty
            : "      <bpmn:multiInstanceLoopCharacteristics isSequential=\"false\">\n"
              + $"        <bpmn:loopCardinality>{cardinality}</bpmn:loopCardinality>\n"
              + (completionCondition is null
                  ? string.Empty
                  : $"        <bpmn:completionCondition>{completionCondition}</bpmn:completionCondition>\n")
              + "      </bpmn:multiInstanceLoopCharacteristics>\n";

        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{key}}" name="环节聚合演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="countersign" />
                <bpmn:userTask id="countersign" name="会签" activiti:assignee="${approver}">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
            {{loop}}      <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="countersign" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        await engine.DeployAsync(stream, key + ".bpmn20.xml", ct: Ct);
    }

    private async Task<(string InstanceId, IReadOnlyList<TaskEntity> Tasks)> StartAsync(string key)
    {
        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            key,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["approver"] = Approver,
                ["applicant"] = "zhangsan",
            },
            ct: Ct);

        return (instanceId, await FindTasksAsync(instanceId));
    }

    private async Task<IReadOnlyList<TaskEntity>> FindTasksAsync(string instanceId)
    {
        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        return await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
    }

    /// <summary>
    /// 平台读 / 写服务都是 <c>Scoped</c>（每请求一份 db session），不能从根容器直接解析 ——
    /// 每个动作开一个 scope，等价于走一次 HTTP 请求。
    /// </summary>
    private async Task SubmitAsync(string taskId, string optionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var submit = scope.ServiceProvider.GetRequiredService<ITaskSubmitService>();

        await submit.SubmitAsync(
            taskId,
            new SubmitTaskRequest(OptionId: optionId, Comment: "测试"),
            Caller(Approver),
            Ct);
    }

    private async Task<NodeOutcome> GetOutcomeAsync(string instanceId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var timeline = scope.ServiceProvider.GetRequiredService<IApprovalTimeline>();
        var history = await timeline.GetHistoryAsync(instanceId, 0, 100, Caller(Approver), Ct);

        Assert.NotNull(history);

        return Assert.Single(history.NodeOutcomes);
    }

    private void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();

        var count = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.tables "
            + "where table_schema = database() and table_name in ('ACT_HI_ACTINST', 'nb_instance_timeline')");

        Assert.SkipUnless(count == 2, "测试库缺少 ACT_HI_ACTINST / nb_instance_timeline，跳过聚合验收。");
    }
}
