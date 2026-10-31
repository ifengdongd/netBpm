using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.IntegrationTests;

/// <summary>
/// JWT 的非对称模式（RS256）：公钥分发（<c>/.well-known/jwks.json</c>）、
/// 自签令牌校验、算法白名单，以及外部 IdP 公钥集的缓存。
/// </summary>
/// <remarks>
/// <para>
/// RS256 是「对外提供服务」与「内部统一接入」的分界线：只有公钥可分发，外部系统才能自己验签，
/// 平台也不必把私钥复制给每一个调用方。HS256 保留为内网模式（见 <see cref="JwtAuthenticationTests"/>）。
/// </para>
/// <para>
/// 算法白名单是本组用例里最重要的一条：若校验端同时接受 HS256，攻击者可以拿<b>公开的</b>
/// RSA 公钥当 HMAC 密钥签一个 HS256 令牌，从而完全绕过签名。
/// </para>
/// </remarks>
public sealed class JwtRs256Tests
{
    private const string Issuer = "https://idp.netbpm.test";
    private const string Audience = "netbpm-api";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Jwks_ShouldExposePublicKey_AndAcceptRs256Token()
    {
        using var signingKey = RSA.Create(2048);
        var privatePem = signingKey.ExportPkcs8PrivateKeyPem();

        using var host = BuildHost(privatePem);
        using var client = host.CreateClient();

        // ① 公钥匿名可达（否则外部系统无法自验签），字段与 JOSE 规范一致
        using var jwks = await client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);

