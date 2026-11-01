using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Events;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 事件订阅管理端点（M13）：<c>/api/admin/event-subscriptions</c>。
/// </summary>
/// <remarks>
/// 锁住三件事，其中第一条是安全红线：
/// <list type="number">
///   <item><description><b>明文密钥只出现一次</b>：创建 / 轮换的响应里有，读接口只有后 4 位 ——
///     订阅密钥等价于「能伪造 NetBpm 通知」的凭据；</description></item>
///   <item><description><b>换密钥有独立入口</b>：PATCH 不接受 secret，
///     避免「改个地址结果把密钥也换了」这类无声事故；</description></item>
///   <item><description><b>死信投递能重投</b>：订阅方修好后不必等新事件，
///     且非死信状态重投要 409（不是静默成功）。</description></item>
/// </list>
/// </remarks>
public sealed class EventSubscriptionEndpointTests
{
    private const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_ShouldReturnPlainSecretOnce_AndHideItAfterwards()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-create", [PlatformScopes.EventSubscribe]);

        using var created = await client.SendAsync(
            Json(HttpMethod.Post, "/api/admin/event-subscriptions", secret, new
            {
                name = "ERP 回调",
                endpointUrl = "https://erp.test/hook",
                eventTypes = new[] { "TASK_CREATED" },
            }), Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var subscriptionId = createdJson.RootElement
            .GetProperty("subscription").GetProperty("id").GetString()!;
        var plainSecret = createdJson.RootElement.GetProperty("secret").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(plainSecret));

        // 读接口：只有 secretPreview，明文绝不出现
        using var read = await client.SendAsync(
            Get($"/api/admin/event-subscriptions/{subscriptionId}", secret), Ct);

        var readBody = await read.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain(plainSecret, readBody, StringComparison.Ordinal);

        using var readJson = JsonDocument.Parse(readBody);

        Assert.EndsWith(plainSecret[^4..], readJson.RootElement.GetProperty("secretPreview").GetString()!, StringComparison.Ordinal);

        // 列表同理：不能因为「一次列了很多条」就把密钥摊开
        using var list = await client.SendAsync(Get("/api/admin/event-subscriptions", secret), Ct);
        var listBody = await list.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain(plainSecret, listBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ShouldRejectBadEndpointUrl()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-update", [PlatformScopes.EventSubscribe]);
        var subscriptionId = await CreateAsync(client, secret, "归档服务", "https://archive.test/hook");

        using var response = await client.SendAsync(
            Json(
                new HttpMethod("PATCH"),
                $"/api/admin/event-subscriptions/{subscriptionId}",
                secret,
                new { endpointUrl = "ftp://nope" }),
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rotate_ShouldIssueNewSecret()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-rotate", [PlatformScopes.EventSubscribe]);
        var subscriptionId = await CreateAsync(client, secret, "归档服务", "https://archive.test/hook");

        var store = host.Services.GetRequiredService<IEventSubscriptionStore>();
        var before = (await store.FindByIdAsync(subscriptionId, Ct))!.Secret;

        using var response = await client.SendAsync(
            Json(HttpMethod.Post, $"/api/admin/event-subscriptions/{subscriptionId}/rotate", secret, new { }), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var rotated = json.RootElement.GetProperty("secret").GetString()!;

        Assert.NotEqual(before, rotated);
        Assert.Equal(rotated, (await store.FindByIdAsync(subscriptionId, Ct))!.Secret);
    }

    [Fact]
    public async Task RetryDelivery_Should409_WhenNotDeadLetter()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-retry", [PlatformScopes.EventSubscribe]);
        var subscriptionId = await CreateAsync(client, secret, "归档服务", "https://archive.test/hook");

        var deliveries = host.Services.GetRequiredService<IEventDeliveryStore>();

        await deliveries.GetOrCreateAsync("evt-1", subscriptionId, DefaultTenant, Ct);

        using var notDead = await client.SendAsync(
            Json(
                HttpMethod.Post,
                $"/api/admin/events/evt-1/deliveries/{subscriptionId}/retry",
                secret,
                new { }),
            Ct);

        Assert.Equal(HttpStatusCode.Conflict, notDead.StatusCode);

        // 进死信之后再重投就该放行
        await deliveries.MarkFailureAsync(
            "evt-1",
            subscriptionId,
            "订阅方返回 HTTP 503。",
            DateTime.UtcNow,
            null,
            1,
            Ct);

        using var dead = await client.SendAsync(
            Json(
                HttpMethod.Post,
                $"/api/admin/events/evt-1/deliveries/{subscriptionId}/retry",
                secret,
                new { }),
            Ct);

        Assert.Equal(HttpStatusCode.NoContent, dead.StatusCode);

        var delivery = await deliveries.FindAsync("evt-1", subscriptionId, Ct);

        Assert.Equal(EventDeliveryStatus.Pending, delivery!.Status);
        Assert.Equal(0, delivery.Attempts);
    }

    [Fact]
    public async Task Subscriptions_ShouldRequireScope()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-noscope", [PlatformScopes.ProcessRead]);

        using var response = await client.SendAsync(Get("/api/admin/event-subscriptions", secret), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

    [Fact]
    public async Task MaxPerMinute_ShouldRoundTrip_AndBeClearableWithZero()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "sub-throttle", [PlatformScopes.EventSubscribe]);

        using var created = await client.SendAsync(
            Json(HttpMethod.Post, "/api/admin/event-subscriptions", secret, new
            {
                name = "限速的订阅",
                endpointUrl = "https://slow.test/hook",
                maxPerMinute = 120,
            }),
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));

        var subscriptionId = createdJson.RootElement
            .GetProperty("subscription").GetProperty("id").GetString()!;

        Assert.Equal(
            120,
            createdJson.RootElement.GetProperty("subscription").GetProperty("maxPerMinute").GetInt32());

        // 显式传 0 = 取消限速：判 null 而不是判 > 0，否则配错之后就再也改不回来
        using var cleared = await client.SendAsync(
            Json(
                new HttpMethod("PATCH"),
                $"/api/admin/event-subscriptions/{subscriptionId}",
                secret,
                new { maxPerMinute = 0 }),
            Ct);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using var clearedJson = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync(Ct));

        Assert.Equal(0, clearedJson.RootElement.GetProperty("maxPerMinute").GetInt32());
    }

    private static async Task<string> CreateAsync(
        HttpClient client,
        string secret,
        string name,
        string endpointUrl)
    {
        using var response = await client.SendAsync(
            Json(
                HttpMethod.Post,
                "/api/admin/event-subscriptions",
                secret,
                new { name, endpointUrl }),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        return json.RootElement.GetProperty("subscription").GetProperty("id").GetString()!;
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

    private static HttpRequestMessage Get(string path, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return request;
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, string secret, object body)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Content = JsonContent.Create(body);

        return request;
    }
}
