using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 幂等键的<b>命名空间</b>：同一个 <c>Idempotency-Key</c> 在不同调用主体之间不能互相打架。
/// </summary>
/// <remarks>
/// <para>
/// 幂等键是表的主键，全局唯一。业务系统生成幂等键的手法通常是「业务单号 + 步骤」
/// （比如 <c>order-1001-submit</c>），而不是 UUID —— 这种键在同一个系统内部够用，
/// 但两个接入方各自算自己的键，撞车只是时间问题。
/// </para>
/// <para>
/// 撞上之后的表现最难排查：后到的一方收到 409「该键已用于另一个请求」，
/// 而它确实是第一次调用；换成别的键能通、用它自己的键却要等 24 小时过期。
/// 因此幂等的作用域必须收窄到「一个调用主体」，而不是「整个平台」。
/// </para>
/// <para>
/// 本类在开放的测试宿主里用头域声明身份（<c>X-Subject-Id</c> / <c>X-Tenant-Id</c>），
/// 直接把这些标头换成 409 与「各自执行」两种结果来区分命名空间是否生效。
/// </para>
/// </remarks>
public sealed class IdempotencyNamespaceTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "idempotency_ns";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>同一主体：同键同请求必须回放，且只真实执行一次。</summary>
    [Fact]
    public async Task SameSubject_SameKey_ShouldReplay()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        const string key = "order-1001-submit";

        var first = await PostStartAsync(client, key, subject: "alice", businessKey: "BIZ-NS-1");
        var second = await PostStartAsync(client, key, subject: "alice", businessKey: "BIZ-NS-1");

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Body, second.Body);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);

        Assert.Single(await FindInstanceIdsAsync(client, "BIZ-NS-1"));
    }

    /// <summary>
    /// 不同主体：同一个键必须各走各的。
    /// 若命名空间失效，第二次会命中别人的记录，而请求体又不同 → 409。
    /// </summary>
    [Fact]
    public async Task DifferentSubject_SameKey_ShouldNotCollide()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        const string key = "order-1001-submit";

        var alice = await PostStartAsync(client, key, subject: "alice", businessKey: "BIZ-NS-A");
        var bob = await PostStartAsync(client, key, subject: "bob", businessKey: "BIZ-NS-B");

        Assert.True(alice.Status == HttpStatusCode.Created, $"alice → {alice.Status} {alice.Body}");
        Assert.True(bob.Status == HttpStatusCode.Created, $"bob → {bob.Status} {bob.Body}");
        Assert.False(bob.Replayed);

        // 两个实例各自存在：说明后到的请求真的执行了，而不是回放了别人的响应
        var createdByAlice = Assert.Single(await FindInstanceIdsAsync(client, "BIZ-NS-A"));
        var createdByBob = Assert.Single(await FindInstanceIdsAsync(client, "BIZ-NS-B"));

        Assert.NotEqual(createdByAlice, createdByBob);
    }

    /// <summary>不同租户：命名空间回落到租户也要能隔开（既不能被别的租户撞，也不能 409）。</summary>
    [Fact]
    public async Task DifferentTenant_SameKey_ShouldNotCollide()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        const string key = "order-1001-submit";

        var tenantDefault = await PostStartAsync(
            client, key, tenantId: "default", businessKey: "BIZ-NS-T1");

        var tenantOther = await PostStartAsync(
            client, key, tenantId: "tenant-other", businessKey: "BIZ-NS-T2");

        Assert.Equal(HttpStatusCode.Created, tenantDefault.Status);
        Assert.NotEqual(HttpStatusCode.Conflict, tenantOther.Status);
        Assert.Single(await FindInstanceIdsAsync(client, "BIZ-NS-T1"));
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
            builder.UseSetting("NetBpm:Security:Tenants:Allowed", DefaultTenant + ",tenant-other");
        });

    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        foreach (var tenant in new[] { DefaultTenant, "tenant-other" })
        {
            var engine = host.Services.GetRequiredService<IProcessEngine>();

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml()));

            await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", tenantId: tenant, ct: Ct);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body, bool Replayed)> PostStartAsync(
        HttpClient client,
        string idempotencyKey,
        string businessKey,
        string? subject = null,
        string? tenantId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/runtime/process-instances", UriKind.Relative));

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        if (subject is not null)
        {
            // 开放模式下头域身份只认 X-User-Id：X-Subject-Id 是客户端凭据模式 +
            // workflow:task:submit:onbehalf 才生效的「代客声明」，这里用会被静默忽略。
            request.Headers.Add("X-User-Id", subject);
        }

        request.Headers.Add("X-Tenant-Id", tenantId ?? DefaultTenant);
        request.Content = new StringContent(
            $$"""{"processDefinitionKey":"{{ProcessKey}}","businessKey":"{{businessKey}}"}""",
            Encoding.UTF8,
            "application/json");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        var replayed = response.Headers.TryGetValues("Idempotency-Replayed", out var values)
            && values.Contains("true", StringComparer.OrdinalIgnoreCase);

        return (response.StatusCode, body, replayed);
    }

    /// <summary>
    /// 只把实例 id 取出来再返回：<c>JsonDocument</c> 一释放，<c>JsonElement</c> 就跟着失效
    /// （<c>ObjectDisposedException</c>），不能把元素本身带出方法。
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindInstanceIdsAsync(
        HttpClient client,
        string businessKey)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"/api/runtime/process-instances?businessKey={businessKey}", UriKind.Relative));

        request.Headers.Add("X-Tenant-Id", DefaultTenant);

        using var response = await client.SendAsync(request, Ct);

        Assert.True(response.IsSuccessStatusCode, $"查询实例失败：{response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(Ct);

        using var json = JsonDocument.Parse(body);

        return json.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    private static string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="幂等命名空间演示" isExecutable="true">
            <bpmn:startEvent id="start" name="Start" />
            <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_review" />
            <bpmn:userTask id="task_review" name="复核" activiti:assignee="alice" />
            <bpmn:sequenceFlow id="flow2" sourceRef="task_review" targetRef="end" />
            <bpmn:endEvent id="end" name="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;
}
