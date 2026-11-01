using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理面接入管理（<c>/admin/v1/tenants</c>、<c>/admin/v1/clients</c>）的守门用例。
/// </summary>
/// <remarks>
/// <para>
/// 这组端点是旧 <c>/api/admin</c> 同面能力的平台面形态，守的是三类属性：
/// ① <b>明文密钥只出现一次</b>（读接口永远看不到 secret）；
/// ② <b>高危 scope 必须显式确认</b>（不能跟着一串业务 scope 顺带发出去）；
/// ③ <b>租户边界</b>（跨租户读按不存在 404、跨租户写按禁止 403）。
/// </para>
/// <para>
/// 另有一条与旧面不同、值得钉住的行为：注册表异常被翻成 RFC 9457 的具体状态码
/// （租户不存在 404、scope 未定义 / 高危未确认 422），而不是笼统的 400——
/// 客户端要能按 <c>code</c> 分支，而不是去 match 中文文案。
/// </para>
/// </remarks>
public sealed class AdminClientEndpointTests
{
    /// <summary>
    /// 运维角色：接入管理 + 审计读 + 流程读。
    /// <para>
    /// 审计读是<b>独立 scope</b>（<c>workflow:admin:audit:read</c>），不再由 <c>workflow:admin:client</c> 自动覆盖 ——
    /// 能发密钥不等于该看合规流水。用例里要读审计就必须显式带上它。
    /// </para>
    /// </summary>
    private static string[] AdminScopes =>
        [PlatformScopes.AdminClient, PlatformScopes.AdminAuditRead, PlatformScopes.ProcessRead];

    [Fact]
    public async Task Register_ShouldIssueSecretOnce_AndNeverReturnItAgain()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        using var created = await RegisterClientAsync(
            client,
            admin,
            new { name = "erp", tenantId = ReadEndpointHarness.DefaultTenant, scopes = new[] { PlatformScopes.ProcessRead } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // 201 + Location：调用方据此知道「已存在」时该去打哪个地址
        Assert.NotNull(created.Headers.Location);

        var issued = await ReadEndpointHarness.ReadJsonAsync(created);

        var clientId = issued.GetProperty("clientId").GetString()!;
        var secret = issued.GetProperty("clientSecret").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(secret), "注册响应必须带明文密钥（只此一次）。");
        Assert.Equal($"/admin/v1/clients/{clientId}", created.Headers.Location!.ToString());

        using var found = await ReadEndpointHarness.GetAsync(client, $"/admin/v1/clients/{clientId}", admin);

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        var descriptor = await ReadEndpointHarness.ReadJsonAsync(found);

        Assert.Equal("erp", descriptor.GetProperty("name").GetString());

        // 🔑 读接口不能出现密钥（明文或摘要都不行）
        Assert.False(descriptor.TryGetProperty("clientSecret", out _), "客户端详情不能返回密钥。");
        Assert.False(descriptor.TryGetProperty("secretHash", out _), "客户端详情不能返回密钥摘要。");
    }

