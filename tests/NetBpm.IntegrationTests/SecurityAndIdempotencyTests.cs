using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P2 收官验收：API Key 鉴权 / 身份与租户透传 / 幂等键 / 分页信封。
/// <para>与其余 REST 用例一样，走真实宿主 + 真实 MySQL；数据库不可用时动态跳过。</para>
/// </summary>
[Collection("MySql")]
public sealed class SecurityAndIdempotencyTests : IClassFixture<RestEngineFactory>
{
    private const string ApiKey = "netbpm-test-key";

    private readonly RestEngineFactory _factory;

    public SecurityAndIdempotencyTests(RestEngineFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApiKey_WhenConfigured_ShouldRejectMissingAndAcceptValid()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var secured = _factory.WithWebHostBuilder(
            builder => builder.UseSetting("NetBpm:Security:ApiKey", ApiKey));

        using var client = secured.CreateClient();

        using (var anonymous = await client.GetAsync(
            new Uri("/api/designer/definitions", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        using (var authorized = await client.GetAsync(
            new Uri("/api/designer/definitions", UriKind.Relative), Ct))
        {
            Assert.True(authorized.IsSuccessStatusCode, $"带 key 仍失败：{authorized.StatusCode}");
        }
    }

    [Fact]
    public async Task IdentityAndTenantHeaders_ShouldFlowIntoQueries()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = _factory.WithWebHostBuilder(
            builder => builder.UseSetting("NetBpm:Security:ApiKey", ApiKey));

        using var client = secured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        // 部署：不传 ?tenantId，由 X-Tenant-Id 决定
        using (var deployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            deployRequest.Headers.Add("X-Tenant-Id", "tenant-a");
            deployRequest.Content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml");

            using var deploy = await client.SendAsync(deployRequest, Ct);

            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode}");
        }

        // 定义列表按租户隔离（未声明租户时不限制，声明 tenant-b 时为空）
        Assert.Single(await JsonArrayAsync(client, "/api/designer/definitions", "tenant-a"));
        Assert.Empty(await JsonArrayAsync(client, "/api/designer/definitions", "tenant-b"));

        // 启动：不传 startUserId，由 X-User-Id 决定
        string instanceId;

        using (var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative)))
        {
            request.Headers.Add("X-Tenant-Id", "tenant-a");
            request.Headers.Add("X-User-Id", "alice");
            request.Content = JsonContent.Create(new
            {
                processDefinitionKey = "security-single",
                businessKey = "BIZ-SEC-1",
            });

            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(response.IsSuccessStatusCode, $"启动失败：{response.StatusCode} {body}");
            instanceId = JsonDocument.Parse(body).RootElement.GetProperty("processInstanceId").GetString()!;
        }

        var instance = await GetJsonAsync(client, $"/api/runtime/process-instances/{instanceId}", "tenant-a");
        Assert.Equal("alice", instance.GetProperty("startUserId").GetString());
        Assert.Equal("tenant-a", instance.GetProperty("tenantId").GetString());

        // 认领：请求体不传 userId，由 X-User-Id 兜底
        using (var candidateDeployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            candidateDeployRequest.Headers.Add("X-Tenant-Id", "tenant-a");
            candidateDeployRequest.Content = new StringContent(CandidateProcess(), Encoding.UTF8, "text/xml");

            using var deploy = await client.SendAsync(candidateDeployRequest, Ct);

            Assert.True(deploy.IsSuccessStatusCode);
        }

        string candidateInstanceId;

