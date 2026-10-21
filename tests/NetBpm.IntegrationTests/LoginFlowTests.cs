using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 账号密码登录（<c>POST /api/auth/login</c>）端到端：REST 宿主 + 无库内存模式
/// （<c>NetBpm:Security:DevUsers</c> 供账号），因此不依赖 MySQL / Testcontainers。
/// <para>
/// 验证四件事：登录接口匿名可达、签出的令牌能被鉴权中间件接受、
/// 失败路径的语义（401 / 429 / 503）与锁定阈值、以及「未配签名密钥时不能假装登录成功」。
/// 管理宿主（NetBpm.Admin）共用同一套 <c>MapAuthEndpoints</c>，但它要求真实数据库
/// （管理端模型 / 流程类型存储依赖 IDbConnectionFactory），故这里的宿主选 REST。
/// </para>
/// </summary>
public sealed class LoginFlowTests
{
    // HS256 要求密钥至少 32 字节
    private const string SigningKey = "netbpm-login-flow-signing-key-0123456789-abcdef";
    private const string Issuer = "https://idp.netbpm.test";
    private const string Audience = "netbpm-api";

    private const string Username = "admin";
    private const string Password = "NetBpm@2026";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_ShouldIssueTokenAcceptedByProtectedEndpoints()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var token = await LoginAsync(client, Username, Password);

        Assert.False(string.IsNullOrWhiteSpace(token));

        // 1) 签出的令牌能被 /api/auth/me 认出来（身份来自令牌，而不是请求头）
        using (var me = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative)))
        {
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(me, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(response.IsSuccessStatusCode, $"/api/auth/me 拒绝登录签发的令牌：{response.StatusCode} {body}");

            using var json = JsonDocument.Parse(body);
            Assert.Equal(Username, json.RootElement.GetProperty("user").GetString());
            Assert.True(json.RootElement.GetProperty("isAdmin").GetBoolean());
            Assert.Contains("netbpm.admin", json.RootElement.GetProperty("scopes").EnumerateArray()
                .Select(item => item.GetString()));
        }

        // 2) 同一个令牌访问受保护的业务端点（netbpm.admin 是超级范围，含 model.read）
        using (var definitions = new HttpRequestMessage(
            HttpMethod.Get, new Uri("/api/designer/definitions", UriKind.Relative)))
        {
            definitions.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(definitions, Ct);
            Assert.True(response.IsSuccessStatusCode, $"业务端点拒绝登录令牌：{response.StatusCode}");
        }
    }

    [Fact]
    public async Task Login_Endpoint_ShouldBeAnonymousWhileOthersStayProtected()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        // 登录本身必须匿名可达（它负责换凭据，不能先要凭据）
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { username = Username, password = Password },
            Ct);

        Assert.True(login.IsSuccessStatusCode, $"匿名调用登录接口失败：{login.StatusCode}");

        using var anonymous = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPasswordOrUnknownUser_ShouldReturnSameUnauthorized()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var wrongPassword = await PostLoginAsync(client, Username, "wrong-password");
        var unknownUser = await PostLoginAsync(client, "no-such-user", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        // 统一文案 + 统一错误码：不透露账号是否存在
        Assert.Equal("invalid_credentials", ErrorCode(wrongPassword));
        Assert.Equal(ErrorCode(wrongPassword), ErrorCode(unknownUser));
    }

    [Fact]
    public async Task Login_AfterMaxFailures_ShouldLockAccount()
    {
        using var host = BuildHost(maxFailedAttempts: 3);
        using var client = host.CreateClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await PostLoginAsync(client, Username, "wrong-password");

            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // 达到阈值后即使口令正确也拒绝：锁定先于口令校验
        using var locked = await PostLoginAsync(client, Username, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("account_locked", ErrorCode(locked));
    }

    [Fact]
    public async Task Login_WithoutSigningKey_ShouldFailLoudly()
    {
        using var host = BuildHost(withJwt: false);
        using var client = host.CreateClient();

        using var response = await PostLoginAsync(client, Username, Password);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("jwt_not_configured", ErrorCode(response));
    }

    [Fact]
    public async Task Login_WithMissingFields_ShouldBeBadRequest()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { username = string.Empty, password = string.Empty },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        using var response = await PostLoginAsync(client, username, password);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"登录失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.GetProperty("token").GetString();

        Assert.False(string.IsNullOrWhiteSpace(token));

        // 过期时间必须是未来时刻（否则前端一登录就会被判过期）
        var expiresAt = json.RootElement.GetProperty("expiresAt").GetDateTime();

        Assert.True(expiresAt > DateTime.UtcNow, $"令牌已过期：{expiresAt:o}");

        return token!;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { username, password },
            Ct);

    private static string? ErrorCode(HttpResponseMessage response)
    {
        var body = response.Content.ReadAsStringAsync(Ct).GetAwaiter().GetResult();

        using var json = JsonDocument.Parse(body);

        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// 起一个<b>内存模式</b>的宿主（无数据库），账号来自 <c>NetBpm:Security:DevUsers</c>。
    /// </summary>
    private static WebApplicationFactory<Program> BuildHost(
        bool withJwt = true,
        int maxFailedAttempts = 5) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");

            // 口令连续失败阈值调小，锁定用例才不用刷十几次
            builder.UseSetting(
                "NetBpm:Security:Login:MaxFailedAttempts",
                maxFailedAttempts.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("NetBpm:Security:Login:LockoutMinutes", "1");

            builder.UseSetting("NetBpm:Security:DevUsers:0:Username", Username);
            builder.UseSetting("NetBpm:Security:DevUsers:0:Password", Password);
            builder.UseSetting("NetBpm:Security:DevUsers:0:DisplayName", "管理员");
            builder.UseSetting("NetBpm:Security:DevUsers:0:Scopes", "netbpm.admin");

            if (withJwt)
            {
                builder.UseSetting("NetBpm:Security:Jwt:SigningKey", SigningKey);
                builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
                builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);
            }
        });
}
