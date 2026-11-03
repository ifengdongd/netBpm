using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// <c>/admin/v1</c> 的组级授权兜底：只有业务 scope 的凭据不能进入任何管理端点。
/// </summary>
/// <remarks>
/// <para>
/// 这条用例守的是「新增管理端点漏写 handler 内 <c>HasScope</c>」这一类错误。
/// 端点内的判定负责<b>细粒度</b>（哪个操作要哪个 scope），中间件负责<b>兜底</b>（连一个管理 scope 都没有时直接 403）。
/// 只有细粒度没有兜底时，「漏写一次」= 「任意接入方都能调」。
/// </para>
/// <para>
/// 判据用 403 而不是 404 / 422：这里不涉及资源存在性，只涉及「你有没有资格进这个面」。
/// 与端点内的 403 共用 <c>auth.insufficient_scope</c>，调用方不需要区分是兜底还是细粒度拒绝。
/// </para>
/// </remarks>
public sealed class AdminSurfaceGuardTests
{
    private const string DefaultTenant = "default";

    /// <summary>
    /// 管理面全部只读入口（覆盖 5 个 Map*Endpoints）。新增端点时应同步加进来。
    /// </summary>
    private static readonly string[] AdminGetPaths =
    [
        "/admin/v1/tenants",
        "/admin/v1/clients",
        "/admin/v1/processes",
        "/admin/v1/dictionaries",
        "/admin/v1/audit",
        "/admin/v1/audit/facets",
        "/admin/v1/subscriptions",
        "/admin/v1/events/deadletters",
        "/admin/v1/events/stats",
        "/admin/v1/quotas",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdminSurface_Should403_ForCredentialWithoutAnyAdminScope()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        // 只有业务面的读 scope：能调 /workflow/v1，但没有资格进 /admin/v1
        var secret = await RegisterAsync(client, [PlatformScopes.TaskRead, PlatformScopes.ProcessRead]);

        foreach (var path in AdminGetPaths)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Api-Key", secret);

            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Forbidden,
                $"{path} 应被组级兜底拒绝（403），实际 {response.StatusCode}：{body}");

            using var problem = JsonDocument.Parse(body);

            Assert.Equal(
                ErrorCodes.AuthInsufficientScope,
                problem.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AdminSurface_ShouldStillServe_ForCredentialWithAdminScope()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        // 对照组：兜底不能把合法的管理凭据也挡掉（否则它就从护栏变成了故障）
        var secret = await RegisterAsync(client, [PlatformScopes.AdminClient]);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/admin/v1/tenants", UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AdminSurface_Should403_ForWriteEndpoint_WithoutAdminScope()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, [PlatformScopes.InstanceStart]);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri("/admin/v1/dictionaries/some_process/options/1", UriKind.Relative))
        {
            Content = new StringContent("""{"nodeId":"task_approve","semantic":"APPROVE"}""", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    private static async Task<string> RegisterAsync(HttpClient client, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name = "surface-guard", tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }
}
