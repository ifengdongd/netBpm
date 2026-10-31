using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 审批记录的<b>人员级可见性</b>（<c>workflow:history:read:all</c> 分级）端到端。
/// </summary>
/// <remarks>
/// <para>
/// 这一组守住的是一句话：<c>workflow:history:read</c> 只证明「这个调用方会用审批记录」，
/// 不证明「这个人跟这张单据有关」。把两者当成一回事的结果是——
/// 任何一个拿到读权限的系统都能拉走全租户的审批意见。
/// </para>
/// <para>
/// 三条路径各一个用例：
/// <list type="bullet">
///   <item><description><b>相关人</b>（发起人）：能看全部条目，但看不到「是哪个系统提交的」；</description></item>
///   <item><description><b>无关真人</b>：整张单据按<b>不存在</b>处理（404，不是 403）；</description></item>
///   <item><description><b>history:read:all</b>：管理视角，全文（含 <c>actorCallerId</c>）。</description></item>
/// </list>
/// </para>
/// <para>
/// 用内存宿主足够：可见性判定依赖的是平台自己的时间线（<c>nb_instance_timeline</c>，内存实现）
/// 与参与者索引，都不踩 <c>ACT_HI_*</c>。跑在这儿比起 MySQL 更快，也更贴近谁都能跑。
/// </para>
/// </remarks>
public sealed class HistoryVisibilityTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "history_visibility_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>发起人能读本单全部条目，但看不到调用系统（那是平台自己的审计字段）。</summary>
    [Fact]
    public async Task History_ShouldBeRedactedForRelatedPeople()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "visibility-starter",
            [
                PlatformScopes.InstanceStart,
                PlatformScopes.HistoryRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartAsync(client, secret, "zhangsan");

        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            "zhangsan");

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 相关人视角里「谁的系统提交的」一律为空：这一列只服务合规报表
        foreach (var entry in body.GetProperty("entries").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("actorCallerId").ValueKind);
        }
    }

    /// <summary>与之无关的真人：不泄露这张单存在（404），且 404 的形状与「真的不存在」一致。</summary>
    [Fact]
    public async Task History_ShouldBeNotFound_ForUnrelatedPeople()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "visibility-outsider",
            [
                PlatformScopes.InstanceStart,
                PlatformScopes.HistoryRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartAsync(client, secret, "zhangsan");

        using var related = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            "zhangsan");
        var relatedBody = await ReadJsonAsync(related);

        Assert.Equal(HttpStatusCode.OK, related.StatusCode);
        Assert.NotEmpty(relatedBody.GetProperty("entries").EnumerateArray().ToList());

        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            "zhaoliu");
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.InstanceNotFound, problem.GetProperty("code").GetString());
    }

    /// <summary>持有 <c>history:read:all</c> 的管理视角：全文可见，含调用系统。</summary>
    [Fact]
    public async Task History_ShouldBeFull_WithReadAllScope()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "visibility-admin",
            [
                PlatformScopes.InstanceStart,
                PlatformScopes.HistoryRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.HistoryReadAll,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartAsync(client, secret, "zhangsan");

        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            "zhaoliu");

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entries = body.GetProperty("entries").EnumerateArray().ToList();

        Assert.NotEmpty(entries);

        // 管理视角保留 actorCallerId：发生故障时「这条是谁的系统写的」是排障第一问
        Assert.NotEqual(JsonValueKind.Null, entries[0].GetProperty("actorCallerId").ValueKind);
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

    /// <summary>部署两环节流程（申请 → 审批），两个环节都带审批选项。</summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="历史可见性演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="zhangsan">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(HttpClient client, string secret, string subject)
    {
        using var response = await SendPostAsync(
            client,
            "/workflow/v1/instances",
            secret,
            subject,
            new { processKey = ProcessKey });

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return body.GetProperty("instanceId").GetString()!;
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name, tenantId = DefaultTenant, scopes },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string path,
        string secret,
        string? subject = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SendPostAsync(
        HttpClient client,
        string path,
        string secret,
        string? subject,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        request.Content = JsonContent.Create(body);

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