    [Fact]
    public async Task Register_Should422_WhenPrivilegedScopeNotConfirmed()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        // 不带 confirmPrivilegedScopes：管理类 scope 属于高危，必须单独确认
        using var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/clients", UriKind.Relative),
            new
            {
                name = "sneaky",
                tenantId = ReadEndpointHarness.DefaultTenant,
                scopes = new[] { PlatformScopes.AdminClient },
            },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ClientScopeNeedsConfirmation, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_Should422_WhenScopeUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        using var response = await RegisterClientAsync(
            client,
            admin,
            new { name = "typo", tenantId = ReadEndpointHarness.DefaultTenant, scopes = new[] { "workflow:nope" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.ClientScopeUnknown, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_Should404_WhenTenantNotRegistered()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 不带凭据（测试宿主关掉了强制鉴权）：不受租户约束，才走得到「租户不存在」这一条
        using var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name = "ghost",
                tenantId = "ghost-" + Guid.NewGuid().ToString("N")[..8],
                scopes = new[] { PlatformScopes.ProcessRead },
            },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        // 旧面把这类错误统一成 400，客户端只能靠中文文案猜
        Assert.Equal(ErrorCodes.TenantNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_ShouldUpdateAndAudit()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        var clientId = await CreateClientAsync(client, admin, "erp");

        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            new Uri($"/admin/v1/clients/{clientId}", UriKind.Relative))
        {
            Content = JsonContent.Create(new { name = "erp-v2", quotaPerMinute = 300 }),
        };

        request.Headers.Add("X-Api-Key", admin);

        using var response = await client.SendAsync(request, ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal("erp-v2", updated.GetProperty("name").GetString());
        Assert.Equal(300, updated.GetProperty("quotaPerMinute").GetInt32());

        using var audit = await ReadEndpointHarness.GetAsync(client, "/admin/v1/audit?action=client.update", admin);

        var page = await ReadEndpointHarness.ReadJsonAsync(audit);

        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(clientId, page.GetProperty("items")[0].GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task Rotate_ShouldKeepPreviousSecretInGraceWindow()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        var clientId = await CreateClientAsync(client, admin, "erp");

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/admin/v1/clients/{clientId}/rotate", UriKind.Relative));

        request.Headers.Add("X-Api-Key", admin);

        using var response = await client.SendAsync(request, ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var issued = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.False(string.IsNullOrWhiteSpace(issued.GetProperty("clientSecret").GetString()));

        // 宽限期是「轮换不打断裂已上线对接」的唯一保障，必须回显
        Assert.NotEqual(JsonValueKind.Null, issued.GetProperty("previousSecretValidUntil").ValueKind);
    }

    [Fact]
    public async Task Revoke_ShouldBeIdempotent_AndMakeSecretUnusable()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        var clientId = await CreateClientAsync(client, admin, "erp");

        using var revoked = await DeleteAsync(client, admin, clientId);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // DELETE 是幂等的：同一个请求重放还是 204（客户端不需要为「重试」准备特殊分支）
        using var again = await DeleteAsync(client, admin, clientId);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        using var found = await ReadEndpointHarness.GetAsync(client, $"/admin/v1/clients/{clientId}", admin);

        var descriptor = await ReadEndpointHarness.ReadJsonAsync(found);

        Assert.Equal("REVOKED", descriptor.GetProperty("status").GetString());

        // 不存在的 id 才是 404
        using var missing = await DeleteAsync(client, admin, "cl_not_exists");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(missing);

        Assert.Equal(ErrorCodes.ClientNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Tenants_ShouldBeIdempotentAndNotOverwrite()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 登记租户是「平台级」动作：绑定了租户的凭据不能开别人的租户（见跨租户写用例），
        // 这里用不受租户约束的 dev 身份
        using var first = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/acme", UriKind.Relative),
            new { name = "Acme 集团", quotaPerMinute = 300 },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 二次登记不覆盖：启动引导每次都会调它，覆盖写会把运维调过的值打回默认
        using var second = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/acme", UriKind.Relative),
            new { name = "改个名", quotaPerMinute = 60 },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var tenant = await ReadEndpointHarness.ReadJsonAsync(second);

        Assert.Equal("Acme 集团", tenant.GetProperty("name").GetString());
        Assert.Equal(300, tenant.GetProperty("quotaPerMinute").GetInt32());
        Assert.True(tenant.GetProperty("active").GetBoolean());

        // 缺少 name 是 400：租户名要进审计与运维看板
        using var nameless = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/acme2", UriKind.Relative),
            new { },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(nameless);

        Assert.Equal(ErrorCodes.TenantNameRequired, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TenantStatus_ShouldRejectSelfSuspend()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 这把凭据绑定 default 租户
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        using var response = await PutTenantStatusAsync(client, admin, ReadEndpointHarness.DefaultTenant, active: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        // 停用后本次调用的凭据立即失效，连「改回来」都做不到
        Assert.Equal(ErrorCodes.TenantSelfSuspend, problem.GetProperty("code").GetString());

        // 启用是允许的（可恢复方向）
        using var activate = await PutTenantStatusAsync(client, admin, ReadEndpointHarness.DefaultTenant, active: true);

        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
    }

    [Fact]
    public async Task CrossTenantWrite_Should403()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        // 写操作给 403 而不是 404：目标租户就在请求里，调用方本来就知道它存在
        using var response = await PutTenantAsync(client, admin, "other", new { name = "别家的租户" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.TenantForbidden, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CrossTenantRead_ShouldNotLeakOtherTenantsClients()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 无凭据的 dev 身份不受租户约束：用它造出「别家租户 + 别家客户端」
        using var otherTenant = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/other", UriKind.Relative),
            new { name = "别家租户" },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);

        using var otherClient = await client.PostAsJsonAsync(
            new Uri("/admin/v1/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name = "other-erp",
                tenantId = "other",
                scopes = new[] { PlatformScopes.ProcessRead },
            },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.Created, otherClient.StatusCode);

        var otherId = (await ReadEndpointHarness.ReadJsonAsync(otherClient)).GetProperty("clientId").GetString()!;

        // 这把凭据绑定 default 租户
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        // 显式指定别家租户：403（参数就在那儿，装作看不见没有意义）
        using var crossed = await ReadEndpointHarness.GetAsync(client, "/admin/v1/clients?tenantId=other", admin);

        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);

        // 不指定时列表自动收窄到本租户，别家的客户端不能出现
        using var list = await ReadEndpointHarness.GetAsync(client, "/admin/v1/clients", admin);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var ids = (await ReadEndpointHarness.ReadJsonAsync(list))
            .EnumerateArray()
            .Select(item => item.GetProperty("clientId").GetString())
            .ToArray();

        Assert.DoesNotContain(otherId, ids);

        // 直接按 id 查别家的客户端：按不存在处理（不泄露存在性）
        using var found = await ReadEndpointHarness.GetAsync(client, $"/admin/v1/clients/{otherId}", admin);

        Assert.Equal(HttpStatusCode.NotFound, found.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(found);

        Assert.Equal(ErrorCodes.ClientNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Writes_ShouldRequireAdminClientScope()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只有业务读 scope：看不了、也发不了密钥
        var secret = await ReadEndpointHarness.RegisterAsync(client, "portal", [PlatformScopes.ProcessRead]);

        using var response = await RegisterClientAsync(
            client,
            secret,
            new { name = "erp", tenantId = ReadEndpointHarness.DefaultTenant, scopes = new[] { PlatformScopes.ProcessRead } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
        Assert.Contains(
            PlatformScopes.AdminClient,
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static Task<HttpResponseMessage> RegisterClientAsync(
        HttpClient client,
        string secret,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/admin/v1/clients?confirmPrivilegedScopes=true", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static async Task<string> CreateClientAsync(HttpClient client, string secret, string name)
    {
        using var response = await RegisterClientAsync(
            client,
            secret,
            new { name, tenantId = ReadEndpointHarness.DefaultTenant, scopes = new[] { PlatformScopes.ProcessRead } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var issued = await ReadEndpointHarness.ReadJsonAsync(response);

        return issued.GetProperty("clientId").GetString()!;
    }

    private static Task<HttpResponseMessage> PutTenantAsync(
        HttpClient client,
        string secret,
        string tenantId,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/admin/v1/tenants/{tenantId}", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static Task<HttpResponseMessage> PutTenantStatusAsync(
        HttpClient client,
        string secret,
        string tenantId,
        bool active)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/admin/v1/tenants/{tenantId}/status", UriKind.Relative))
        {
            Content = JsonContent.Create(new { active }),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string secret, string clientId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"/admin/v1/clients/{clientId}", UriKind.Relative));

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    // ─────────────────────────── 旧面租户边界（P0 回归） ───────────────────────────

    [Fact]
    public async Task LegacyTenantSurface_ShouldRespectTenantBoundary()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 无凭据的 dev 身份不受租户约束：用它造一个「别家租户」
        using var other = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/other", UriKind.Relative),
            new { name = "别家租户" },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // 这把凭据绑定 default 租户、持有管理 scope
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        // ① 列表：旧面也不能列出别家租户（与 /admin/v1/tenants 同口径）
        using var list = await ReadEndpointHarness.GetAsync(client, "/api/admin/tenants", admin);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var tenantIds = (await ReadEndpointHarness.ReadJsonAsync(list))
            .EnumerateArray()
            .Select(item => item.GetProperty("tenantId").GetString())
            .ToArray();

        Assert.Contains(ReadEndpointHarness.DefaultTenant, tenantIds);
        Assert.DoesNotContain("other", tenantIds);

        // ② 跨租户写：登记 / 停用都必须是 403
        using var put = await PutLegacyTenantAsync(client, admin, "other", new { name = "别家租户" });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        using var status = await PutLegacyTenantStatusAsync(client, admin, "other", active: false);

        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
    }

    // ─────────────────────────── 密钥签发不落幂等表（P0 回归） ───────────────────────────

    [Fact]
    public async Task SecretIssuingEndpoint_ShouldNotBeServedFromIdempotencyCache()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var admin = await ReadEndpointHarness.RegisterAsync(client, "ops", AdminScopes);

        // 同一个 Idempotency-Key + 同一个请求体：普通写端点会被回放，
        // 密钥签发端点必须真的再发一把（否则明文密钥已被缓存进 ACT_IDEMPOTENCY）
        var key = Guid.NewGuid().ToString("N");

        using var first = await RegisterClientWithIdempotencyKeyAsync(client, admin, key, "erp");
        using var second = await RegisterClientWithIdempotencyKeyAsync(client, admin, key, "erp");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        Assert.False(
            second.Headers.Contains("Idempotency-Replayed"),
            "密钥签发端点不允许回放：回放意味着明文密钥被写入幂等表。");

        var firstId = (await ReadEndpointHarness.ReadJsonAsync(first)).GetProperty("clientId").GetString();
        var secondId = (await ReadEndpointHarness.ReadJsonAsync(second)).GetProperty("clientId").GetString();

        Assert.NotEqual(firstId, secondId);
    }

    // ─────────────────────────── 审计读：独立 scope + 租户边界（P0 回归） ───────────────────────────

    [Fact]
    public async Task AuditRead_ShouldRequireDedicatedScope()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只有客户端管理 scope（含能发密钥）：读不了审计
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "ops",
            [PlatformScopes.AdminClient, PlatformScopes.ProcessRead]);

        using var response = await ReadEndpointHarness.GetAsync(client, "/admin/v1/audit", secret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
        Assert.Contains(
            PlatformScopes.AdminAuditRead,
            problem.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditRead_ShouldNotCrossTenant()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        using var other = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/other", UriKind.Relative),
            new { name = "别家租户" },
            ReadEndpointHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var auditReader = await ReadEndpointHarness.RegisterAsync(
            client,
            "auditor",
            [PlatformScopes.AdminAuditRead, PlatformScopes.ProcessRead]);

        // 显式指向别家租户：403（租户管理员不是平台运营）
        using var crossed = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/audit?tenantId=other",
            auditReader);

        Assert.Equal(HttpStatusCode.Forbidden, crossed.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(crossed);

        Assert.Equal(ErrorCodes.TenantForbidden, problem.GetProperty("code").GetString());

        // 不带参数：自动落到本租户，正常返回
        using var own = await ReadEndpointHarness.GetAsync(client, "/admin/v1/audit", auditReader);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    private static Task<HttpResponseMessage> RegisterClientWithIdempotencyKeyAsync(
        HttpClient client,
        string secret,
        string idempotencyKey,
        string name)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/admin/v1/clients?confirmPrivilegedScopes=true", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                name,
                tenantId = ReadEndpointHarness.DefaultTenant,
                scopes = new[] { PlatformScopes.ProcessRead },
            }),
        };

        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static Task<HttpResponseMessage> PutLegacyTenantAsync(
        HttpClient client,
        string secret,
        string tenantId,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/api/admin/tenants/{tenantId}", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }

    private static Task<HttpResponseMessage> PutLegacyTenantStatusAsync(
        HttpClient client,
        string secret,
        string tenantId,
        bool active)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/api/admin/tenants/{tenantId}/status", UriKind.Relative))
        {
            Content = JsonContent.Create(new { active }),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }
}
