using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 用户级 JWT 鉴权（HS256）：与 API Key 并存、令牌校验、身份声明透传、幂等按身份隔离。
/// <para>与其余 REST 用例一样，走真实宿主 + 真实 MySQL；数据库不可用时动态跳过。</para>
/// </summary>
[Collection("MySql")]
public sealed class JwtAuthenticationTests : IClassFixture<RestEngineFactory>
{
    private const string ApiKey = "netbpm-test-key";

    // HS256 要求密钥至少 32 字节
    private const string SigningKey = "netbpm-jwt-signing-key-0123456789-abcdefghij";
    private const string Issuer = "https://idp.netbpm.test";
    private const string Audience = "netbpm-api";

    private readonly RestEngineFactory _factory;

    public JwtAuthenticationTests(RestEngineFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Jwt_ShouldRejectMissingForgedExpiredAndWrongAudience()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        using (var anonymous = await client.GetAsync(
            new Uri("/api/designer/definitions", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal("Bearer", anonymous.Headers.WwwAuthenticate.ToString());
        }

        // 伪造签名：用另一把密钥签发
        using (var forged = await SendWithTokenAsync(
            client, CreateToken(signingKey: new string('k', 48)), "/api/designer/definitions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        // 已过期（时钟偏移 30s，取 10 分钟前）
        using (var expired = await SendWithTokenAsync(
            client,
            CreateToken(notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddMinutes(-10)),
            "/api/designer/definitions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }

        // 受众不匹配
        using (var wrongAudience = await SendWithTokenAsync(
            client, CreateToken(audience: "another-api"), "/api/designer/definitions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
        }

        // 签名、iss、aud、有效期全部正确（读设计器数据需要 model.read scope）
        using (var valid = await SendWithTokenAsync(
            client, CreateToken(scopes: "netbpm.admin.model.read"), "/api/designer/definitions"))
        {
            Assert.True(valid.IsSuccessStatusCode, $"有效 JWT 仍被拒绝：{valid.StatusCode}");
        }
    }

    [Fact]
    public async Task Jwt_IdentityClaims_ShouldFlowAndIgnoreSpoofedHeaders()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        using (var content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml"))
        using (var deployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            deployRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "designer", tenantId: "tenant-jwt", scopes: "netbpm.admin"));
            deployRequest.Content = content;

            using var deploy = await client.SendAsync(deployRequest, Ct);
            var body = await deploy.Content.ReadAsStringAsync(Ct);

            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode} {body}");
        }

        string instanceId;

        using (var startRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative)))
        {
            startRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "carol", tenantId: "tenant-jwt"));

            // 请求头试图冒充他人：JWT 模式下必须被忽略
            startRequest.Headers.Add("X-User-Id", "mallory");
            startRequest.Headers.Add("X-Tenant-Id", "tenant-evil");

            startRequest.Content = JsonContent.Create(new
            {
                processDefinitionKey = "jwt-single",
                businessKey = "BIZ-JWT-1",
            });

            using var start = await client.SendAsync(startRequest, Ct);
            var body = await start.Content.ReadAsStringAsync(Ct);

            Assert.True(start.IsSuccessStatusCode, $"启动失败：{start.StatusCode} {body}");
            instanceId = JsonDocument.Parse(body).RootElement.GetProperty("processInstanceId").GetString()!;
        }

        using (var detailRequest = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/api/runtime/process-instances/{instanceId}", UriKind.Relative)))
        {
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "carol", tenantId: "tenant-jwt"));

            using var detail = await client.SendAsync(detailRequest, Ct);
            var body = await detail.Content.ReadAsStringAsync(Ct);

            Assert.True(detail.IsSuccessStatusCode, $"查询失败：{detail.StatusCode} {body}");

            var instance = JsonDocument.Parse(body).RootElement;

            Assert.Equal("carol", instance.GetProperty("startUserId").GetString());
            Assert.Equal("tenant-jwt", instance.GetProperty("tenantId").GetString());
        }