        using (var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative)))
        {
            request.Headers.Add("X-Tenant-Id", "tenant-a");
            request.Content = JsonContent.Create(new { processDefinitionKey = "security-candidate" });

            using var response = await client.SendAsync(request, Ct);
            candidateInstanceId = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct))
                .RootElement.GetProperty("processInstanceId").GetString()!;
        }

        var task = Assert.Single(await JsonArrayAsync(
            client, $"/api/runtime/tasks?processInstanceId={candidateInstanceId}", "tenant-a"));

        using (var claim = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/tasks/{task.GetProperty("id").GetString()}/claim", UriKind.Relative)))
        {
            claim.Headers.Add("X-User-Id", "bob");
            claim.Headers.Add("X-Tenant-Id", "tenant-a");
            claim.Content = JsonContent.Create(new { });

            using var response = await client.SendAsync(claim, Ct);
            Assert.True(response.IsSuccessStatusCode, $"按请求头认领失败：{response.StatusCode}");
        }

        var detail = await GetJsonAsync(
            client,
            $"/api/runtime/tasks/{task.GetProperty("id").GetString()}",
            "tenant-a");

        Assert.Equal("bob", detail.GetProperty("assignee").GetString());
    }

    [Fact]
    public async Task Idempotency_ShouldReplayAndRejectDifferentRequest()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        using (var content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml"))
        using (var deploy = await client.PostAsync(
            new Uri("/api/designer/deploy", UriKind.Relative), content, Ct))
        {
            Assert.True(deploy.IsSuccessStatusCode);
        }

        var key = Guid.NewGuid().ToString("N");
        var payload = """{"processDefinitionKey":"security-single","businessKey":"BIZ-IDEM-1"}""";

        var (firstStatus, firstBody, firstReplayed) = await PostRawAsync(client, payload, key);
        var (secondStatus, secondBody, secondReplayed) = await PostRawAsync(client, payload, key);

        Assert.True((int)firstStatus is >= 200 and < 300);
        Assert.Equal(firstStatus, secondStatus);
        Assert.Equal(firstBody, secondBody);
        Assert.False(firstReplayed);
        Assert.True(secondReplayed);

        var firstId = JsonDocument.Parse(firstBody).RootElement.GetProperty("processInstanceId").GetString();

        // 只有一次真实执行（按业务主键反查）
        var instances = await JsonArrayAsync(client, "/api/runtime/process-instances?businessKey=BIZ-IDEM-1");
        Assert.Equal(firstId, Assert.Single(instances).GetProperty("id").GetString());

        // 同 key 不同请求体 → 409
        var (conflictStatus, _, _) = await PostRawAsync(
            client,
            """{"processDefinitionKey":"security-single","businessKey":"BIZ-IDEM-2"}""",
            key);

        Assert.Equal(HttpStatusCode.Conflict, conflictStatus);

        static async Task<(HttpStatusCode Status, string Body, bool Replayed)> PostRawAsync(
            HttpClient client,
            string json,
            string idempotencyKey)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

            request.Headers.Add("Idempotency-Key", idempotencyKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            var replayed = response.Headers.TryGetValues("Idempotency-Replayed", out var values)
                && values.Contains("true", StringComparer.OrdinalIgnoreCase);

            return (response.StatusCode, body, replayed);
        }
    }

    [Fact]
    public async Task PaginationEnvelope_ShouldReturnMetadata()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        using (var content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml"))
        using (var deploy = await client.PostAsync(
            new Uri("/api/designer/deploy", UriKind.Relative), content, Ct))
        {
            Assert.True(deploy.IsSuccessStatusCode);
        }

        using (var start = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances", UriKind.Relative),
            new { processDefinitionKey = "security-single", businessKey = "BIZ-PAGE-1" },
            Ct))
        {
            Assert.True(start.IsSuccessStatusCode);
        }

        // 默认：裸数组 + X-Total-Count（兼容既有调用方）
        using (var response = await client.GetAsync(
            new Uri("/api/runtime/tasks?assignee=applicant", UriKind.Relative), Ct))
        {
            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("1", response.Headers.GetValues("X-Total-Count").Single());
            Assert.Equal(JsonValueKind.Array, (await ParseAsync(response)).ValueKind);
        }

        // envelope=true：{ items, total, firstResult, maxResults }
        var envelope = await GetJsonAsync(
            client,
            "/api/runtime/tasks?assignee=applicant&envelope=true&firstResult=0&maxResults=5");

        Assert.Equal(1, envelope.GetProperty("total").GetInt32());
        Assert.Equal(0, envelope.GetProperty("firstResult").GetInt32());
        Assert.Equal(5, envelope.GetProperty("maxResults").GetInt32());
        Assert.Equal(1, envelope.GetProperty("items").GetArrayLength());

        // maxResults 超过上限会被钳到 200
        var clamped = await GetJsonAsync(
            client,
            "/api/runtime/tasks?assignee=applicant&envelope=true&maxResults=10000");

        Assert.Equal(200, clamped.GetProperty("maxResults").GetInt32());
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static async Task<JsonElement[]> JsonArrayAsync(
        HttpClient client,
        string path,
        string? tenantId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        if (tenantId is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantId);
        }

        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();

        return (await ParseAsync(response)).EnumerateArray().ToArray();
    }

    private static async Task<JsonElement> GetJsonAsync(
        HttpClient client,
        string path,
        string? tenantId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        if (tenantId is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantId);
        }

        using var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();

        return await ParseAsync(response);
    }

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string SingleTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="security-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" activiti:assignee="applicant" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;

    private static string CandidateProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="security-candidate" isExecutable="true">
            <startEvent id="start" />
            <userTask id="approve" activiti:candidateGroups="managers" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="approve" />
            <sequenceFlow id="f2" sourceRef="approve" targetRef="end" />
          </process>
        </definitions>
        """;
}
