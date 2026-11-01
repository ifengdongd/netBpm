using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Admin;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 管理面流程目录覆盖行（<c>/admin/v1/processes</c>）的守门用例。
/// </summary>
/// <remarks>
/// <para>
/// 这组用例守的是<b>三条容易退化的事</b>：
/// ① 覆盖是<b>整行替换</b>——第二次 PUT 只给 category，上一次给的 name 必须回到 BPMN 派生
/// （否则「把字段改回跟随派生」这条路根本表达不出来）；
/// ② 空覆盖被拒——全空等价于「没有覆盖行」，写进去只会堆垃圾；
/// ③ unknown processKey 被拒——拼错一个 key 就在库里留一行谁也看不见的孤儿。
/// </para>
/// <para>
/// 用内存宿主：覆盖表的写路径与「读面合并」都在进程内完成，内存版与 Dapper 版的差异
/// 只有 SQL 那几行（由 <c>ProcessCatalogTests</c> 的真库用例覆盖「读」这一侧）。
/// </para>
/// </remarks>
public sealed class AdminProcessCatalogTests
{
    private static readonly string ProcessKey = ReadEndpointHarness.ProcessKey;

    private static string[] AdminScopes =>
    [
        PlatformScopes.AdminProcess,
        PlatformScopes.ProcessRead,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Put_ShouldCreateOverride_AndSurfaceInBusinessCatalog()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var put = await PutAsync(client, secret, ProcessKey, new
        {
            name = "请假申请（HR 精修）",
            category = "HR",
            pageUrls = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["start"] = "/hr/leave/new",
                ["approve"] = "/approve.html?taskId={taskId}",
            },
            sortOrder = 5,
            labels = new Dictionary<string, string>(StringComparer.Ordinal) { ["zh-CN"] = "请假" },
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var created = await ReadEndpointHarness.ReadJsonAsync(put);

        Assert.Equal(ProcessKey, created.GetProperty("processKey").GetString());
        Assert.Equal("default", created.GetProperty("tenantId").GetString());
        Assert.Equal("请假申请（HR 精修）", created.GetProperty("name").GetString());
        Assert.Equal("HR", created.GetProperty("category").GetString());
        Assert.Equal(5, created.GetProperty("sortOrder").GetInt32());

        // 业务面看到的是合并结果：精修值覆盖 BPMN 派生的 name
        using var catalog = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/processes/{ProcessKey}",
            secret);

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);

        var descriptor = await ReadEndpointHarness.ReadJsonAsync(catalog);

        Assert.Equal("请假申请（HR 精修）", descriptor.GetProperty("name").GetString());
        Assert.Equal("HR", descriptor.GetProperty("category").GetString());
        Assert.Equal("/hr/leave/new", descriptor.GetProperty("pageUrls").GetProperty("start").GetString());

        // 写路径要留痕：审计里能查到这次精修，且带上了改动的字段名
        var audit = await QueryAuditAsync(host, AdminAuditActions.ProcessCatalogUpsert, ProcessKey);

        Assert.Equal(1, audit.Total);
        Assert.Contains("Name", audit.Items[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_ShouldReplaceWholeRow_NotMergeFields()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using (var first = await PutAsync(client, secret, ProcessKey, new
        {
            name = "精修名",
            category = "HR",
            sortOrder = 9,
        }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        var before = await ReadJsonAsync(client, secret, $"/admin/v1/processes/{ProcessKey}");
        var createdAt = before.GetProperty("createdAt").GetDateTimeOffset();

        // 第二次只给 category：整行替换 ⇒ name 与 sortOrder 回到「跟随派生」
        using (var second = await PutAsync(client, secret, ProcessKey, new { category = "SALES" }))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var replaced = await ReadEndpointHarness.ReadJsonAsync(second);

            Assert.Equal("SALES", replaced.GetProperty("category").GetString());
            Assert.Equal(JsonValueKind.Null, replaced.GetProperty("name").ValueKind);
            Assert.Equal(0, replaced.GetProperty("sortOrder").GetInt32());
        }

        // created_at 是「这条覆盖第一次出现」的证据，不该被后续编辑改写
        var after = await ReadJsonAsync(client, secret, $"/admin/v1/processes/{ProcessKey}");

        Assert.Equal(createdAt, after.GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(
            after.GetProperty("updatedAt").GetDateTimeOffset() >= createdAt,
            "updated_at 必须随写入前进。");

        // 业务面的 name 回到 BPMN 里的 name（"只读端点演示"）
        var descriptor = await ReadJsonAsync(client, secret, $"/workflow/v1/processes/{ProcessKey}");

        Assert.Equal("只读端点演示", descriptor.GetProperty("name").GetString());
        Assert.Equal("SALES", descriptor.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Put_ShouldRejectEmptyOverride()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, ProcessKey, new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.CatalogOverrideEmpty, problem.GetProperty("code").GetString());

        // 一条都没写进去：覆盖行仍然不存在
        using var admin = await ReadEndpointHarness.GetAsync(
            client,
            $"/admin/v1/processes/{ProcessKey}",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, admin.StatusCode);
    }

    [Fact]
    public async Task Put_ShouldRejectUnknownProcessKey()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using var response = await PutAsync(client, secret, "no_such_process", new { name = "打错 key 的名片" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        // 用 process.not_found 而不是 catalog.override_not_found：这里是 key 打错了
        Assert.Equal(ErrorCodes.ProcessNotFound, problem.GetProperty("code").GetString());

        // 拼错的 key 不能留下一行孤儿覆盖
        using var list = await ReadEndpointHarness.GetAsync(client, "/admin/v1/processes", secret);
        var overrides = await ReadEndpointHarness.ReadJsonAsync(list);

        Assert.Empty(overrides.EnumerateArray());
    }

    [Fact]
    public async Task Delete_ShouldRemoveOverride_AndReturnToDerived()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-admin", AdminScopes);

        using (var put = await PutAsync(client, secret, ProcessKey, new { name = "临时精修", category = "HR" }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        using (var delete = await DeleteAsync(client, secret, ProcessKey))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        // 再删一次：没有覆盖行了，404 用 catalog.override_not_found（不是 process.not_found）
        using (var again = await DeleteAsync(client, secret, ProcessKey))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

            var problem = await ReadEndpointHarness.ReadJsonAsync(again);

            Assert.Equal(ErrorCodes.CatalogOverrideNotFound, problem.GetProperty("code").GetString());
        }

        var descriptor = await ReadJsonAsync(client, secret, $"/workflow/v1/processes/{ProcessKey}");

        Assert.Equal("只读端点演示", descriptor.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, descriptor.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task Put_ShouldRequireAdminProcessScope()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只拿业务读 scope 的接入方：能看目录，不能改名片
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", [PlatformScopes.ProcessRead]);

        using var response = await PutAsync(client, secret, ProcessKey, new { name = "越权改名" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());

        // 读仍然可以（scope 判定与读路径无关）
        using var catalog = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/processes/{ProcessKey}",
            secret);

        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        string secret,
        string processKey,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/admin/v1/processes/{processKey}", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string secret, string processKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri($"/admin/v1/processes/{processKey}", UriKind.Relative));

        request.Headers.Add("X-Api-Key", secret);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string secret, string path)
    {
        using var response = await ReadEndpointHarness.GetAsync(client, path, secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await ReadEndpointHarness.ReadJsonAsync(response);
    }

    private static Task<AdminAuditPage> QueryAuditAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string action,
        string targetId) =>
        host.Services
            .GetRequiredService<IAdminAuditStore>()
            .QueryAsync(new AdminAuditQuery(Action: action, TargetId: targetId), Ct)
            .AsTask();
}
