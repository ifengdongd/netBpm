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
/// <c>netbpm:parameters</c> 覆盖扩展（§4.3）：<c>apiName</c> / <c>writableBy</c> /
/// <c>visibleTo</c> / 约束端到端。
/// </summary>
/// <remarks>
/// <para>
/// 平台扩展表达的是 <c>formProperty</c> 说不出来的那几件事：<b>谁能看一个参数</b>。
/// 此前 <c>GET /instances/{id}</c> 会把实例的全部业务变量一股脑返回，
/// 「这个字段只对审批人可见」这种要求只能靠调用方自觉——那等于没有。
/// </para>
/// <para>
/// 三条断言分别打在三件事上：
/// <list type="number">
///   <item><description><b>覆盖生效</b>：apiName / 类型 / 约束 / 可见角色出现在参数规范里；</description></item>
///   <item><description><b>看不见就<b>不存在</b></b>：不满足 <c>visibleTo</c> 时键整条消失，而不是返回 null；</description></item>
///   <item><description><b>没声明就不限制</b>：老 BPMN（不写扩展）一个字都不用改。</description></item>
/// </list>
/// </para>
/// <para>
/// 内存宿主足够：参数规范来自 BPMN 派生（带缓存），可见性只依赖实例上的角色判定。
/// </para>
/// </remarks>
public sealed class ParameterOverridesTests
{
    private const string DefaultTenant = "default";
    private const string OverriddenKey = "param_overridden";
    private const string LegacyKey = "param_legacy";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>扩展里声明的属性覆盖派生结果，并在参数规范里出现。</summary>
    [Fact]
    public async Task Schema_ShouldCarryPlatformOverrides()
    {
        using var host = BuildHost();
        await DeployAsync(host, OverriddenKey);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "param-overrides", [PlatformScopes.ProcessRead]);

        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/processes/{OverriddenKey}/parameters/schema",
            secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var parameters = body.GetProperty("parameters").EnumerateArray().ToList();

        // days：由 formProperty 派生，再被扩展覆盖（类型 / 范围 / 可见角色）
        var days = parameters.Single(item => item.GetProperty("variableName").GetString() == "days");

        Assert.Equal("param.days", days.GetProperty("apiName").GetString());
        Assert.Equal("LONG", days.GetProperty("type").GetString());
        Assert.Equal(1, days.GetProperty("constraints").GetProperty("minimum").GetInt32());
        Assert.Equal(365, days.GetProperty("constraints").GetProperty("maximum").GetInt32());
        Assert.Equal(
            ["APPLICANT"],
            days.GetProperty("visibleTo").EnumerateArray().Select(role => role.GetString()).ToArray());
        Assert.Equal(
            ["APPLICANT"],
            days.GetProperty("writableBy").EnumerateArray().Select(role => role.GetString()).ToArray());

        // costCenter：BPMN 环节上没有对应表单字段，只存在于扩展里——它照样要进规范
        var costCenter = parameters.Single(item => item.GetProperty("variableName").GetString() == "costCenter");

