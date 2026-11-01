using System.Net;
using System.Net.Http.Json;
using System.Text;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 「错误体按面区分」的守门用例。
/// </summary>
/// <remarks>
/// <para>
/// 平台面（<c>/workflow/v1</c> / <c>/admin/v1</c>）的错误体<b>一律</b>是 RFC 9457，
/// 包括最容易漏的那几条通道：认证中间件的 401、全局异常处理器的请求体绑定失败。
/// 旧引擎面（<c>/api/**</c>）继续用 <c>{ code, message }</c> 信封。
/// </para>
/// <para>
/// 这几条用例存在的意义：错误体是 SDK 与网关的公共依赖，靠人眼 review「有没有走 PlatformProblems」
/// 是守不住的 —— 只要有一条通道漏了，问题解析就会在线上第一次调用时炸。
/// </para>
/// </remarks>
public sealed class ErrorSurfaceTests
{
    private static CancellationToken Ct => ReadEndpointHarness.Ct;

    [Fact]
    public async Task PlatformFace_Unauthorized_ShouldBeProblemJson()
    {
        using var host = ReadEndpointHarness.BuildHost();
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/workflow/v1/processes", UriKind.Relative));

        // 无效密钥在任何配置下都必须被拒（开放模式也只放行「完全不带凭据」）
        request.Headers.Add("X-Api-Key", "nbc.cl_0000000000000000.not-a-real-token");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.AuthUnauthorized, problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("title", out _), "RFC 9457 必须有 title。");
        Assert.Equal("/workflow/v1/processes", problem.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task EngineFace_Unauthorized_ShouldKeepLegacyEnvelope()
    {
        using var host = ReadEndpointHarness.BuildHost();
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/auth/me", UriKind.Relative));

        request.Headers.Add("X-Api-Key", "nbc.cl_0000000000000000.not-a-real-token");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        // 旧面保持 { code, message }：换错误体是破坏性变更，得跟 SDK 一起发版
        Assert.Equal("unauthorized", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("title", out _), "旧面不该出现 RFC 9457 字段。");
    }

    [Fact]
    public async Task PlatformFace_MalformedBody_ShouldBeProblemJson()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 半截 JSON：Minimal API 抛 BadHttpRequestException，走全局异常处理器
        using var content = new StringContent("{\"processKey\":", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            new Uri("/workflow/v1/instances", UriKind.Relative),
            content,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        // 平台面的「请求不合法」统一用 request.malformed，而不是旧引擎的 validation_error
        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task PlatformFace_TenantForbidden_ShouldBeProblemJson()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        using var other = await client.PutAsJsonAsync(
            new Uri("/admin/v1/tenants/other", UriKind.Relative),
            new { name = "别家租户" },
            Ct);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var auditReader = await ReadEndpointHarness.RegisterAsync(
            client,
            "auditor",
            [PlatformScopes.AdminAuditRead, PlatformScopes.ProcessRead]);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/admin/v1/audit?tenantId=other",
            auditReader);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.TenantForbidden, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PlatformFace_IdempotencyConflict_ShouldBeProblemJson()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.InstanceStart, PlatformScopes.InstanceRead]);

        var key = Guid.NewGuid().ToString("N");

        using var first = await StartInstanceAsync(client, secret, key, "biz-1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // 同键不同体：409 幂等键冲突
        using var second = await StartInstanceAsync(client, secret, key, "biz-2");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var problem = await ReadEndpointHarness.ReadJsonAsync(second);

        Assert.Equal(ErrorCodes.IdempotencyKeyReused, problem.GetProperty("code").GetString());
    }

    private static Task<HttpResponseMessage> StartInstanceAsync(
        HttpClient client,
        string secret,
        string idempotencyKey,
        string businessKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/workflow/v1/instances", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                processKey = ReadEndpointHarness.ProcessKey,
                businessKey,
            }),
        };

        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return client.SendAsync(request, Ct);
    }
}