        // 租户隔离：列表按令牌里的租户过滤，换一个租户的令牌看不到该实例
        using (var otherTenant = await SendWithTokenAsync(
            client,
            CreateToken(userId: "carol", tenantId: "tenant-other"),
            "/api/runtime/process-instances"))
        {
            Assert.True(otherTenant.IsSuccessStatusCode);

            var body = await otherTenant.Content.ReadAsStringAsync(Ct);
            Assert.Empty(JsonDocument.Parse(body).RootElement.EnumerateArray());
        }
    }

    [Fact]
    public async Task ApiKey_ShouldStillWorkWhenJwtIsAlsoEnabled()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var secured = BuildHost(jwt: true, apiKey: ApiKey);
        using var client = secured.CreateClient();

        using (var missing = await client.GetAsync(
            new Uri("/api/designer/definitions", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        }

        using (var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/designer/definitions", UriKind.Relative)))
        {
            request.Headers.Add("X-Api-Key", ApiKey);

            using var response = await client.SendAsync(request, Ct);
            Assert.True(response.IsSuccessStatusCode, $"API Key 与 JWT 并存时被拒绝：{response.StatusCode}");
        }

        // 服务密钥写错仍然 401
        using (var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/designer/definitions", UriKind.Relative)))
        {
            request.Headers.Add("X-Api-Key", "wrong-key");

            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Jwt_ShouldRejectTenantOrUserOverrides()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();
        var token = CreateToken(userId: "carol", tenantId: "tenant-jwt");

        // 请求体指定其它租户
        using (var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new
            {
                processDefinitionKey = "jwt-single",
                tenantId = "tenant-evil",
            });

            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 请求体冒充其它发起人
        using (var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new
            {
                processDefinitionKey = "jwt-single",
                startUserId = "mallory",
            });

            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 部署时用 query 指定其它租户
        using (var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy?tenantId=tenant-evil", UriKind.Relative)))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml");

            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 列表查询指定其它租户
        using (var response = await SendWithTokenAsync(
            client, token, "/api/designer/definitions?tenantId=tenant-evil"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Jwt_CrossTenantById_ShouldBeNotFound()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        var owner = CreateToken(userId: "carol", tenantId: "tenant-jwt");
        var intruder = CreateToken(userId: "dave", tenantId: "tenant-other");

        var instanceId = await DeployAndStartAsync(client, owner, "BIZ-TENANT-1");
        var taskId = await GetSingleTaskIdAsync(client, owner, instanceId);

        // 同租户：详情 / 任务 / 历史都可见
        using (var own = await SendWithTokenAsync(
            client, owner, $"/api/runtime/tasks/{taskId}"))
        {
            Assert.True(own.IsSuccessStatusCode, $"同租户读任务失败：{own.StatusCode}");
        }

        var deniedPaths = new[]
        {
            $"/api/runtime/process-instances/{instanceId}",
            $"/api/runtime/process-instances/{instanceId}/variables",
            $"/api/runtime/process-instances/{instanceId}/diagram",
            $"/api/runtime/tasks/{taskId}",
            $"/api/runtime/tasks/{taskId}/variables",
            $"/api/runtime/tasks/{taskId}/identity-links",
            $"/api/runtime/history/process-instances/{instanceId}",
            $"/api/runtime/history/process-instances/{instanceId}/activities",
            $"/api/runtime/history/process-instances/{instanceId}/variables",
        };

        foreach (var path in deniedPaths)
        {
            using var response = await SendWithTokenAsync(client, intruder, path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // 跨租户写操作同样 404（不是 403，避免存在性泄露）
        using (var complete = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/tasks/{taskId}/complete", UriKind.Relative)))
        {
            complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", intruder);
            complete.Content = JsonContent.Create(new { });

            using var response = await client.SendAsync(complete, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // 跨租户的管理动作同样 404：授权过滤器的固定顺序是「先存在性 / 租户，后 scope」，
        // 否则无 scope 的越权者会拿到 403，等于被告知该对象确实存在。
        using (var delete = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/runtime/process-instances/{instanceId}", UriKind.Relative)))
        {
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", intruder);

            using var response = await client.SendAsync(delete, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var suspend = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/process-instances/{instanceId}/suspend", UriKind.Relative)))
        {
            suspend.Headers.Authorization = new AuthenticationHeaderValue("Bearer", intruder);

            using var response = await client.SendAsync(suspend, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // 历史任务列表走查询条件过滤：跨租户返回空集合而不是 404（不泄露是否存在）
        using (var foreignTasks = await SendWithTokenAsync(
            client, intruder, $"/api/runtime/history/process-instances/{instanceId}/tasks"))
        {
            Assert.True(foreignTasks.IsSuccessStatusCode);
            var body = await foreignTasks.Content.ReadAsStringAsync(Ct);
            Assert.Empty(JsonDocument.Parse(body).RootElement.EnumerateArray());
        }

        // 历史列表按令牌租户过滤
        using (var ownHistory = await SendWithTokenAsync(
            client, owner, "/api/runtime/history/process-instances"))
        {
            var body = await ownHistory.Content.ReadAsStringAsync(Ct);
            Assert.Contains(instanceId, body, StringComparison.Ordinal);
        }

        using (var otherHistory = await SendWithTokenAsync(
            client, intruder, "/api/runtime/history/process-instances"))
        {
            var body = await otherHistory.Content.ReadAsStringAsync(Ct);
            Assert.Empty(JsonDocument.Parse(body).RootElement.EnumerateArray());
        }
    }

    [Fact]
    public async Task Mutations_ShouldReturnProperStatusCodesAndAllowBodylessClaim()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();
        var token = CreateToken(userId: "carol", tenantId: "tenant-jwt");

        var instanceId = await DeployAndStartAsync(client, token, "BIZ-STATUS-1");
        var taskId = await GetSingleTaskIdAsync(client, token, instanceId);

        // 0) 先退回未分配（BPMN 里默认 assignee 是 applicant）
        using (var unassign = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/tasks/{taskId}/assignee", UriKind.Relative)))
        {
            unassign.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            unassign.Content = JsonContent.Create(new { assignee = (string?)null });

            using var response = await client.SendAsync(unassign, Ct);
            Assert.True(response.IsSuccessStatusCode, $"退回未分配失败：{response.StatusCode}");
        }

        // 1) 无请求体认领：身份来自 JWT（历史实现会误报 400）
        using (var claim = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/tasks/{taskId}/claim", UriKind.Relative)))
        {
            claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(claim, Ct);
            Assert.True(response.IsSuccessStatusCode, $"无 body 认领失败：{response.StatusCode}");
        }

        // 2) 已认领 → 409
        using (var secondClaim = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/runtime/tasks/{taskId}/claim", UriKind.Relative)))
        {
            secondClaim.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "dave", tenantId: "tenant-jwt"));
            secondClaim.Content = JsonContent.Create(new { });

            using var response = await client.SendAsync(secondClaim, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        // 3) 未知任务 → 404（变更类不再统一 400）
        using (var missing = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/tasks/999999999/complete", UriKind.Relative)))
        {
            missing.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            missing.Content = JsonContent.Create(new { });

            using var response = await client.SendAsync(missing, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // 4) 部署仍有运行实例时删除 → 409
        string deploymentId;
        using (var definitions = await SendWithTokenAsync(
            client,
            CreateToken(userId: "ops", tenantId: "tenant-jwt", scopes: "netbpm.admin.model.read"),
            "/api/designer/definitions?key=jwt-single"))
        {
            var body = await definitions.Content.ReadAsStringAsync(Ct);
            deploymentId = JsonDocument.Parse(body).RootElement[0]
                .GetProperty("deploymentId").GetString()!;
        }

        using (var delete = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/designer/deployments/{deploymentId}", UriKind.Relative)))
        {
            // 删部署需要管理 scope（本用例只关心「仍有运行实例 → 409」）
            delete.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "ops", tenantId: "tenant-jwt", scopes: "netbpm.admin"));

            using var response = await client.SendAsync(delete, Ct);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task TaskActions_ShouldRequireAssigneeCandidateOrAdmin()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        var carol = CreateToken(userId: "carol", tenantId: "tenant-jwt");
        var applicant = CreateToken(userId: "applicant", tenantId: "tenant-jwt");
        var admin = CreateToken(userId: "ops", tenantId: "tenant-jwt", scopes: "netbpm.admin");

        // BPMN 默认 assignee=applicant
        var instanceId = await DeployAndStartAsync(client, carol, "BIZ-AUTHZ-1");
        var taskId = await GetSingleTaskIdAsync(client, carol, instanceId);

        // 非办理人完成 → 403
        using (var response = await PostEmptyAsync(
            client, carol, $"/api/runtime/tasks/{taskId}/complete"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 办理人完成 → 204
        using (var response = await PostEmptyAsync(
            client, applicant, $"/api/runtime/tasks/{taskId}/complete"))
        {
            Assert.True(response.IsSuccessStatusCode, $"办理人完成失败：{response.StatusCode}");
        }

        // 管理 scope 可越权完成
        var adminInstanceId = await DeployAndStartAsync(client, carol, "BIZ-AUTHZ-2");
        var adminTaskId = await GetSingleTaskIdAsync(client, carol, adminInstanceId);

        using (var response = await PostEmptyAsync(
            client, admin, $"/api/runtime/tasks/{adminTaskId}/complete"))
        {
            Assert.True(response.IsSuccessStatusCode, $"管理员完成失败：{response.StatusCode}");
        }

        // 候选组：组内可认领，组外 403
        var candidateInstanceId = await DeployAndStartAsync(client, carol, "BIZ-AUTHZ-3");
        var candidateTaskId = await GetSingleTaskIdAsync(client, carol, candidateInstanceId);

        using (var unassign = await PostJsonAsync(
            client, carol, $"/api/runtime/tasks/{candidateTaskId}/assignee", new { assignee = (string?)null }))
        {
            Assert.True(unassign.IsSuccessStatusCode, $"退回未分配失败：{unassign.StatusCode}");
        }

        using (var addGroup = await PostJsonAsync(
            client, carol, $"/api/runtime/tasks/{candidateTaskId}/candidate-groups", new { groupId = "finance" }))
        {
            Assert.True(addGroup.IsSuccessStatusCode, $"加候选组失败：{addGroup.StatusCode}");
        }

        var outsider = CreateToken(userId: "erin", tenantId: "tenant-jwt");

        using (var response = await PostEmptyAsync(
            client, outsider, $"/api/runtime/tasks/{candidateTaskId}/claim"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var groupMember = CreateToken(
            userId: "dave", tenantId: "tenant-jwt", groups: "finance sales");

        using (var response = await PostEmptyAsync(
            client, groupMember, $"/api/runtime/tasks/{candidateTaskId}/claim"))
        {
            Assert.True(response.IsSuccessStatusCode, $"候选组认领失败：{response.StatusCode}");
        }

        // 非办理人取消认领 → 403；办理人 → 204
        using (var response = await PostJsonAsync(
            client, carol, $"/api/runtime/tasks/{candidateTaskId}/claim", new { unclaim = true }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var response = await PostJsonAsync(
            client, groupMember, $"/api/runtime/tasks/{candidateTaskId}/claim", new { unclaim = true }))
        {
            Assert.True(response.IsSuccessStatusCode, $"办理人取消认领失败：{response.StatusCode}");
        }
    }

    [Fact]
    public async Task TaskModeration_ShouldRequireAssigneeOwnerOrInitiator()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        var initiator = CreateToken(userId: "carol", tenantId: "tenant-jwt");
        var outsider = CreateToken(userId: "erin", tenantId: "tenant-jwt");

        var instanceId = await DeployAndStartAsync(client, initiator, "BIZ-MOD-1");
        var taskId = await GetSingleTaskIdAsync(client, initiator, instanceId);

        // 非参与人（同租户）不能转办 / 加候选人 —— 否则可先改办自己再完成，绕过任务级授权
        using (var reassign = await PostJsonAsync(
            client, outsider, $"/api/runtime/tasks/{taskId}/assignee", new { assignee = "erin" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, reassign.StatusCode);
        }

        using (var addCandidate = await PostJsonAsync(
            client, outsider, $"/api/runtime/tasks/{taskId}/candidate-users", new { userId = "erin" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, addCandidate.StatusCode);
        }

        using (var patch = new HttpRequestMessage(
            new HttpMethod("PATCH"), new Uri($"/api/runtime/tasks/{taskId}", UriKind.Relative)))
        {
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", outsider);
            patch.Content = JsonContent.Create(new { priority = 7 });

            using var response = await client.SendAsync(patch, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // 发起人（流程负责人）仍可转办
        using (var allowed = await PostJsonAsync(
            client, initiator, $"/api/runtime/tasks/{taskId}/assignee", new { assignee = "bob" }))
        {
            Assert.True(allowed.IsSuccessStatusCode, $"发起人转办被拒：{allowed.StatusCode}");
        }

        // 历史变量补漏：非参与人读历史变量同样 403（与运行态变量授权同口径）
        using (var historic = await SendWithTokenAsync(
            client, outsider, $"/api/runtime/history/process-instances/{instanceId}/variables"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, historic.StatusCode);
        }
    }

    [Fact]
    public async Task LegacyBearerApiKey_ShouldStillWorkWhenJwtIsDisabled()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var secured = BuildHost(jwt: false, apiKey: ApiKey);
        using var client = secured.CreateClient();

        // 旧调用方把服务密钥放在 Bearer 里：未启用 JWT 时按 API Key 处理
        using var response = await SendWithTokenAsync(client, ApiKey, "/api/designer/definitions");

        Assert.True(response.IsSuccessStatusCode, $"Bearer API Key 被拒绝：{response.StatusCode}");
    }

    [Fact]
    public async Task Idempotency_ShouldBeScopedToJwtIdentity()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost(jwt: true);
        using var client = secured.CreateClient();

        using (var deployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            deployRequest.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer", CreateToken(userId: "designer", tenantId: "tenant-jwt", scopes: "netbpm.admin"));
            deployRequest.Content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml");

            using var deploy = await client.SendAsync(deployRequest, Ct);
            Assert.True(deploy.IsSuccessStatusCode);
        }

        var key = Guid.NewGuid().ToString("N");
        var payload = """{"processDefinitionKey":"jwt-single","businessKey":"BIZ-JWT-IDEM"}""";

        var (firstStatus, firstBody, firstReplayed) = await PostRawAsync(
            client, CreateToken(userId: "carol", tenantId: "tenant-jwt"), payload, key);

        Assert.True((int)firstStatus is >= 200 and < 300);
        Assert.False(firstReplayed);

        var carolInstanceId = JsonDocument.Parse(firstBody)
            .RootElement.GetProperty("processInstanceId").GetString();

        // 同一用户重试：回放原响应
        var (replayStatus, replayBody, replayed) = await PostRawAsync(
            client, CreateToken(userId: "carol", tenantId: "tenant-jwt"), payload, key);

        Assert.Equal(firstStatus, replayStatus);
        Assert.Equal(firstBody, replayBody);
        Assert.True(replayed);

        // 另一用户用同一个 key：不得拿到别人的响应
        var (_, otherBody, otherReplayed) = await PostRawAsync(
            client, CreateToken(userId: "dave", tenantId: "tenant-jwt"), payload, key);

        // 幂等键按调用主体收窄后，dave 的键落在自己的命名空间里：
        //   · 安全诉求仍然满足 —— 他不回放 carol 的响应，拿到的是自己的新实例；
        //   · 而「冲突」不再是这里该有的结果：用「业务单号 + 步骤」当幂等键时，
        //     第二个人的第一次调用若被别人的键挡成 409，他就永远发不出去。
        // 这条断言 v1.19 前是 409（当时键是全局的），语义变更后随之改为「各自成立」。
        Assert.False(otherReplayed);

        var daveInstanceId = JsonDocument.Parse(otherBody)
            .RootElement.GetProperty("processInstanceId").GetString();

        Assert.NotEqual(carolInstanceId, daveInstanceId);

        static async Task<(HttpStatusCode Status, string Body, bool Replayed)> PostRawAsync(
            HttpClient client,
            string token,
            string json,
            string idempotencyKey)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            var wasReplayed = response.Headers.TryGetValues("Idempotency-Replayed", out var values)
                && values.Contains("true", StringComparer.OrdinalIgnoreCase);

            return (response.StatusCode, body, wasReplayed);
        }
    }

    [Fact]
    public async Task VariableAccess_ShouldRequireInstanceParticipantOrAdmin()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        // 同时启用 API Key：验证服务密钥仍然免参与人校验
        using var secured = BuildHost(jwt: true, apiKey: ApiKey);
        using var client = secured.CreateClient();

        var carol = CreateToken(userId: "carol", tenantId: "tenant-jwt");        // 发起人
        var applicant = CreateToken(userId: "applicant", tenantId: "tenant-jwt"); // 任务办理人
        var outsider = CreateToken(userId: "erin", tenantId: "tenant-jwt");
        var admin = CreateToken(userId: "ops", tenantId: "tenant-jwt", scopes: "netbpm.admin");

        var instanceId = await DeployAndStartAsync(client, carol, "BIZ-VAR-1");
        var taskId = await GetSingleTaskIdAsync(client, carol, instanceId);
        var instanceVariables = $"/api/runtime/process-instances/{instanceId}/variables";

        // 发起人：实例变量读写
        using (var read = await SendWithTokenAsync(client, carol, instanceVariables))
        {
            Assert.True(read.IsSuccessStatusCode, $"发起人读变量失败：{read.StatusCode}");
        }

        using (var write = await PutJsonAsync(client, carol, $"{instanceVariables}/amount", 100))
        {
            Assert.True(write.IsSuccessStatusCode, $"发起人写变量失败：{write.StatusCode}");
        }

        // 办理人（实例上的活跃任务参与人）可读
        using (var read = await SendWithTokenAsync(client, applicant, instanceVariables))
        {
            Assert.True(read.IsSuccessStatusCode, $"办理人读变量失败：{read.StatusCode}");
        }

        // 非参与人：读 / 写 / 删一律 403
        using (var read = await SendWithTokenAsync(client, outsider, instanceVariables))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }

        using (var write = await PutJsonAsync(client, outsider, $"{instanceVariables}/amount", 999))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }

        using (var remove = await DeleteWithTokenAsync(client, outsider, $"{instanceVariables}/amount"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        }

        // 管理 scope 可越过参与人校验
        using (var read = await SendWithTokenAsync(client, admin, instanceVariables))
        {
            Assert.True(read.IsSuccessStatusCode, $"管理员读变量失败：{read.StatusCode}");
        }

        // 任务局部变量：办理人与发起人可读写，非参与人 403
        using (var write = await PutJsonAsync(
            client, applicant, $"/api/runtime/tasks/{taskId}/variables/local", "ok"))
        {
            Assert.True(write.IsSuccessStatusCode, $"办理人写任务变量失败：{write.StatusCode}");
        }

        using (var read = await SendWithTokenAsync(client, carol, $"/api/runtime/tasks/{taskId}/variables"))
        {
            Assert.True(read.IsSuccessStatusCode, $"发起人读任务变量失败：{read.StatusCode}");
        }

        using (var read = await SendWithTokenAsync(client, outsider, $"/api/runtime/tasks/{taskId}/variables"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }

        using (var write = await PutJsonAsync(
            client, outsider, $"/api/runtime/tasks/{taskId}/variables/local", "hack"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        }

        // 候选人：退回未分配后加候选用户，候选人可读写任务变量
        using (var unassign = await PostJsonAsync(
            client, carol, $"/api/runtime/tasks/{taskId}/assignee", new { assignee = (string?)null }))
        {
            Assert.True(unassign.IsSuccessStatusCode, $"退回未分配失败：{unassign.StatusCode}");
        }

        using (var addCandidate = await PostJsonAsync(
            client, carol, $"/api/runtime/tasks/{taskId}/candidate-users", new { userId = "frank" }))
        {
            Assert.True(addCandidate.IsSuccessStatusCode, $"加候选用户失败：{addCandidate.StatusCode}");
        }

        var frank = CreateToken(userId: "frank", tenantId: "tenant-jwt");

        using (var read = await SendWithTokenAsync(client, frank, $"/api/runtime/tasks/{taskId}/variables"))
        {
            Assert.True(read.IsSuccessStatusCode, $"候选人读任务变量失败：{read.StatusCode}");
        }

        using (var read = await SendWithTokenAsync(client, outsider, $"/api/runtime/tasks/{taskId}/variables"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }

        // API Key 代表可信服务：不做参与人校验
        using (var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(instanceVariables, UriKind.Relative)))
        {
            request.Headers.Add("X-Api-Key", ApiKey);

            using var response = await client.SendAsync(request, Ct);
            Assert.True(response.IsSuccessStatusCode, $"API Key 读变量被拒：{response.StatusCode}");
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async Task<HttpResponseMessage> PostEmptyAsync(
        HttpClient client,
        string token,
        string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            return await client.SendAsync(request, Ct);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string token,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> PutJsonAsync(
        HttpClient client,
        string token,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> DeleteWithTokenAsync(
        HttpClient client,
        string token,
        string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> DeployAndStartAsync(
        HttpClient client,
        string token,
        string businessKey)
    {
        // 部署需要管理 scope：单独签一个同租户的部署令牌，避免给业务令牌（办理人 / 发起人）附带管理能力
        var deployToken = CreateToken(userId: "deployer", tenantId: "tenant-jwt", scopes: "netbpm.admin");

        using (var deployRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/designer/deploy", UriKind.Relative)))
        {
            deployRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deployToken);
            deployRequest.Content = new StringContent(SingleTaskProcess(), Encoding.UTF8, "text/xml");

            using var deploy = await client.SendAsync(deployRequest, Ct);
            var body = await deploy.Content.ReadAsStringAsync(Ct);

            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode} {body}");
        }

        using var startRequest = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

        startRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        startRequest.Content = JsonContent.Create(new
        {
            processDefinitionKey = "jwt-single",
            businessKey,
        });

        using var start = await client.SendAsync(startRequest, Ct);
        var startBody = await start.Content.ReadAsStringAsync(Ct);

        Assert.True(start.IsSuccessStatusCode, $"启动失败：{start.StatusCode} {startBody}");

        return JsonDocument.Parse(startBody).RootElement.GetProperty("processInstanceId").GetString()!;
    }

    private static async Task<string> GetSingleTaskIdAsync(
        HttpClient client,
        string token,
        string instanceId)
    {
        using var response = await SendWithTokenAsync(
            client, token, $"/api/runtime/tasks?processInstanceId={instanceId}");

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"任务查询失败：{response.StatusCode} {body}");

        var items = JsonDocument.Parse(body).RootElement;

        Assert.Equal(1, items.GetArrayLength());

        return items[0].GetProperty("id").GetString()!;
    }

    private WebApplicationFactory<Program> BuildHost(bool jwt, string? apiKey = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:ApiKey", apiKey ?? string.Empty);

            if (jwt)
            {
                builder.UseSetting("NetBpm:Security:Jwt:SigningKey", SigningKey);
                builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
                builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);
            }
        });

    private static string CreateToken(
        string signingKey = SigningKey,
        string? issuer = Issuer,
        string? audience = Audience,
        string? userId = "carol",
        string? tenantId = "tenant-jwt",
        DateTime? notBefore = null,
        DateTime? expires = null,
        string? groups = null,
        string? scopes = null)
    {
        var claims = new List<Claim>();

        if (userId is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, userId));
        }

        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant", tenantId));
        }

        if (groups is not null)
        {
            claims.Add(new Claim("groups", groups));
        }

        if (scopes is not null)
        {
            claims.Add(new Claim("scope", scopes));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<HttpResponseMessage> SendWithTokenAsync(
        HttpClient client,
        string token,
        string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request, Ct);
    }

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static string SingleTaskProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="jwt-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" activiti:assignee="applicant" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;
}
