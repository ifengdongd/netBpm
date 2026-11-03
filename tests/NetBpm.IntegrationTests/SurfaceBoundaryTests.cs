using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 引擎原语面与平台面的<b>边界</b>：管理干预 / 变量 / 外部任务仍只走 <c>/api/**</c>，
/// 且仍按 <c>workflow:*</c> scope 判定。
/// </summary>
/// <remarks>
/// <para>
/// 这是一条「决策护栏」，不是一条功能用例。平台化改造的结论是：
/// <b>客户端只走 <c>/workflow/v1</c>，引擎面只保留读与管理干预</b>。
/// 因此 modify / jump / migrate / 变量直写 / 外部任务这些能力<b>不</b>搬进平台面
/// （搬进去等于给外部接入方开管理通道，还要为它们扩 22 路径的对外契约）。
/// </para>
/// <para>
/// 决策如果不写成可执行断言，半年后就会有人「顺手把 jump 加到 /admin/v1」，
/// 而那时没人记得当初为什么没加。这条用例把三件事钉住：
/// ① 这些端点还在引擎面且仍挂 scope；② 没有管理 scope 的凭据调不动；
/// ③ 它们没有出现在治理快照里（两份快照都由各自的门禁守着）。
/// </para>
/// <para>
/// 若将来确实要把某个能力搬到平台面，正确做法是：先在 <c>docs/platform-api-refactoring-plan.md</c>
/// 里改边界，再改本用例、契约快照与 SDK —— 而不是让新端点悄悄出现在平台上。
/// </para>
/// </remarks>
public sealed class SurfaceBoundaryTests
{
    private const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/runtime/process-instances")]
    [InlineData("POST", "/api/runtime/process-instances/batch/delete")]
    [InlineData("POST", "/api/runtime/external-tasks/fetch")]
    [InlineData("GET", "/api/runtime/external-tasks")]
    public async Task EngineSurface_Should403_ForClientWithoutMatchingScope(string method, string path)
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        // 只有审批办理的 scope：能办自己的待办，但没有任何管理 / 外部任务能力
        var secret = await RegisterAsync(client, [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit]);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative))
        {
            Content = method == "POST"
                ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                : null,
        };

        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Forbidden,
            $"{method} {path} 应因缺 scope 返回 403，实际 {response.StatusCode}。"
            + "引擎面如果对客户端放行管理动作，平台面那套 scope 就形同虚设。");
    }

    [Fact]
    public async Task EngineSurface_ShouldServe_ForWorkerWithExternalTaskScope()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, [PlatformScopes.ExternalTask]);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/runtime/external-tasks/fetch", UriKind.Relative))
        {
            Content = new StringContent(
                """{"workerId":"worker-1","topics":["demo"],"maxTasks":1}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("X-Api-Key", secret);

        using var response = await client.SendAsync(request, Ct);

        // 对照组：scope 对了就不该被挡（否则上面的 403 可能只是「路径写错」）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
            new { name = "surface-boundary", tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }
}
