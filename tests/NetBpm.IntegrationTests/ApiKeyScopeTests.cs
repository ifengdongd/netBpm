using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// API Key 服务作用域：<c>NetBpm:Security:ApiKeyTenants</c> 非空时，密钥只能在指定租户内读写。
/// </summary>
[Collection("MySql")]
public sealed class ApiKeyScopeTests : IClassFixture<RestEngineFactory>
{
    private const string ApiKey = "netbpm-scoped-key";

    private readonly RestEngineFactory _factory;

    public ApiKeyScopeTests(RestEngineFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SingleTenantScope_ShouldDefaultToThatTenantAndRejectOthers()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost("tenant-a");
        using var client = secured.CreateClient();

        using (var deployed = await DeployAsync(client, tenantId: null))
        {
            Assert.True(deployed.IsSuccessStatusCode, $"单租户密钥部署失败：{deployed.StatusCode}");
        }

        // 未显式指定租户时，列表自动落到唯一授权租户
        var definitions = await GetArrayAsync(client, "/api/designer/definitions");
        Assert.NotEmpty(definitions);
        Assert.All(
            definitions,
            definition => Assert.Equal("tenant-a", definition.GetProperty("tenantId").GetString()));

        // 超范围租户 → 403
        using (var deniedDeploy = await DeployAsync(client, tenantId: "tenant-b"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedDeploy.StatusCode);
        }

        using (var deniedList = await GetAsync(client, "/api/designer/definitions?tenantId=tenant-b"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        }

        // 启动实例同样自动落到唯一租户
        var instanceId = await StartAsync(client);
        using (var instance = await GetAsync(client, $"/api/runtime/process-instances/{instanceId}"))
        {
            Assert.True(instance.IsSuccessStatusCode, $"读取实例失败：{instance.StatusCode}");

            var body = await instance.Content.ReadAsStringAsync(Ct);
            Assert.Equal(
                "tenant-a",
                JsonDocument.Parse(body).RootElement.GetProperty("tenantId").GetString());
        }
    }

    [Fact]
    public async Task MultipleTenants_ShouldRequireExplicitTenant()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost("tenant-a", "tenant-b");
        using var client = secured.CreateClient();

        // 不指定租户 → 403（避免“全租户”查询 / 写入）
        using (var ambiguous = await DeployAsync(client, tenantId: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, ambiguous.StatusCode);
        }

        using (var explicitDeploy = await DeployAsync(client, tenantId: "tenant-b"))
        {
            Assert.True(explicitDeploy.IsSuccessStatusCode, $"指定租户部署失败：{explicitDeploy.StatusCode}");
        }

        var definitions = await GetArrayAsync(client, "/api/designer/definitions?tenantId=tenant-b");
        Assert.NotEmpty(definitions);
        Assert.All(
            definitions,
            definition => Assert.Equal("tenant-b", definition.GetProperty("tenantId").GetString()));
    }

    [Fact]
    public async Task CrossTenantById_ShouldBeNotFoundForScopedKey()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        string instanceId;

        using (var tenantA = BuildHost("tenant-a"))
        {
            using var clientA = tenantA.CreateClient();

            using (var deployed = await DeployAsync(clientA, tenantId: null))
            {
                Assert.True(deployed.IsSuccessStatusCode, $"部署失败：{deployed.StatusCode}");
            }

            instanceId = await StartAsync(clientA);
        }

