using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Events;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理面只读观测（<c>/admin/v1</c> 的字典 / 审计 / 订阅 / 死信 / 配额）的守门用例。
/// </summary>
/// <remarks>
/// <para>
/// 这五个端点的风险不是「算错」，而是<b>漏出去</b>：
/// ① 订阅视图里带明文密钥（等价于把「能伪造 NetBpm 通知」的凭据摊在列表页上）；
/// ② 死信把非死信行也列出来（管理端以为那几条挂了，去重投，结果把正常的也重发一遍）；
/// ③ 配额只回显一个层级（「租户 600、客户端 120」只看一层就会误判谁被限住了）。
/// 用例逐条钉住这三件事，另加 scope 与「缺 processKey」两条边界。
/// </para>
/// <para>
/// 内存宿主：这五个面读的都是平台表，内存版与 Dapper 版的差异只在 SQL；
/// 真库上的分页 SQL 由 <c>EventDeliveryDatabaseTests</c> 与审计相关用例覆盖。
/// </para>
/// </remarks>
public sealed class AdminReadEndpointsTests
{
    private static readonly string ProcessKey = ReadEndpointHarness.ProcessKey;

    private static string[] AdminScopes =>
    [
        PlatformScopes.AdminProcess,
        PlatformScopes.AdminClient,
        PlatformScopes.EventSubscribe,
        PlatformScopes.ProcessRead,
    ];

    [Fact]
    public async Task Dictionaries_ShouldRequireProcessKey()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await ReadEndpointHarness.GetAsync(client, "/admin/v1/dictionaries", secret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dictionaries_ShouldListRowsOfTheProcess()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        SeedDictionary(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/admin/v1/dictionaries?processKey={ProcessKey}",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(1, rows.GetArrayLength());

        var row = rows[0];

        Assert.Equal(ProcessKey, row.GetProperty("processKey").GetString());
        Assert.Equal("1", row.GetProperty("optionId").GetString());
        Assert.Equal("APPROVE", row.GetProperty("semantic").GetString());
        Assert.Equal("POSITIVE", row.GetProperty("outcomeClass").GetString());

        // 节点级映射要能看出「这条只对某个节点生效」
        Assert.Equal("task_approve", row.GetProperty("nodeId").GetString());
        Assert.True(row.GetProperty("requiresComment").GetBoolean());
    }

    [Fact]
    public async Task Audit_ShouldPageAndExposeFacets()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        // 用真实写路径产生审计，而不是直接往 store 里塞记录
        foreach (var name in new[] { "第一次精修", "第二次精修" })
        {
            using var put = await PutOverrideAsync(client, secret, new { name });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using var page = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/audit?action=process_catalog.upsert&max=1",
            secret);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(page);

        // total 是「满足条件的总数」，不是「本页条数」——合规核对要用前者
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("max").GetInt32());
        Assert.Equal(1, body.GetProperty("items").GetArrayLength());

        var entry = body.GetProperty("items")[0];

        Assert.Equal("process_catalog.upsert", entry.GetProperty("action").GetString());
        Assert.Equal("process_catalog", entry.GetProperty("targetType").GetString());
        Assert.Equal(ProcessKey, entry.GetProperty("targetId").GetString());

        // 明细里只记字段名，不记值（页面地址可能带内部域名）
        Assert.Contains("Name", entry.GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var facets = await ReadEndpointHarness.GetAsync(client, "/admin/v1/audit/facets", secret);

        Assert.Equal(HttpStatusCode.OK, facets.StatusCode);

        var facetBody = await ReadEndpointHarness.ReadJsonAsync(facets);

        Assert.Contains(
            "process_catalog.upsert",
            facetBody.GetProperty("actions").EnumerateArray().Select(item => item.GetString()));

        var counts = facetBody.GetProperty("counts").EnumerateArray().ToList();

        Assert.Contains(counts, item => item.GetProperty("count").GetInt32() == 2);
    }

