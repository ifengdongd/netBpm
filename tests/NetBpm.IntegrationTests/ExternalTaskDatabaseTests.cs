using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 外部任务对着<b>真库</b>的验证（<c>nb_external_task</c> / <c>DapperExternalTaskBroker</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 内存实现跑通不代表 SQL 跑得通。这里要盯的恰好是内存版天然不会出问题的两处：
/// </para>
/// <list type="number">
///   <item><description><b>抢占</b>：并发领取靠 <c>update ... where 可领取条件</c> 的影响行数仲裁，
///     内存版用 <c>lock</c> 天然串行，SQL 写错条件就会出现「两个 worker 领到同一条」；</description></item>
///   <item><description><b>payload 的类型保真</b>：变量快照过一遍 JSON，
///     写进去是 <c>long</c>、读出来变成 <c>string</c> 的话，下游表达式会静默算错。</description></item>
/// </list>
/// <para>
/// 另外顺带验证一件事：领取写在了命令事务里（锁与流程推进同进同退）。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class ExternalTaskDatabaseTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "external_task_db";

    private readonly RestEngineFactory _factory;

    /// <summary>
    /// 🔑 <b>每条用例一个独立 topic</b>。
    /// <para>
    /// 测试库是长期复用的（不是每次新建），而 <c>nb_external_task</c> 不在
    /// <c>EngineTableCleaner.Purge</c> 的清理范围内 —— 上一轮留下的行会一直躺着。
    /// 锁过期的 <c>LOCKED</c> 行按设计要能被接管（worker 崩了不能把活锁死），
    /// 于是它们迟早重新变成「可领取」；等到下一轮 fetch，本用例就会一次领到好几条，
    /// <c>Assert.Single</c> 红 —— 而且红不红取决于距上次跑了多久，是典型的偶发失败。
    /// 用随机 topic 把每条用例与历史残留、以及彼此之间彻底隔开。
    /// </para>
    /// </summary>
    private readonly string _topic = "invoice-" + Guid.NewGuid().ToString("N")[..8];

    public ExternalTaskDatabaseTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private string Topic => _topic;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>登记 → 领取 → 成功回执 → 流程推进，全走 Dapper SQL。</summary>
    [Fact]
    public async Task Complete_ShouldAdvanceProcess_ThroughSqlBroker()
    {
        SkipUnlessReady();

        await DeployAsync();
        var instanceId = await StartAsync();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        var fetched = await engine.FetchExternalTasksAsync(
            "worker-a", [Topic], 10, null, DefaultTenant, Ct);

        var task = Assert.Single(fetched);

        Assert.Equal(Topic, task.Topic);
        Assert.Equal(instanceId, task.ProcessInstanceId);
        Assert.Equal(ExternalTaskStatus.Locked, task.Status);
        Assert.Equal("worker-a", task.WorkerId);
        Assert.NotNull(task.LockExpiresAt);

        // payload 的类型必须保真：写进去的是 long 3，读出来不能变成 "3"
        Assert.Equal(3L, task.Payload["days"]);

        await engine.CompleteExternalTaskAsync(
            task.Id,
            "worker-a",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["invoiceNo"] = "INV-1" },
            DefaultTenant,
            null,
            Ct);

        // 推进：节点之后的人工环节出现
        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        Assert.Single(tasks);

        var variables = await engine.GetVariablesAsync(instanceId, Ct);
        Assert.Equal("INV-1", variables["invoiceNo"]?.ToString());
    }

    /// <summary>并发领取：同一条任务只能被一个 worker 领走。</summary>
    [Fact]
    public async Task Fetch_ShouldNotHandSameTaskToTwoWorkers()
    {
        SkipUnlessReady();

        await DeployAsync();
        await StartAsync();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        var broker = _factory.Services.GetRequiredService<IExternalTaskBroker>();

        var first = await engine.FetchExternalTasksAsync(
            "worker-a", [Topic], 10, TimeSpan.FromMinutes(5), DefaultTenant, Ct);

        Assert.Single(first);

        // 锁未过期：第二个 worker 领不到（SQL 的可领取条件必须把 LOCKED 且未过期的排除掉）
        var second = await engine.FetchExternalTasksAsync("worker-b", [Topic], 10, null, DefaultTenant, Ct);
        Assert.Empty(second);

        // 先释放、再用极短锁重新领取 —— 等价于「worker 领到后崩溃，锁到期未续租」
        Assert.True(await broker.UnlockAsync(first[0].Id, "worker-a", Ct));

        var relocked = await engine.FetchExternalTasksAsync(
            "worker-a", [Topic], 10, TimeSpan.FromMilliseconds(1), DefaultTenant, Ct);

        Assert.Single(relocked);

        await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);

        // 锁过期后可以被接管：worker 崩了，活不能被永久锁死
        var taken = Assert.Single(
            await engine.FetchExternalTasksAsync("worker-b", [Topic], 10, null, DefaultTenant, Ct));

        Assert.Equal(first[0].Id, taken.Id);
        Assert.Equal("worker-b", taken.WorkerId);
    }

    /// <summary>失败回执：退避 + 重试计数，耗尽进死信，流程原地不动。</summary>
    [Fact]
    public async Task Failure_ShouldBackoff_ThenDeadLetter_WithoutAdvancing()
    {
        SkipUnlessReady();

        await DeployAsync();
        var instanceId = await StartAsync();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        var fetched = await engine.FetchExternalTasksAsync("worker-a", [Topic], 10, null, DefaultTenant, Ct);
        var taskId = Assert.Single(fetched).Id;

        await engine.FailExternalTaskAsync(
            taskId,
            "worker-a",
            new ExternalTaskFailure("对端超时", null, 2, DateTime.UtcNow.AddSeconds(-1)),
            DefaultTenant,
            null,
            Ct);

        // 流程仍在等待
        Assert.Empty(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        var requeued = await engine.FetchExternalTasksAsync("worker-b", [Topic], 10, null, DefaultTenant, Ct);
        var again = Assert.Single(requeued);

        Assert.Equal(taskId, again.Id);
        Assert.Equal(2, again.Retries);
        Assert.Equal("对端超时", again.ErrorMessage);

        await engine.FailExternalTaskAsync(
            taskId,
            "worker-b",
            new ExternalTaskFailure("对端不可用", null, 0),
            DefaultTenant,
            null,
            Ct);

        // 死信：领不到，但任务还在（不能消失 —— 消失等于这一步从没发生过）
        Assert.Empty(await engine.FetchExternalTasksAsync("worker-a", [Topic], 10, null, DefaultTenant, Ct));

        var dead = await engine.FindExternalTasksAsync(
            new ExternalTaskQuery(Topic, null, ExternalTaskStatus.DeadLetter, null, false, 10, DefaultTenant), Ct);

        Assert.Contains(dead, row => string.Equals(row.Id, taskId, StringComparison.Ordinal));
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        try
        {
            using var session = MySqlTestDatabase.CreateSessionFactory().Create();
            var count = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables "
                + "where table_schema = database() and table_name = 'nb_external_task'");

            Assert.SkipUnless(
                count >= 1,
                "本库缺 nb_external_task。请把 NETBPM_MYSQL_CONNECTION 指向 netbpm_ittest，"
                + "或对目标库执行 DbMigrate migrate 补齐平台表。");
        }
        catch
        {
            Assert.Skip("无法探测平台表，跳过。");
        }
    }

    private async Task DeployAsync()
    {
        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml()));

        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", tenantId: DefaultTenant, ct: Ct);
    }

    private async Task<string> StartAsync()
    {
        var engine = _factory.Services.GetRequiredService<IProcessEngine>();

        return await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "lisi",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = 3L },
            tenantId: DefaultTenant,
            ct: Ct);
    }

    private string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="外部任务数据库演示" isExecutable="true">
            <bpmn:startEvent id="start" name="Start" />
            <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="svc_invoice" />
            <bpmn:serviceTask id="svc_invoice" name="开票" activiti:topic="{{_topic}}" />
            <bpmn:sequenceFlow id="flow2" sourceRef="svc_invoice" targetRef="task_review" />
            <bpmn:userTask id="task_review" name="复核" activiti:assignee="lisi" />
            <bpmn:sequenceFlow id="flow3" sourceRef="task_review" targetRef="end" />
            <bpmn:endEvent id="end" name="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;
}
