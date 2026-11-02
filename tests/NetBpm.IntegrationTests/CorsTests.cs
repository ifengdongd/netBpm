using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 跨域（CORS）：默认关闭、按配置的来源白名单放行、预检不被鉴权拦掉。
/// </summary>
/// <remarks>
/// <para>
/// 这一组守的是<b>平台化之后的新场景</b>：管理端与设计器由 <c>wwwroot</c> 同源托管，
/// 浏览器根本不发预检；但独立部署的前端（异源 SPA / 本地 dev server）直连 <c>/workflow/v1</c>
/// 时，缺一条 CORS 响应头就是「页面白屏、控制台只有一句 CORS error」。
/// </para>
/// <para>
/// 断言的是三条互相独立的边界：<b>不配就是关</b>（不能默认放开任意来源）、
/// <b>白名单内放行</b>、<b>白名单外不带头</b>（不是返回 403 —— CORS 是浏览器的判定，
/// 服务端只负责「不给许可」，把 403 当成 CORS 失败反而更难排查）。
/// </para>
/// </remarks>
public class CorsTests
{
    private const string AllowedOrigin = "https://approve.example.com";
    private const string OtherAllowedOrigin = "http://localhost:5173";
    private const string ForeignOrigin = "https://evil.example.com";

    /// <summary>
    /// 没配来源时不启用 CORS：响应里不能出现任何 <c>Access-Control-*</c> 头，
    /// 否则等于「默认对所有站点开放」。
    /// </summary>
    [Fact]
    public async Task Cors_ShouldBeDisabled_WhenNoOriginConfigured()
    {
        using var host = BuildHost([]);
        using var client = host.CreateClient();

        using var request = Preflight("/health", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "未配置 NetBpm:Cors:AllowedOrigins 时不应返回 CORS 许可头。");
    }

    /// <summary>白名单来源的预检要拿到许可，且方法 / 头 / 缓存时长都符合预期。</summary>
    [Fact]
    public async Task Preflight_ShouldSucceed_ForAllowedOrigin()
    {
        using var host = BuildHost([AllowedOrigin, OtherAllowedOrigin]);
        using var client = host.CreateClient();

        using var request = Preflight("/workflow/v1/instances", AllowedOrigin, "X-Api-Key", "Idempotency-Key");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));

        var allowedMethods = Single(response, "Access-Control-Allow-Methods");
        Assert.Contains("GET", allowedMethods, StringComparison.Ordinal);
        Assert.Contains("POST", allowedMethods, StringComparison.Ordinal);

        var allowedHeaders = Single(response, "Access-Control-Allow-Headers");
        Assert.Contains("X-Api-Key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Idempotency-Key", allowedHeaders, StringComparison.OrdinalIgnoreCase);

        // 预检结果可缓存：否则每个跨域请求都要多一次往返
        Assert.True(response.Headers.Contains("Access-Control-Max-Age"));
    }

    /// <summary>
    /// 预检必须绕过鉴权：浏览器发预检时<b>不带</b>凭据头，
    /// 若先过鉴权就会 401，而浏览器不会把 401 的原因告诉前端。
    /// </summary>
    /// <remarks>
    /// 关键是对照：同一个宿主上，<b>真实请求</b>不带凭据必须是 401，而<b>预检</b>必须放行。
    /// 只测预检通过是没意义的——把鉴权整体关掉也能过，证明不了中间件顺序。
    /// </remarks>
    [Fact]
    public async Task Preflight_ShouldBypassAuthentication()
    {
        using var host = BuildHost([AllowedOrigin], requireCredential: true);
        using var client = host.CreateClient();

        // 对照：真实请求没有凭据 → 401（说明鉴权确实生效）
        using (var guarded = new HttpRequestMessage(HttpMethod.Get, "/workflow/v1/instances"))
        {
            using var denied = await client.SendAsync(guarded);

            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // 预检没有凭据 → 仍然 204 且带许可头（说明 CORS 在鉴权之前）
        using var request = Preflight("/workflow/v1/instances", AllowedOrigin);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// 白名单外的来源：服务端照常返回业务结果，但<b>不给</b> CORS 许可，
    /// 由浏览器决定拦截。返回 403 会把「浏览器策略」和「服务端授权」混成同一件事。
    /// </summary>
    [Fact]
    public async Task Request_FromForeignOrigin_ShouldNotBeAuthorized()
    {
        using var host = BuildHost([AllowedOrigin]);
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", ForeignOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "白名单外的来源不应拿到 Access-Control-Allow-Origin。");
    }

    /// <summary>真实请求（非预检）也要带许可头，否则浏览器同样读不到响应体。</summary>
    [Fact]
    public async Task Request_FromAllowedOrigin_ShouldCarryCorsHeaders()
    {
        using var host = BuildHost([AllowedOrigin]);
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", AllowedOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));

        // 分页总数在 X-Total-Count 里：不 expose 的话跨域前端读不到它，只能自己数条数
        var exposed = Single(response, "Access-Control-Expose-Headers");
        Assert.Contains("X-Total-Count", exposed, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static HttpRequestMessage Preflight(string path, string origin, params string[] requestHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);

        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        if (requestHeaders.Length > 0)
        {
            request.Headers.Add("Access-Control-Request-Headers", string.Join(',', requestHeaders));
        }

        return request;
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.GetValues(header));

    /// <summary>
    /// 起一个内存宿主，只按需注入跨域来源。
    /// </summary>
    /// <remarks>
    /// 配置走 <c>UseSetting</c>：<c>AddNetBpmHost</c> 在<b>服务注册阶段</b>就读了配置，
    /// <c>ConfigureAppConfiguration</c> 要等 <c>Build()</c> 才生效，那时早已被读走（与 RestEngineFactory 同一坑）。
    /// </remarks>
    private static WebApplicationFactory<Program> BuildHost(
        string[] allowedOrigins,
        bool requireCredential = false) =>
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

            for (var index = 0; index < allowedOrigins.Length; index++)
            {
                builder.UseSetting($"NetBpm:Cors:AllowedOrigins:{index}", allowedOrigins[index]);
            }

            // 测试宿主默认会吃到 appsettings.Development.json 里的 dev JWT 密钥，
            // 只要「有校验密钥」鉴权就进入强制模式；显式清掉，行为才只由 RequireCredential 决定。
            builder.UseSetting("NetBpm:Security:Jwt:SigningKey", string.Empty);
        });
}