    [Fact]
    public async Task Audit_ShouldRejectUnparseableTimeWindow()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/audit?since=昨天",
            secret);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        // 静默忽略一个解析不了的时间条件，会让调用方以为「筛过了」
        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeadLetters_ShouldListOnlyDeadLetterRows()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        var deliveries = host.Services.GetRequiredService<IEventDeliveryStore>();

        var now = DateTime.UtcNow;

        // 挂掉的：一次失败就到上限
        await deliveries.GetOrCreateAsync("evt-dead", "sub-1", "default");
        await deliveries.MarkFailureAsync("evt-dead", "sub-1", "500 网关错误", now, null, maxAttempts: 1);

        // 正常的：还在退避重试中，不该出现在死信列表里
        await deliveries.GetOrCreateAsync("evt-pending", "sub-1", "default");
        await deliveries.MarkFailureAsync("evt-pending", "sub-1", "超时", now, now.AddSeconds(5), maxAttempts: 5);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "event-ops",
            [PlatformScopes.EventSubscribe]);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/events/deadletters",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(1, body.GetProperty("total").GetInt32());

        var items = body.GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("evt-dead", items[0].GetProperty("eventId").GetString());
        Assert.Equal("DEAD_LETTER", items[0].GetProperty("status").GetString());
        Assert.Equal("500 网关错误", items[0].GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Subscriptions_ShouldHidePlainSecret()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        var store = host.Services.GetRequiredService<IEventSubscriptionStore>();

        var created = await store.CreateAsync(new EventSubscription
        {
            TenantId = "default",
            Name = "ERP 回调",
            EndpointUrl = "https://erp.example.com/hooks/netbpm",
            EventTypes = ["instance.completed"],
        });

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "event-ops",
            [PlatformScopes.EventSubscribe]);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/subscriptions",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(1, rows.GetArrayLength());

        var row = rows[0];

        Assert.Equal("ERP 回调", row.GetProperty("name").GetString());

        // 🔑 列表页摊开明文密钥是最常见的泄漏方式：只能出现掩码 + 后 4 位
        Assert.False(row.TryGetProperty("secret", out _), "订阅列表不能返回明文密钥。");

        var preview = row.GetProperty("secretPreview").GetString()!;

        Assert.StartsWith("****", preview, StringComparison.Ordinal);
        Assert.NotEqual(created.Secret, preview);
        Assert.True(
            preview.Length < created.Secret.Length,
            "预览必须比明文短——否则「预览」就是明文。");
    }

    [Fact]
    public async Task Quotas_ShouldExposeTenantAndClientLevels()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 租户默认 600（BuildHost 的默认登记值），这个客户端单独配成 120
        await RegisterClientAsync(client, "quota-client", quotaPerMinute: 120);

        var secret = await RegisterClientAsync(client, "quota-reader", quotaPerMinute: null);

        using var response = await ReadEndpointHarness.GetAsync(client, "/admin/v1/quotas", secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        var tenants = body.GetProperty("tenants").EnumerateArray().ToList();

        Assert.Contains(tenants, tenant => tenant.GetProperty("tenantId").GetString() == "default");

        var defaultTenant = tenants.Single(tenant => tenant.GetProperty("tenantId").GetString() == "default");

        Assert.Equal(600, defaultTenant.GetProperty("quotaPerMinute").GetInt32());
        Assert.True(
            defaultTenant.GetProperty("clientCount").GetInt32() >= 2,
            "clientCount 要统计该租户下的客户端数。");

        var clients = body.GetProperty("clients").EnumerateArray().ToList();

        var overridden = clients.Single(item => item.GetProperty("name").GetString() == "quota-client");

        Assert.Equal(120, overridden.GetProperty("quotaPerMinute").GetInt32());

        // 没单独配的客户端继承租户默认值（注册时就写进这一行）
        var inherited = clients.Single(item => item.GetProperty("name").GetString() == "quota-reader");

        Assert.Equal(600, inherited.GetProperty("quotaPerMinute").GetInt32());

        // 限流在测试宿主里是关的：管理端必须能区分「配额声明值」与「真会 429」
        Assert.False(body.GetProperty("rateLimitEnabled").GetBoolean());
    }

    [Fact]
    public async Task ReadEndpoints_ShouldRequireTheirOwnScopes()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只有业务读 scope：能看目录与实例，看不了管理面
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.ProcessRead]);

        foreach (var (path, scope) in new[]
        {
            ($"/admin/v1/dictionaries?processKey={ProcessKey}", PlatformScopes.AdminProcess),
            ("/admin/v1/audit", PlatformScopes.AdminClient),
            ("/admin/v1/subscriptions", PlatformScopes.EventSubscribe),
            ("/admin/v1/events/deadletters", PlatformScopes.EventSubscribe),
            ("/admin/v1/quotas", PlatformScopes.AdminClient),
        })
        {
            using var response = await ReadEndpointHarness.GetAsync(client, path, secret);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            var problem = await ReadEndpointHarness.ReadJsonAsync(response);

            Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());

            // 错误体要指出缺的是哪个 scope，否则接入方只能猜
            Assert.Contains(
                scope,
                problem.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
        }
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static void SeedDictionary(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var store = (InMemoryApprovalDictionaryStore)host.Services
            .GetRequiredService<IApprovalDictionaryStore>();

        store.Upsert(new ApprovalDictionaryEntry(
            "default",
            ProcessKey,
            "task_approve",
            "1",
            ApprovalSemantic.Approve,
            ApprovalOutcomeClass.Positive,
            "同意",
            RequiresComment: true,
            RequiresTargetNode: false,
            AllowedTargetNodes: null,
            Attributes: null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
    }

    private static async Task<string> RegisterClientAsync(
        HttpClient client,
        string name,
        int? quotaPerMinute)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name,
                tenantId = ReadEndpointHarness.DefaultTenant,
                scopes = AdminScopes,
                quotaPerMinute,
            },
            ReadEndpointHarness.Ct);

        var body = await response.Content.ReadAsStringAsync(ReadEndpointHarness.Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> PutOverrideAsync(HttpClient client, string secret, object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/admin/v1/processes/{ProcessKey}", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }
}