        Assert.Equal("^[A-Z]{2,4}-\\d{2}$", costCenter.GetProperty("constraints").GetProperty("pattern").GetString());
        Assert.Equal(
            ["APPROVER", "ADMIN"],
            costCenter.GetProperty("visibleTo").EnumerateArray().Select(role => role.GetString()).ToArray());
    }

    /// <summary>
    /// <c>visibleTo</c> 不满足时参数<b>整条消失</b>：不是 null，也不是空串——那两种都会让调用方
    /// 分不清「没填」与「不该看」。
    /// </summary>
    [Fact]
    public async Task Parameters_ShouldBeFiltered_ByVisibleTo()
    {
        using var host = BuildHost();
        await DeployAsync(host, OverriddenKey);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "param-visibility",
            [
                PlatformScopes.ProcessRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.InstanceStart,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartAsync(
            client,
            secret,
            "zhangsan",
            new
            {
                processKey = OverriddenKey,
                parameters = new Dictionary<string, string?>
                {
                    ["param.days"] = "3",
                    ["costCenter"] = "HR-01",
                    ["note"] = "可见性演示",
                },
            });

        // ① 发起人：看得到 days，看不到只对审批人可见的 costCenter
        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances/{instanceId}", secret, "zhangsan"))
        {
            var body = await ReadJsonAsync(response);
            var values = body.GetProperty("parameters").GetProperty("values");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("3", values.GetProperty("param.days").GetString());
            Assert.False(values.TryGetProperty("param.costCenter", out _));

            // 没声明 visibleTo 的参数对所有人都可见（兼容性底线）
            Assert.Equal("可见性演示", values.GetProperty("param.note").GetString());
        }

        // ② 审批人（当前环节的办理人）：反过来
        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances/{instanceId}", secret, "manager"))
        {
            var body = await ReadJsonAsync(response);
            var values = body.GetProperty("parameters").GetProperty("values");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("HR-01", values.GetProperty("param.costCenter").GetString());
            Assert.False(values.TryGetProperty("param.days", out _));
        }

        // ③ 管理视角：都可见
        var adminSecret = await RegisterAsync(
            client,
            "param-admin",
            [
                PlatformScopes.ProcessRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.InstanceStart,
                PlatformScopes.TaskSubmitOnBehalf,
                PlatformScopes.HistoryReadAll,
            ]);

        using var adminResponse = await SendGetAsync(
            client, $"/workflow/v1/instances/{instanceId}", adminSecret, "zhaoliu");
        var adminBody = await ReadJsonAsync(adminResponse);
        var adminValues = adminBody.GetProperty("parameters").GetProperty("values");

        Assert.Equal("3", adminValues.GetProperty("param.days").GetString());
        Assert.Equal("HR-01", adminValues.GetProperty("param.costCenter").GetString());
    }

    /// <summary>不写扩展的老流程：参数照样全部可见（不为新能力付兼容税）。</summary>
    [Fact]
    public async Task Parameters_ShouldStayVisible_WithoutOverrides()
    {
        using var host = BuildHost();
        await DeployLegacyAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "param-legacy",
            [
                PlatformScopes.ProcessRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.InstanceStart,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        var instanceId = await StartAsync(
            client,
            secret,
            "zhangsan",
            new
            {
                processKey = LegacyKey,
                parameters = new Dictionary<string, string?> { ["days"] = "5" },
            });

        using var response = await SendGetAsync(
            client, $"/workflow/v1/instances/{instanceId}", secret, "lisi");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var values = body.GetProperty("parameters").GetProperty("values");

        Assert.Equal("5", values.GetProperty("param.days").GetString());
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

    /// <summary>
    /// 带 <c>netbpm:parameters</c> 的流程：<c>days</c> 覆盖 formProperty，
    /// <c>costCenter</c> 只在扩展里声明（BPMN 环节上没有它的表单字段）。
    /// </summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host, string processKey)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              xmlns:netbpm="http://netbpm.org/schema/bpmn-extensions"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{processKey}}" name="参数覆盖演示" isExecutable="true">
                <bpmn:extensionElements>
                  <netbpm:parameters>
                    <netbpm:parameter name="days" type="long" required="true"
                                      apiName="param.days"
                                      writableBy="APPLICANT" visibleTo="APPLICANT"
                                      min="1" max="365" />
                    <netbpm:parameter name="costCenter" type="string"
                                      visibleTo="APPROVER,ADMIN"
                                      pattern="^[A-Z]{2,4}-\d{2}$" />
                    <netbpm:parameter name="note" type="string" />
                  </netbpm:parameters>
                </bpmn:extensionElements>
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="days" name="天数" type="string" />
                    <activiti:formProperty id="note" name="备注" type="string" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);
    }

    /// <summary>不写平台扩展的老流程（只有 formProperty）。</summary>
    private static async Task DeployLegacyAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{LegacyKey}}" name="老参数演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="days" name="天数" type="long" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, LegacyKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(
        HttpClient client,
        string secret,
        string subject,
        object body)
    {
        using var response = await SendPostAsync(client, "/workflow/v1/instances", secret, subject, body);
        var started = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return started.GetProperty("instanceId").GetString()!;
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
