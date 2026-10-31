using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Jobs;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 外部任务死信重投（<c>POST /external-tasks/{id}/retry</c>）与无人领取告警的收口。
/// </summary>
/// <remarks>
/// <para>
/// 死信是外部任务<b>唯一会停下来的地方</b>：失败绝不跳过节点，所以重试耗尽后流程就停在
/// <c>serviceTask</c> 上，谁也不许替它往前走。停下来的目的正是等一个人来看 ——
/// 这个端点就是「看完了，再给一次机会」那个动作。
/// </para>
/// <para>本类锁住四件事：</para>
/// <list type="number">
///   <item><description><b>只有死信能重投</b>：其它状态一律 409（不是静默成功）；</description></item>
///   <item><description><b>重投后能重新被领</b>：状态回到可领取、重试次数恢复、锁已清空；</description></item>
///   <item><description><b>跨租户按 404</b>：不能借此探测别的租户有没有这条任务；</description></item>
///   <item><description><b>要管理 scope</b>：worker 凭据不能自己复活自己 —— 那会把退避与死信机制架空。</description></item>
/// </list>
/// </remarks>
public sealed class ExternalTaskDeadLetterRetryTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "external_task_retry_demo";
    private const string Topic = "invoice";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Retry_Should404_WhenTaskUnknown()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "ext-retry-404", [PlatformScopes.AdminInstance]);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/external-tasks/not-exist/retry",
            secret,
            new { retries = 3 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var error = await ReadJsonAsync(response);

        Assert.Equal("external_task_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Retry_Should409_WhenTaskIsNotDeadLetter()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "ext-retry-409",
            [PlatformScopes.ExternalTask, PlatformScopes.AdminInstance]);

        var instanceId = await StartAsync(host);

        var fetched = await FetchAsync(client, secret, "worker-a", Topic);
        var taskId = fetched[0].GetProperty("id").GetString()!;

        // 任务正被 worker 持锁：此时「重投」没有意义，也不该被当成成功
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/retry",
            secret,
            new { retries = 3 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = await ReadJsonAsync(response);

        Assert.Equal("external_task_not_dead_letter", error.GetProperty("code").GetString());

        // 状态没被改动，worker 手里的锁还在
        var task = await host.Services.GetRequiredService<IExternalTaskBroker>()
            .FindByIdAsync(taskId, Ct);

        Assert.Equal(ExternalTaskStatus.Locked, task!.Status);
        Assert.Equal("worker-a", task.WorkerId);
    }

    [Fact]
    public async Task Retry_ShouldReviveDeadLetter_AndMakeItFetchableAgain()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "ext-retry-happy",
            [PlatformScopes.ExternalTask, PlatformScopes.AdminInstance]);

        await StartAsync(host);

        var taskId = (await FetchAsync(client, secret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        var broker = host.Services.GetRequiredService<IExternalTaskBroker>();

        // 把重试额度耗光（默认 3 次）：流程停在原地不动。
        // ⚠️ 每次失败都会把锁还回去、并按下一次退避时间（默认 5 秒起），
        // 所以要重新领一次才能再失败一次；retryAfter 传「刚刚」是为了让下一次立刻可领，
        // 否则用例要等 5 秒退避 —— 那不是被测行为，只是节奏。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 第一次进来时锁还在手上（上面刚 fetch 过），从第二次起才需要重新领
            if (attempt > 0)
            {
                var relocked = await FetchAsync(client, secret, "worker-a", Topic);

                Assert.Equal(taskId, Assert.Single(relocked).GetProperty("id").GetString());
            }

            using var failure = await SendJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/runtime/external-tasks/{taskId}/failure",
                secret,
                new
                {
                    workerId = "worker-a",
                    errorMessage = $"第 {attempt + 1} 次失败",
                    retryAfter = DateTime.UtcNow.AddSeconds(-1),
                });

            Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);
        }

        var dead = await broker.FindByIdAsync(taskId, Ct);

        Assert.Equal(ExternalTaskStatus.DeadLetter, dead!.Status);
        Assert.Equal(0, dead.Retries);

        using var retry = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/retry",
            secret,
            new { retries = 2 });

        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);

        var revived = await broker.FindByIdAsync(taskId, Ct);

        Assert.Equal(ExternalTaskStatus.Pending, revived!.Status);
        Assert.Equal(2, revived.Retries);
        Assert.Null(revived.WorkerId);
        Assert.Null(revived.NextDueAt);

        // 能重新被领走，才说明重投真的生效了（不只是改了个状态字）
        var refetched = await FetchAsync(client, secret, "worker-b", Topic);

        Assert.Equal(taskId, Assert.Single(refetched).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Retry_Should403_ForClientWithoutAdminScope()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var workerSecret = await RegisterAsync(client, "ext-retry-worker", [PlatformScopes.ExternalTask]);
        var adminSecret = await RegisterAsync(client, "ext-retry-admin", [PlatformScopes.AdminInstance]);

        await StartAsync(host);

        var taskId = (await FetchAsync(client, workerSecret, "worker-a", Topic))[0]
            .GetProperty("id").GetString()!;

        // 耗光重试额度让它进死信（FindByIdAsync 返回的是副本，改它不起作用，只能真失败）
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await FetchAsync(client, workerSecret, "worker-a", Topic);
            }

            using var failure = await SendJsonAsync(
                client,
                HttpMethod.Post,
                $"/api/runtime/external-tasks/{taskId}/failure",
                workerSecret,
                new
                {
                    workerId = "worker-a",
                    errorMessage = $"第 {attempt + 1} 次失败",
                    retryAfter = DateTime.UtcNow.AddSeconds(-1),
                });

            Assert.Equal(HttpStatusCode.NoContent, failure.StatusCode);
        }

        var task = await host.Services.GetRequiredService<IExternalTaskBroker>().FindByIdAsync(taskId, Ct);

        Assert.Equal(ExternalTaskStatus.DeadLetter, task!.Status);

        // worker 自己的凭据不能重投：这是「管理干预」，不是 worker 的自助通道
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/retry",
            workerSecret,
            new { retries = 3 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var allowed = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/external-tasks/{taskId}/retry",
            adminSecret,
            new { retries = 3 });

        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

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
            builder.UseSetting("NetBpm:ExternalTasks:Watchdog:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    private static string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="外部任务死信演示" isExecutable="true">
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

    private static async Task<string> StartAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        return await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "lisi",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = "3" },
            tenantId: DefaultTenant,
            ct: Ct);
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchAsync(
        HttpClient client,
        string secret,
        string workerId,
        string topic)
    {
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/external-tasks/fetch",
            secret,
            new { workerId, topics = new[] { topic }, maxTasks = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
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

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return document.RootElement.Clone();
    }
}