        using (var tenantB = BuildHost("tenant-b"))
        {
            using var clientB = tenantB.CreateClient();

            using var response = await GetAsync(
                clientB, $"/api/runtime/process-instances/{instanceId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task ScopedKey_ShouldAllowTaskUpdateAssignAndInstanceDelete()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var secured = BuildHost("tenant-a");
        using var client = secured.CreateClient();

        using (var deployed = await DeployAsync(client, tenantId: null))
        {
            Assert.True(deployed.IsSuccessStatusCode, $"部署失败：{deployed.StatusCode}");
        }

        var instanceId = await StartAsync(client);
        var taskId = await SingleTaskIdAsync(client, instanceId);
        var dueDate = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        int originalRevision;

        using (var detail = await GetAsync(client, $"/api/runtime/tasks/{taskId}"))
        {
            Assert.True(detail.IsSuccessStatusCode, $"读取任务失败：{detail.StatusCode}");

            var task = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.Equal("tenant-a", task.GetProperty("tenantId").GetString());
            Assert.Equal("apply", task.GetProperty("taskDefinitionKey").GetString());
            Assert.Equal("applicant", task.GetProperty("assignee").GetString());

            originalRevision = task.GetProperty("revision").GetInt32();
        }

        // PATCH：一次性写入全部可变字段；未提供的字段保持原值
        using (var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"/api/runtime/tasks/{taskId}",
            new
            {
                name = "审批（已修改）",
                description = "作用域测试写入",
                priority = 42,
                dueDate,
                category = "review",
                owner = "owner-a",
            }))
        {
            var body = await updated.Content.ReadAsStringAsync(Ct);
            Assert.True(updated.IsSuccessStatusCode, $"修改任务失败：{updated.StatusCode} {body}");
        }

        using (var detail = await GetAsync(client, $"/api/runtime/tasks/{taskId}"))
        {
            Assert.True(detail.IsSuccessStatusCode, $"读取任务失败：{detail.StatusCode}");

            var task = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.Equal("审批（已修改）", task.GetProperty("name").GetString());
            Assert.Equal("作用域测试写入", task.GetProperty("description").GetString());
            Assert.Equal(42, task.GetProperty("priority").GetInt32());
            Assert.Equal("review", task.GetProperty("category").GetString());
            Assert.Equal("owner-a", task.GetProperty("owner").GetString());
            Assert.Equal(dueDate, task.GetProperty("dueDate").GetDateTime());

            // 写入走乐观锁，版本号必须前进
            Assert.True(task.GetProperty("revision").GetInt32() > originalRevision);
        }

        // 转办：改派是管理动作，作用域内允许
        using (var assigned = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/tasks/{taskId}/assignee",
            new { assignee = "reviewer" }))
        {
            var body = await assigned.Content.ReadAsStringAsync(Ct);
            Assert.True(assigned.IsSuccessStatusCode, $"转办失败：{assigned.StatusCode} {body}");
        }

        using (var detail = await GetAsync(client, $"/api/runtime/tasks/{taskId}"))
        {
            var task = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.Equal("reviewer", task.GetProperty("assignee").GetString());
        }

