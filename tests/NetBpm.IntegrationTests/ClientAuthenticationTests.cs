using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 客户端凭据（<c>nbc.{clientId}.{token}</c>）端到端：注册 → 鉴权 → 租户钉死 → 代客声明 → 注销。
/// <para>
/// 全部跑在<b>内存模式</b>宿主（无数据库），因为这一层要验证的是「身份三元组是否被正确填充」，
/// 不是持久化；持久化由 <see cref="ClientRegistryDatabaseTests"/> 在真库上单独覆盖。
/// </para>
/// <para>
/// 这里刻意<b>不</b>配置 <c>NetBpm:Security:ApiKey</c>：宿主处于「未启用鉴权」的开放模式，
/// 用来验证一条关键规则——<b>带了形如客户端密钥的凭据就必须验过</b>，
/// 不能因为「没配置任何凭据」就静默降级成匿名（否则一把错误的密钥等于没带密钥）。
/// </para>
/// </summary>
public sealed class ClientAuthenticationTests
{
    private const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_ShouldIssueSecretOnce_AndNeverExposeItAfterwards()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        // 密钥自带客户端 id 与可识别前缀：鉴权时按主键查一行，不遍历全表
        Assert.StartsWith("nbc.", issued.Secret, StringComparison.Ordinal);
        Assert.Contains(issued.ClientId, issued.Secret, StringComparison.Ordinal);

        using var list = await client.GetAsync(new Uri("/api/admin/clients", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var body = await list.Content.ReadAsStringAsync(Ct);

        Assert.Contains(issued.ClientId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Secret, body, StringComparison.Ordinal);

        // 读接口连字段都不给：不是「置空」，而是没有这个字段
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement[0].TryGetProperty("clientSecret", out _));
    }

