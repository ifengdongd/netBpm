using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 限流按<b>调用方</b>分区（clientId），不是按 IP。
/// </summary>
/// <remarks>
/// <para>
/// 这是「配额字段入库但没生效」的收口用例：<c>nb_api_client.quota_per_minute</c> 在注册时写下，
/// 这里断言它<b>真的</b>成了该客户端的每分钟上限，并且<b>只</b>影响那一个客户端。
/// </para>
/// <para>
/// 两个客户端在测试里必然同 IP（都走 TestServer），所以「另一个客户端不受影响」
/// 这条断言恰好也是「分区键不是 IP」的证明——按 IP 分的话它们会共用一个桶。
/// </para>
/// </remarks>
public sealed class RateLimitByClientTests
{
    private const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientExceedingQuota_ShouldGet429_WithRetryAfter_AndOtherClientsUnaffected()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        // 配额 2：第三次必须被拒
        var throttled = await RegisterAsync(client, "batch-job", quotaPerMinute: 2);
        var healthy = await RegisterAsync(client, "hr-system", quotaPerMinute: 60);

        var first = await ProbeAsync(client, throttled);
        var second = await ProbeAsync(client, throttled);
        var third = await ProbeAsync(client, throttled);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        // 配额是客户端自己的（2），不是全局默认（600）——这证明 quota_per_minute 已生效
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("1", first.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.Equal("0", third.Headers.GetValues("RateLimit-Remaining").Single());

        // 429 必须带 Retry-After：没有它，客户端只能猜「等多久」或立刻重试，把窗口踩得更死
        var retryAfter = third.Headers.GetValues("Retry-After").Single();
        Assert.InRange(int.Parse(retryAfter, System.Globalization.CultureInfo.InvariantCulture), 1, 60);

        var problem = await ReadJsonAsync(third);
        Assert.Equal("rate_limited", problem.GetProperty("code").GetString());

        // 另一个客户端同 IP、同宿主：完全不受影响
        var other = await ProbeAsync(client, healthy);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal("60", other.Headers.GetValues("RateLimit-Limit").Single());
    }

    [Fact]
    public void QuotaShouldReset_InTheNextWindow()
    {
        // 用可控时钟直接验证计数器本身：等到真实窗口过去要一分钟，集成用例里不可接受
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var limiter = new NetBpm.Rest.RateLimiting.FixedWindowRateLimiter(1024, clock);

        var first = limiter.Acquire("client:cl_test", permitLimit: 1);
        var second = limiter.Acquire("client:cl_test", permitLimit: 1);

        Assert.True(first.Allowed);
        Assert.Equal(0, first.Remaining);
        Assert.False(second.Allowed);

        // 跨过窗口边界：计数从头开始，RetryAfter 归零方向
        clock.Advance(TimeSpan.FromSeconds(61));

        var afterReset = limiter.Acquire("client:cl_test", permitLimit: 1);

        Assert.True(afterReset.Allowed);
        Assert.Equal(0, afterReset.Remaining);
    }

    [Fact]
    public void UnlimitedPartition_ShouldNotConsumeOrEmitHeaders()
    {
        var limiter = new NetBpm.Rest.RateLimiting.FixedWindowRateLimiter(16);

        // permitLimit <= 0 表示「不限流」：用于关闭限流或给特定调用方开口子
        for (var index = 0; index < 100; index++)
        {
            var decision = limiter.Acquire("client:cl_unlimited", permitLimit: 0);

            Assert.True(decision.Allowed);
            Assert.False(decision.Counted);
        }

        Assert.Equal(0, limiter.PartitionCount);
    }

    [Fact]
    public void PartitionLimit_ShouldFailOpen_InsteadOfTakingTheWholeServiceDown()
    {
        var limiter = new NetBpm.Rest.RateLimiting.FixedWindowRateLimiter(maxPartitions: 2);

        Assert.True(limiter.Acquire("ip:1", 10).Allowed);
        Assert.True(limiter.Acquire("ip:2", 10).Allowed);

        // 第 3 个分区：分区表已满 → 放行但不计数。
        // 这里刻意 fail-open：分区键可以被海量 IP 撑爆，满表时 fail-closed 等于用内存保护
        // 把全体正常调用方一起打挂。
        var overflow = limiter.Acquire("ip:3", 10);

        Assert.True(overflow.Allowed);
        Assert.False(overflow.Counted);
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

            // 只留客户端分区的差异可见：其余档位给足，避免用例被无关限额干扰
            builder.UseSetting("NetBpm:RateLimit:Enabled", "true");
            builder.UseSetting("NetBpm:RateLimit:ApiPerMinute", "600");
            builder.UseSetting("NetBpm:RateLimit:ProbePerMinute", "600");
            builder.UseSetting("NetBpm:RateLimit:LoginPerMinute", "600");
            builder.UseSetting("NetBpm:RateLimit:ClientPerMinute", "600");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string name,
        int quotaPerMinute)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients", UriKind.Relative),
            new
            {
                name,
                tenantId = DefaultTenant,
                scopes = new[] { "workflow:process:read" },
                quotaPerMinute,
            },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    /// <summary>凭据探针：一次轻量调用，用来消耗配额。</summary>
    private static Task<HttpResponseMessage> ProbeAsync(HttpClient client, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>可推进的时钟（限流窗口按分钟，测试里不能真的等）。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