        // 删除实例：作用域内允许，待办随之消失
        using (var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"/api/runtime/process-instances/{instanceId}",
            body: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var goneTask = await GetAsync(client, $"/api/runtime/tasks/{taskId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, goneTask.StatusCode);
        }

        using (var goneInstance = await GetAsync(client, $"/api/runtime/process-instances/{instanceId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, goneInstance.StatusCode);
        }
    }

    [Fact]
    public async Task CrossTenantTaskAndInstanceOperations_ShouldBeNotFound()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        string instanceId;
        string taskId;

        using (var tenantA = BuildHost("tenant-a"))
        {
            using var clientA = tenantA.CreateClient();

            using (var deployed = await DeployAsync(clientA, tenantId: null))
            {
                Assert.True(deployed.IsSuccessStatusCode, $"部署失败：{deployed.StatusCode}");
            }

            instanceId = await StartAsync(clientA);
            taskId = await SingleTaskIdAsync(clientA, instanceId);
        }

        using (var tenantB = BuildHost("tenant-b"))
        {
            using var clientB = tenantB.CreateClient();

            // 列表按生效租户过滤：看不到别的租户的待办
            var tasks = await GetArrayAsync(
                clientB, $"/api/runtime/tasks?processInstanceId={instanceId}");
            Assert.Empty(tasks);

            // 按 id 的读 / 写一律按「不存在」处理，避免泄露对象是否存在
            using (var detail = await GetAsync(clientB, $"/api/runtime/tasks/{taskId}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
            }

            using (var updated = await SendJsonAsync(
                clientB,
                HttpMethod.Patch,
                $"/api/runtime/tasks/{taskId}",
                new { priority = 99 }))
            {
                Assert.Equal(HttpStatusCode.NotFound, updated.StatusCode);
            }

            using (var assigned = await SendJsonAsync(
                clientB,
                HttpMethod.Post,
                $"/api/runtime/tasks/{taskId}/assignee",
                new { assignee = "intruder" }))
            {
                Assert.Equal(HttpStatusCode.NotFound, assigned.StatusCode);
            }

            using (var jumped = await SendJsonAsync(
                clientB,
                HttpMethod.Post,
                $"/api/runtime/process-instances/{instanceId}/jump",
                new { targetActivityId = "end" }))
            {
                Assert.Equal(HttpStatusCode.NotFound, jumped.StatusCode);
            }

            using (var deleted = await SendJsonAsync(
                clientB,
                HttpMethod.Delete,
                $"/api/runtime/process-instances/{instanceId}",
                body: null))
            {
                Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            }
        }

        // 回到 tenant-a：资源既没被改、也没被删
        using (var tenantA = BuildHost("tenant-a"))
        {
            using var clientA = tenantA.CreateClient();

            using (var detail = await GetAsync(clientA, $"/api/runtime/tasks/{taskId}"))
            {
                Assert.True(detail.IsSuccessStatusCode, $"任务被跨租户操作破坏：{detail.StatusCode}");

                var task = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
                Assert.Equal("applicant", task.GetProperty("assignee").GetString());
                Assert.NotEqual(99, task.GetProperty("priority").GetInt32());
            }

            using (var instance = await GetAsync(clientA, $"/api/runtime/process-instances/{instanceId}"))
            {
                Assert.True(instance.IsSuccessStatusCode, $"实例被跨租户操作破坏：{instance.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task OutOfScopeTenantHeader_ShouldBeForbidden()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        string instanceId;
        string taskId;

        using (var tenantA = BuildHost("tenant-a"))
        {
            using var clientA = tenantA.CreateClient();

            using (var deployed = await DeployAsync(clientA, tenantId: null))
            {
                Assert.True(deployed.IsSuccessStatusCode, $"部署失败：{deployed.StatusCode}");
            }

            instanceId = await StartAsync(clientA);
            taskId = await SingleTaskIdAsync(clientA, instanceId);
        }

        // 密钥只授权 tenant-b；用 X-Tenant-Id 声明 tenant-a 不能“自证”属于 tenant-a
        using (var tenantB = BuildHost("tenant-b"))
        {
            using var clientB = tenantB.CreateClient();

            using (var detail = await GetAsync(clientB, $"/api/runtime/tasks/{taskId}", tenantHeader: "tenant-a"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
            }

            using (var updated = await SendJsonAsync(
                clientB,
                HttpMethod.Patch,
                $"/api/runtime/tasks/{taskId}",
                new { priority = 99 },
                tenantHeader: "tenant-a"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, updated.StatusCode);
            }

            using (var instance = await GetAsync(
                clientB, $"/api/runtime/process-instances/{instanceId}", tenantHeader: "tenant-a"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, instance.StatusCode);
            }

            // 列表也会因为请求头超出服务作用域被拒，而不是悄悄改成别的租户
            using (var tasks = await GetAsync(
                clientB, "/api/runtime/tasks?assignee=applicant", tenantHeader: "tenant-a"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, tasks.StatusCode);
            }
        }

        // tenant-a 侧数据未被改动
        using (var tenantA = BuildHost("tenant-a"))
        {
            using var clientA = tenantA.CreateClient();

            using var detail = await GetAsync(clientA, $"/api/runtime/tasks/{taskId}");
            Assert.True(detail.IsSuccessStatusCode, $"任务被越界请求破坏：{detail.StatusCode}");

            var task = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.NotEqual(99, task.GetProperty("priority").GetInt32());
        }
    }

    [Fact]
    public async Task HeaderTenantWithinScope_ShouldFilterListsAndByidAccess()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        string taskA;
        string taskB;

        // 多租户密钥：部署与启动都必须显式给出租户
        using var secured = BuildHost("tenant-a", "tenant-b");
        using var client = secured.CreateClient();

        using (var deployedA = await DeployAsync(client, tenantId: "tenant-a"))
        {
            Assert.True(deployedA.IsSuccessStatusCode, $"租户 A 部署失败：{deployedA.StatusCode}");
        }

        using (var deployedB = await DeployAsync(client, tenantId: "tenant-b"))
        {
            Assert.True(deployedB.IsSuccessStatusCode, $"租户 B 部署失败：{deployedB.StatusCode}");
        }

        taskA = await SingleTaskIdAsync(client, await StartAsync(client, tenantId: "tenant-a"), "tenant-a");
        taskB = await SingleTaskIdAsync(client, await StartAsync(client, tenantId: "tenant-b"), "tenant-b");

        // 请求头是生效租户：只影响自己的列表
        var onlyA = await GetArrayAsync(client, "/api/runtime/tasks?assignee=applicant", tenantHeader: "tenant-a");
        Assert.Equal(taskA, Assert.Single(onlyA).GetProperty("id").GetString());

        var onlyB = await GetArrayAsync(client, "/api/runtime/tasks?assignee=applicant", tenantHeader: "tenant-b");
        Assert.Equal(taskB, Assert.Single(onlyB).GetProperty("id").GetString());

        // 多租户密钥不给租户 → 403（避免“全租户”查询）
        using (var ambiguous = await GetAsync(client, "/api/runtime/tasks?assignee=applicant"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, ambiguous.StatusCode);
        }

        // 作用域内的头可以访问，但别的租户的对象仍是 404
        using (var crossTenant = await GetAsync(client, $"/api/runtime/tasks/{taskA}", tenantHeader: "tenant-b"))
        {
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private WebApplicationFactory<Program> BuildHost(params string[] tenants) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:ApiKey", ApiKey);
            builder.UseSetting("NetBpm:Security:ApiKeyTenants:0", tenants.Length > 0 ? tenants[0] : string.Empty);

            for (var index = 1; index < tenants.Length; index++)
            {
                builder.UseSetting($"NetBpm:Security:ApiKeyTenants:{index}", tenants[index]);
            }
        });

    private static async Task<HttpResponseMessage> DeployAsync(HttpClient client, string? tenantId)
    {
        var path = tenantId is null
            ? "/api/designer/deploy"
            : $"/api/designer/deploy?tenantId={Uri.EscapeDataString(tenantId)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Content = new StringContent(ScopedProcess(), Encoding.UTF8, "text/xml");

        return await client.SendAsync(request, Ct);
    }

    private static async Task<string> StartAsync(HttpClient client, string? tenantId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

        request.Headers.Add("X-Api-Key", ApiKey);

        var payload = new Dictionary<string, object?> { ["processDefinitionKey"] = "scoped-single" };

        if (tenantId is not null)
        {
            payload["tenantId"] = tenantId;
        }

        request.Content = JsonContent.Create(payload);

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"启动失败：{response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("processInstanceId").GetString()!;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        GetAsync(client, path, tenantHeader: null);

    /// <summary>GET；<paramref name="tenantHeader"/> 非空时附带 <c>X-Tenant-Id</c>。</summary>
    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? tenantHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", ApiKey);

        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantHeader);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>发一个带作用域密钥的 JSON 请求；<paramref name="body"/> 为 <see langword="null"/> 时不带请求体。</summary>
    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body) =>
        SendJsonAsync(client, method, path, body, tenantHeader: null);

    /// <summary>发 JSON 请求；<paramref name="tenantHeader"/> 非空时附带 <c>X-Tenant-Id</c>。</summary>
    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        string? tenantHeader)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", ApiKey);

        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenantHeader);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>按实例查待办并返回唯一任务的 id。</summary>
    private static Task<string> SingleTaskIdAsync(HttpClient client, string instanceId) =>
        SingleTaskIdAsync(client, instanceId, tenantHeader: null);

    /// <summary>按实例查待办并返回唯一任务的 id；可按租户头收窄。</summary>
    private static async Task<string> SingleTaskIdAsync(
        HttpClient client,
        string instanceId,
        string? tenantHeader)
    {
        var tasks = await GetArrayAsync(
            client, $"/api/runtime/tasks?processInstanceId={instanceId}", tenantHeader);

        Assert.Single(tasks);

        return tasks[0].GetProperty("id").GetString()!;
    }

    private static Task<JsonElement[]> GetArrayAsync(HttpClient client, string path) =>
        GetArrayAsync(client, path, tenantHeader: null);

    private static async Task<JsonElement[]> GetArrayAsync(
        HttpClient client,
        string path,
        string? tenantHeader)
    {
        using var response = await GetAsync(client, path, tenantHeader);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"查询失败：{response.StatusCode} {body}");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray()];
    }

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static string ScopedProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="scoped-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" activiti:assignee="applicant" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;
}
