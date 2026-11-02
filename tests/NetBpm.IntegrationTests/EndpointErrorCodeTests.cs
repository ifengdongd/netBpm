using System.Net;
using System.Net.Http.Json;
using NetBpm.Platform.Contracts;
using NetBpm.Rest;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 端点错误体必须带机器可读的 <see cref="ErrorResponse.Code"/>：调用方按 code 分支，
/// 不去匹配中文文案（文案一改就断）。
/// </summary>
/// <remarks>
/// <para>
/// 这里只守「曾经裸着返回、<c>code</c> 为 null」的两条路径（登录 400、身份 404）：
/// 它们现在走同一个助手 <c>EndpointResults</c>，任何一处退回裸 <c>ErrorResponse</c> 都会被这两条抓住。
/// </para>
/// <para>
/// 设计器那几条 400 / 404 也改用了同一个助手，但它们的端点挂在 <c>netbpm.admin.*</c> 管理 scope 下
/// （客户端密钥拿不到），由 <c>AdminGovernanceTests</c> 的 JWT 链路间接覆盖，这里不重复造凭据。
/// </para>
/// </remarks>
public sealed class EndpointErrorCodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>登录缺参数：400 + <c>validation_error</c>（此前是裸 <c>ErrorResponse</c>）。</summary>
    [Fact]
    public async Task Login_WithMissingCredentials_ShouldCarryValidationErrorCode()
    {
        using var host = ReadEndpointHarness.BuildHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { username = "", password = "" },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(EngineErrorCode.ValidationError, body.GetProperty("code").GetString());
    }

    /// <summary>查不存在的组：404 + <c>not_found</c>（此前是裸 <c>ErrorResponse</c>）。</summary>
    [Fact]
    public async Task UnknownGroup_ShouldCarryNotFoundCode()
    {
        using var host = ReadEndpointHarness.BuildHost();
        using var client = host.CreateClient();

        var secret = await ReadEndpointHarness.RegisterAsync(
            client,
            "error-code",
            [PlatformScopes.TaskRead]);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/api/identity/groups/no-such-group",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(EngineErrorCode.NotFound, body.GetProperty("code").GetString());
    }
}
