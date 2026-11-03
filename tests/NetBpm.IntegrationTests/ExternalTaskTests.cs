using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 外部任务（<c>activiti:topic</c>）的端到端闭环 —— <b>S4 独立部署的门槛</b>。
/// </summary>
/// <remarks>
/// <para>
/// 没有它，独立部署的引擎只能跑纯人工流程：<c>serviceTask</c> 的实现类在客户系统里，
/// 引擎进程内无从解析。这类流程走「引擎登记 → worker 拉走 → 回执 → 引擎推进」。
/// </para>
/// <para>本类锁住五件事：</para>
/// <list type="number">
///   <item><description><b>登记与领取</b>：执行到 topic 节点会留下一条外部任务，流程原地等待；</description></item>
///   <item><description><b>回执推进</b>：complete 后变量写回、流程走到下一个节点；</description></item>
///   <item><description><b>锁即归属</b>：不是持锁者的回执一律拒绝（409），任务不会被执行两次；</description></item>
///   <item><description><b>失败不跳过</b>：failure 只扣重试、流程原地不动；耗尽进死信，
///     而不是「看起来办完了」；</description></item>
///   <item><description><b>锁过期可接管</b>：worker 崩溃后任务能被别的 worker 领走，
///     原持锁者的迟到回执被拒。</description></item>
/// </list>
/// </remarks>
public sealed class ExternalTaskTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "external_task_demo";
    private const string Topic = "invoice";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_ShouldRegisterExternalTask_AndWaitAtNode()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-wait", [PlatformScopes.ExternalTask]);
        var instanceId = await StartAsync(host);

        var fetched = await FetchAsync(client, secret, "worker-a", Topic);

        var task = Assert.Single(fetched);
        Assert.Equal(Topic, task.GetProperty("topic").GetString());
        Assert.Equal(instanceId, task.GetProperty("processInstanceId").GetString());
        Assert.Equal("LOCKED", task.GetProperty("status").GetString());
        Assert.Equal("worker-a", task.GetProperty("workerId").GetString());

        // 快照：起实例时传的变量要能被 worker 看到，否则它无从下手
        var payload = task.GetProperty("payload");
        Assert.Equal("3", payload.GetProperty("days").GetString());

        // 流程必须停在节点上：task 节点之后还有一个人工环节，此时不该出现
        Assert.Empty(await FindUserTasksAsync(host, instanceId));
    }

    [Fact]
    public async Task Complete_ShouldAdvanceProcess_AndWriteBackVariables()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-complete", [PlatformScopes.ExternalTask]);
        var instanceId = await StartAsync(host);

        var taskId = (await FetchAsync(client, secret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/complete",
            secret,
            "worker-a",
            new { workerId = "worker-a", variables = new Dictionary<string, string> { ["invoiceNo"] = "INV-1" } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // 推进：节点之后的人工环节出现了
        var userTasks = await FindUserTasksAsync(host, instanceId);
        Assert.Single(userTasks);

        // 变量写回：worker 的回执值要落到流程实例作用域
        var variables = await host.Services.GetRequiredService<IProcessEngine>()
            .GetVariablesAsync(instanceId, Ct);

        Assert.Equal("INV-1", variables["invoiceNo"]?.ToString());
    }

    [Fact]
    public async Task Complete_Should409_WhenCallerIsNotLockHolder()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-wrong-worker", [PlatformScopes.ExternalTask]);
        await StartAsync(host);

        var taskId = (await FetchAsync(client, secret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/complete",
            secret,
            "worker-b",
            new { workerId = "worker-b" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Failure_ShouldKeepProcessWaiting_AndAllowRequeue()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-failure", [PlatformScopes.ExternalTask]);
        var instanceId = await StartAsync(host);

        var taskId = (await FetchAsync(client, secret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        using var failure = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/failure",
            secret,
            "worker-a",
            new
            {
                workerId = "worker-a",
                errorMessage = "对端超时",
                retries = 2,
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(-1),
            });

        Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);

        // 失败不推进流程：节点之后的人工环节仍然不存在
        Assert.Empty(await FindUserTasksAsync(host, instanceId));

        // 退避期已过 → 可以再领（领不到就等于这一步永远没人做，流程静默卡死）
        var refetched = await FetchAsync(client, secret, "worker-b", Topic);
        var again = Assert.Single(refetched);

        Assert.Equal(taskId, again.GetProperty("id").GetString());
        Assert.Equal(2, again.GetProperty("retries").GetInt32());
        Assert.Equal("worker-b", again.GetProperty("workerId").GetString());
    }

    [Fact]
    public async Task Failure_ShouldMoveToDeadLetter_WhenRetriesExhausted()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-deadletter", [PlatformScopes.ExternalTask]);
        var instanceId = await StartAsync(host);

        var taskId = (await FetchAsync(client, secret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        using var failure = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/failure",
            secret,
            "worker-a",
            new { workerId = "worker-a", errorMessage = "对端不可用", retries = 0 });

        Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);

        // 死信：不再被领取，但也不能消失 —— 消失等于「这一步从没发生过」
        Assert.Empty(await FetchAsync(client, secret, "worker-a", Topic));

        var listed = await ListAsync(client, secret, "DEAD_LETTER");
        var dead = Assert.Single(listed);

        Assert.Equal(taskId, dead.GetProperty("id").GetString());
        Assert.Equal("对端不可用", dead.GetProperty("errorMessage").GetString());

        // 流程仍停在原地：死信不是「跳过」
        Assert.Empty(await FindUserTasksAsync(host, instanceId));
    }

    [Fact]
    public async Task LockExpired_ShouldAllowAnotherWorker_AndRejectLateReceipt()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-lock-expired", [PlatformScopes.ExternalTask]);
        await StartAsync(host);

        // 锁只给 1 毫秒：worker-a 领走后立刻过期，等价于「worker 崩溃后不再续租」
        var first = await FetchAsync(client, secret, "worker-a", Topic, lockDurationMs: 1);
        var taskId = first[0].GetProperty("id").GetString()!;

        await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);

        var second = await FetchAsync(client, secret, "worker-b", Topic);
        Assert.Equal(taskId, Assert.Single(second).GetProperty("id").GetString());

        // 迟到的回执必须被拒：否则一次调用产生两次推进
        using var late = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/complete",
            secret,
            "worker-a",
            new { workerId = "worker-a" });

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Fetch_Should403_WhenClientLacksExternalTaskScope()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-no-scope", [PlatformScopes.InstanceRead]);
        await StartAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/external-tasks/fetch",
            secret,
            "worker-a",
            new { workerId = "worker-a", topics = new[] { Topic } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// 业务键必须跟着外部任务走 —— 它是 worker 把回执对回业务单据的<b>唯一</b>可靠关联键。
    /// </summary>
    /// <remarks>
    /// 回归点：<c>BUSINESS_KEY_</c> 只写在<b>根执行</b>上，而服务任务是在<b>子执行</b>上执行的
    /// （执行到达节点时已下钻一层）。早先直接读当前执行，于是
    /// <c>nb_external_task.business_key</c> 恒为 <c>null</c>，worker 只能退化用实例 id 兜底。
    /// </remarks>
    [Fact]
    public async Task Fetch_ShouldCarryBusinessKeyFromRootExecution()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-business-key", [PlatformScopes.ExternalTask]);
        const string BusinessKey = "LEAVE-2026-1103-001";

        await StartAsync(host, BusinessKey);

        var task = Assert.Single(await FetchAsync(client, secret, "worker-a", Topic));

        Assert.Equal(BusinessKey, task.GetProperty("businessKey").GetString());
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static WebApplicationFactory<Program> BuildHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    /// <summary>
    /// start → serviceTask(topic) → userTask → end：
    /// 外部任务后面必须留一个人工环节，否则「流程是否推进」只能靠实例是否存在来判断，
    /// 而内存宿主下结束的实例查不到，断言会变成「什么都没验证」。
    /// </summary>
    private static string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="外部任务演示" isExecutable="true">
            <bpmn:startEvent id="start" name="Start" />
            <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="svc_invoice" />
            <bpmn:serviceTask id="svc_invoice" name="开票" activiti:topic="{{Topic}}" />
            <bpmn:sequenceFlow id="flow2" sourceRef="svc_invoice" targetRef="task_review" />
            <bpmn:userTask id="task_review" name="复核" activiti:assignee="lisi" />
            <bpmn:sequenceFlow id="flow3" sourceRef="task_review" targetRef="end" />
            <bpmn:endEvent id="end" name="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml()));

        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", tenantId: DefaultTenant, ct: Ct);
    }

    private static async Task<string> StartAsync(WebApplicationFactory<Program> host) =>
        await StartAsync(host, businessKey: null);

    private static async Task<string> StartAsync(WebApplicationFactory<Program> host, string? businessKey)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        return await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            businessKey: businessKey,
            startUserId: "lisi",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = "3" },
            tenantId: DefaultTenant,
            ct: Ct);
    }

    private static async Task<IReadOnlyList<NetBpm.Engine.Tasks.TaskEntity>> FindUserTasksAsync(
        WebApplicationFactory<Program> host,
        string instanceId)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        return await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchAsync(
        HttpClient client,
        string secret,
        string workerId,
        string topic,
        int? lockDurationMs = null)
    {
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/external-tasks/fetch",
            secret,
            workerId,
            new { workerId, topics = new[] { topic }, maxTasks = 10, lockDurationMs });

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"领取外部任务失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(
        HttpClient client,
        string secret,
        string? status)
    {
        using var response = await SendGetAsync(
            client,
            $"/api/runtime/external-tasks?status={status}",
            secret,
            "worker-a");

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"查询外部任务失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name, tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    // worker 不是人：外部任务的调用不需要 X-Subject-Id（带了反而会被当成代客调用，
    // 要求 workflow:task:submit:onbehalf —— 那是「替某个审批人办理」的语义，与外部任务无关）
    private static Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string path,
        string secret,
        string workerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        string workerId,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Content = JsonContent.Create(body ?? new { });

        return client.SendAsync(request, Ct);
    }
}