        var document = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync(Ct)).RootElement;
        var keys = document.GetProperty("keys").EnumerateArray().ToList();
        var key = Assert.Single(keys);

        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());

        var kid = key.GetProperty("kid").GetString();

        Assert.False(string.IsNullOrWhiteSpace(kid), "JWKS 必须给出 kid（轮换期会有多把公钥）。");
        Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("n").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("e").GetString()));

        // ② 用该私钥签的令牌能通过校验：身份与 scope 正常透传
        using (var ok = await SendAsync(client, CreateRs256Token(signingKey, kid, PlatformScopes.ProcessRead), "/workflow/v1/processes"))
        {
            Assert.True(ok.IsSuccessStatusCode, $"RS256 令牌被拒绝：{ok.StatusCode}");
        }

        // ③ 换一把私钥（kid 相同、签名不同）→ 401
        using (var other = RSA.Create(2048))
        using (var forged = await SendAsync(
            client, CreateRs256Token(other, kid, PlatformScopes.ProcessRead), "/workflow/v1/processes"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        // ④ 只配 RS256 时 HS256 令牌必须被拒（算法混淆攻击的防线）
        using (var hs256 = await SendAsync(
            client, CreateHs256Token("attacker-key-0123456789-0123456789-abcd", kid), "/workflow/v1/processes"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, hs256.StatusCode);
        }
    }

    /// <summary>只有公钥时也能验签（对外部 IdP 签发的令牌，本服务不持私钥）。</summary>
    [Fact]
    public async Task Jwks_ShouldValidateWithPublicKeyOnly()
    {
        using var idpRsa = RSA.Create(2048);
        var publicPem = idpRsa.ExportSubjectPublicKeyInfoPem();

        using var host = BuildHost(privatePem: null, publicPem: publicPem);
        using var client = host.CreateClient();

        // 公钥模式下不提供签发能力，但 JWKS 仍然要能读出 kid
        using var jwks = await client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Ct);
        var document = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync(Ct)).RootElement;
        var kid = document.GetProperty("keys")[0].GetProperty("kid").GetString();

        using var ok = await SendAsync(
            client, CreateRs256Token(idpRsa, kid, PlatformScopes.ProcessRead), "/workflow/v1/processes");

        Assert.True(ok.IsSuccessStatusCode, $"公钥验签失败：{ok.StatusCode}");
    }

    /// <summary>
    /// 外部 IdP 公钥集：按 <c>kid</c> 选公钥，并做缓存（同一个 kid 只拉一次）。
    /// </summary>
    [Fact]
    public void RemoteJwks_ShouldFetchFilterByKid_AndCache()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);

        var jwksJson = BuildJwksJson(("kid-1", first), ("kid-2", second));
        var handler = new RecordingHandler(jwksJson);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RemoteJwksProvider.ConfigKey] = "https://idp.test/.well-known/jwks.json",
                // 用例要在同一次运行里触发「密钥轮换 → 重新拉取」，间隔必须归零
                ["NetBpm:Security:Jwt:JwksMinRefreshSeconds"] = "0",
            })
            .Build();

        using var http = new HttpClient(handler);
        var provider = new RemoteJwksProvider(configuration, http, NullLogger<RemoteJwksProvider>.Instance);

        var keys = provider.Resolve("kid-2");

        Assert.Single(keys);
        Assert.Equal("kid-2", keys[0].KeyId);
        Assert.Equal(1, handler.Calls);

        // 第二次：缓存命中，不再发请求
        var again = provider.Resolve("kid-2");

        Assert.Single(again);
        Assert.Equal(1, handler.Calls);

        // 未知 kid（IdP 轮换密钥的正常路径）：允许重新拉取一次，仍然拿不到就返回空——
        // 此时验签会失败并 401，绝不会「找不到公钥就放行」
        var unknown = provider.Resolve("kid-3");

        Assert.Empty(unknown);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// HS256（内网模式）在引入 RS256 后仍然可用，且不接受非对称令牌——
    /// 两个方向都要守：只测「新的能用」会漏掉「旧的被顺手弄坏」。
    /// </summary>
    [Fact]
    public async Task Hs256_ShouldStillWork_WhenOnlySymmetricKeyConfigured()
    {
        const string signingKey = "netbpm-jwt-signing-key-0123456789-abcdefghij";

        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", "default");
            builder.UseSetting("NetBpm:Security:Jwt:SigningKey", signingKey);
            builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
            builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);
        });

        using var client = host.CreateClient();

        using (var ok = await SendAsync(client, CreateHs256Token(signingKey), "/workflow/v1/processes"))
        {
            Assert.True(ok.IsSuccessStatusCode, $"HS256 令牌被拒绝：{ok.StatusCode}");
        }

        // 仅配置对称密钥时，JWKS 是空集：调用方据此知道本服务不提供非对称验签
        using (var jwks = await client.GetAsync(new Uri("/.well-known/jwks.json", UriKind.Relative), Ct))
        {
            var document = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync(Ct)).RootElement;

            Assert.Equal(0, document.GetProperty("keys").GetArrayLength());
        }

        // 非对称令牌在 HS256 模式下必须被拒（没有对应公钥，也不在算法白名单里）
        using (var rsa = RSA.Create(2048))
        using (var rejected = await SendAsync(
            client, CreateRs256Token(rsa, kid: null, PlatformScopes.ProcessRead), "/workflow/v1/processes"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
    }

    /// <summary>
    /// 登录签发的令牌必须能被同一个宿主校验：这是「签发器与校验器共用一份密钥材料」的端到端证明。
    /// </summary>
    [Fact]
    public async Task Login_ShouldIssueRs256Token_ThatTheValidatorAccepts()
    {
        using var signingKey = RSA.Create(2048);

        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "true");
            builder.UseSetting("NetBpm:EnableHistory", "false");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", "default");
            builder.UseSetting("NetBpm:Security:Jwt:Algorithm", "RS256");
            builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
            builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);
            builder.UseSetting("NetBpm:Security:Jwt:RsaPrivateKey", signingKey.ExportPkcs8PrivateKeyPem());
            builder.UseSetting("NetBpm:Security:DevUsers:0:Username", "alice");
            builder.UseSetting("NetBpm:Security:DevUsers:0:Password", "Passw0rd!");
            builder.UseSetting("NetBpm:Security:DevUsers:0:Scopes", PlatformScopes.ProcessRead);
        });

        using var client = host.CreateClient();

        using var login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { username = "alice", password = "Passw0rd!" },
            Ct);

        var body = await login.Content.ReadAsStringAsync(Ct);

        Assert.True(login.IsSuccessStatusCode, $"登录失败：{login.StatusCode} {body}");

        var token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString()!;
        var header = new JwtSecurityTokenHandler().ReadJwtToken(token).Header;

        Assert.Equal(SecurityAlgorithms.RsaSha256, header.Alg);
        Assert.False(string.IsNullOrWhiteSpace(header.Kid), "RS256 令牌必须带 kid。");

        using var ok = await SendAsync(client, token, "/workflow/v1/processes");

        Assert.True(ok.IsSuccessStatusCode, $"登录签发的令牌没通过校验：{ok.StatusCode}");
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static WebApplicationFactory<Program> BuildHost(string? privatePem, string? publicPem = null) =>
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
            builder.UseSetting("NetBpm:Security:Tenants:Default", "default");
            builder.UseSetting("NetBpm:Security:Jwt:Algorithm", "RS256");
            builder.UseSetting("NetBpm:Security:Jwt:Issuer", Issuer);
            builder.UseSetting("NetBpm:Security:Jwt:Audience", Audience);

            if (privatePem is not null)
            {
                builder.UseSetting("NetBpm:Security:Jwt:RsaPrivateKey", privatePem);
            }

            if (publicPem is not null)
            {
                builder.UseSetting("NetBpm:Security:Jwt:RsaPublicKey", publicPem);
            }
        });

    private static string CreateRs256Token(RSA rsa, string? kid, string scope)
    {
        var credentials = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, "carol"),
                new Claim("tenant", "default"),
                new Claim("scope", scope),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        if (kid is not null)
        {
            token.Header["kid"] = kid;
        }

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateHs256Token(string signingKey, string? kid = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, "mallory"),
                new Claim("tenant", "default"),
                new Claim("scope", PlatformScopes.ProcessRead),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        if (kid is not null)
        {
            token.Header["kid"] = kid;
        }

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client.SendAsync(request, Ct);
    }

    private static string BuildJwksJson(params (string Kid, RSA Rsa)[] keys)
    {
        var documents = keys.Select(item =>
        {
            var parameters = item.Rsa.ExportParameters(includePrivateParameters: false);

            return new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = item.Kid,
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent),
            };
        });

        return JsonSerializer.Serialize(new { keys = documents });
    }

    /// <summary>固定返回一段 JWKS 的 HTTP 处理器，并记录被调用次数。</summary>
    private sealed class RecordingHandler(string payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