    [Fact]
    public async Task ClientSecret_ShouldAuthenticateAndPinTenant()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);

        using var response = await client.SendAsync(request, Ct);
        var me = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("client", me.GetProperty("authMode").GetString());
        Assert.Equal(issued.ClientId, me.GetProperty("caller").GetString());
        Assert.Equal("hr-system", me.GetProperty("callerName").GetString());
        Assert.Equal(DefaultTenant, me.GetProperty("tenant").GetString());

        // 纯服务令牌：没有真人主体，因此不得提交审批
        Assert.False(me.TryGetProperty("subject", out var subject) && subject.ValueKind == JsonValueKind.String);
        Assert.False(me.GetProperty("canSubmitApproval").GetBoolean());

        // 请求 id 在响应头与响应体里一致，排障时能一眼对上
        Assert.False(string.IsNullOrEmpty(me.GetProperty("requestId").GetString()));
        Assert.Equal(
            me.GetProperty("requestId").GetString(),
            response.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task ClientSecret_ShouldAlsoWorkAsBearerToken()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", issued.Secret);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("client", (await ReadJsonAsync(response)).GetProperty("authMode").GetString());
    }

    [Fact]
    public async Task BogusSecret_ShouldBeRejected_EvenWhenAuthIsNotConfigured()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        foreach (var bogus in new[] { "nbc.cl_0000000000000000.not-a-real-token", "nbc.malformed" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
            request.Headers.Add("X-Api-Key", bogus);

            using var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // 对照：完全不带凭据时，开放模式仍然放行（开发体验不能被这条规则误伤）
        using var anonymous = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public async Task TenantHeader_OutsideClientTenant_ShouldBeForbidden()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);
        request.Headers.Add("X-Tenant-Id", "tenant-other");

        using var response = await client.SendAsync(request, Ct);

        // 租户由客户端记录钉死：请求头不能「自证」属于别的租户
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantQueryParameter_CannotEscapeClientTenant()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/runtime/process-instances?tenantId=tenant-other", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);

        using var response = await client.SendAsync(request, Ct);

        // 按 id 访问有 TenantGuard 兜底，但「列表」的过滤条件完全由 RequestIdentity.Tenant 决定：
        // 不在这里拒绝，就等于允许 `?tenantId=别人` 跨租户读
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeclaringSubject_WithoutOnBehalfScope_ShouldBeForbidden()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:task:submit"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);
        request.Headers.Add("X-User-Id", "zhangsan");

        using var response = await client.SendAsync(request, Ct);

        // 允许任意客户端声明操作人 = 允许纯服务令牌冒充真人提交审批
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeclaringSubject_WithOnBehalfScope_ShouldBecomeOnBehalfOf()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(
            client,
            "oa-portal",
            ["workflow:task:submit", "workflow:task:submit:onbehalf"],
            confirmPrivilegedScopes: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);
        request.Headers.Add("X-Subject-Id", "zhangsan");

        using var response = await client.SendAsync(request, Ct);
        var me = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("zhangsan", me.GetProperty("subject").GetString());
        Assert.Equal(issued.ClientId, me.GetProperty("caller").GetString());

        // 有真人主体才允许提交审批（判据是「有没有人」，不是「有没有 scope」）
        Assert.True(me.GetProperty("canSubmitApproval").GetBoolean());
    }

    [Fact]
    public async Task PrivilegedScope_WithoutConfirmation_ShouldBeRejected()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        using var response = await RegisterAsyncRaw(
            client,
            new
            {
                name = "ops-bot",
                tenantId = DefaultTenant,
                scopes = new[] { "workflow:admin:client" },
            },
            confirmPrivilegedScopes: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("client.scope_needs_confirmation", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ClientWithoutAdminScope_ShouldNotManageClients()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/admin/clients", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);

        using var response = await client.SendAsync(request, Ct);

        // 旧 API Key 是「一个密钥代表整个可信内网」的历史产物；客户端是「一个业务系统」，必须逐条命中
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // 对照：拿到 workflow:admin:client 才能管客户端
        var admin = await RegisterAsync(
            client,
            "platform-console",
            ["workflow:admin:client"],
            confirmPrivilegedScopes: true);

        using var adminRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/admin/clients", UriKind.Relative));
        adminRequest.Headers.Add("X-Api-Key", admin.Secret);

        using var allowed = await client.SendAsync(adminRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task RevokedClient_ShouldBeRejectedImmediately()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using (var revoked = await client.DeleteAsync(
            new Uri($"/api/admin/clients/{issued.ClientId}", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SuspendedClient_ShouldBeRejected_ButRecoverable()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using (var suspended = await SendPatchAsync(
            client,
            $"/api/admin/clients/{issued.ClientId}",
            new { status = "SUSPENDED" }))
        {
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        }

        using (var denied = await SendWithSecretAsync(client, issued.Secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // 暂停不是注销：密钥还在，恢复后立即可用（临时止血的开关）
        using (var activated = await SendPatchAsync(
            client,
            $"/api/admin/clients/{issued.ClientId}",
            new { status = "ACTIVE" }))
        {
            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        }

        using (var allowed = await SendWithSecretAsync(client, issued.Secret))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
    }

    [Fact]
    public async Task RotatedSecret_ShouldReplaceOldOne_AfterGracePeriod()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var rotate = await client.PostAsync(
            new Uri($"/api/admin/clients/{issued.ClientId}/rotate", UriKind.Relative),
            content: null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);

        var rotated = await ReadJsonAsync(rotate);
        var newSecret = rotated.GetProperty("clientSecret").GetString();

        Assert.NotNull(newSecret);
        Assert.NotEqual(issued.Secret, newSecret);

        // 宽限期内新旧两把都可用（这是给调用方灰度改配置的窗口）
        using (var oldOne = await SendWithSecretAsync(client, issued.Secret))
        {
            Assert.Equal(HttpStatusCode.OK, oldOne.StatusCode);
        }

        using (var newOne = await SendWithSecretAsync(client, newSecret!))
        {
            Assert.Equal(HttpStatusCode.OK, newOne.StatusCode);
        }
    }

    [Fact]
    public async Task RequireCredential_ShouldCloseAnonymousAccess_WithoutLegacyKey()
    {
        using var host = BuildHost(requireCredential: true);
        using var client = host.CreateClient();

        // 只注册了客户端、没配旧 API Key 时，匿名请求默认仍会放行；
        // 打开 RequireCredential 才是「必须带凭据」——这是运维最容易漏的一步
        using (var me = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        // 匿名白名单不受影响：登录入口与运维信息必须能读，否则连登录都做不到
        using (var info = await client.GetAsync(new Uri("/api/admin/info", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        }
    }

    [Fact]
    public async Task TenantEndpoints_ShouldBootstrapAndGuardSelfSuspend()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        using (var tenants = await client.GetAsync(new Uri("/api/admin/tenants", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);

            var list = await ReadJsonAsync(tenants);

            // 启动引导必须已经建好默认租户，否则「注册客户端」会先撞上「租户不存在」
            Assert.Contains(
                list.EnumerateArray(),
                tenant => tenant.GetProperty("tenantId").GetString() == DefaultTenant);
        }

        var issued = await RegisterAsync(client, "hr-system", ["workflow:instance:read"]);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/api/admin/tenants/{DefaultTenant}/status", UriKind.Relative));
        request.Headers.Add("X-Api-Key", issued.Secret);
        request.Content = JsonContent.Create(new { active = false });

        using var response = await client.SendAsync(request, Ct);

        // 客户端没有 workflow:admin:client → 403（先撞授权，还没轮到「自停用」这条校验）
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// 起一个<b>内存模式</b>宿主：无数据库、无后台作业、关闭限流（整类测试共享同一 IP 分区）。
    /// </summary>
    /// <param name="requireCredential">
    /// 是否要求必须带凭据。默认 <see langword="false"/>，与生产默认一致（保持既有部署的开放模式行为）。
    /// </param>
    private static WebApplicationFactory<Program> BuildHost(bool requireCredential = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "NetBpm:Security:RequireCredential",
                requireCredential ? "true" : "false");

            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");

            // 默认租户名固定，便于断言（真实部署用 NetBpm:Security:Tenants:Default* 覆盖）
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    private static async Task<IssuedClient> RegisterAsync(
        HttpClient client,
        string name,
        string[] scopes,
        bool confirmPrivilegedScopes = false)
    {
        using var response = await RegisterAsyncRaw(
            client,
            new { name, tenantId = DefaultTenant, scopes },
            confirmPrivilegedScopes);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        return new IssuedClient(
            root.GetProperty("clientId").GetString()!,
            root.GetProperty("clientSecret").GetString()!);
    }

    private static Task<HttpResponseMessage> RegisterAsyncRaw(
        HttpClient client,
        object body,
        bool confirmPrivilegedScopes)
    {
        var path = confirmPrivilegedScopes
            ? "/api/admin/clients?confirmPrivilegedScopes=true"
            : "/api/admin/clients";

        return client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Ct);
    }

    private static Task<HttpResponseMessage> SendWithSecretAsync(HttpClient client, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static async Task<HttpResponseMessage> SendPatchAsync(
        HttpClient client,
        string path,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        using var json = JsonDocument.Parse(body);

        return json.RootElement.Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        using var json = JsonDocument.Parse(body);

        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record IssuedClient(string ClientId, string Secret);
}
