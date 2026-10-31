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
/// 引擎原语面（<c>/api/runtime</c> / <c>/api/identity</c> / <c>/api/designer</c>）的 scope 收口。
/// </summary>
/// <remarks>
/// <para>
/// 收紧前引擎面只做租户隔离：一个只被授予 <c>workflow:instance:read</c> 的接入方，
/// 拿自己的凭据就能 <c>POST /api/runtime/tasks/{id}/complete</c> 把审批办掉 ——
/// 没有时间线、没有表单快照、没有 <c>optionId</c> 校验。本类锁住三件事：
/// </para>
/// <list type="number">
///   <item><description><b>缺 scope 就 403</b>：写原语不能靠「能读就能写」越过能力上限；</description></item>
///   <item><description><b>有 scope 就通</b>：包括 <c>workflow:admin:*</c> 对引擎管理动作的翻译
///     （客户端拿不到 <c>netbpm.admin.*</c>，不翻译等于管理动作全废）；</description></item>
///   <item><description><b>顺序不变</b>：先 404（不存在 / 跨租户）再 403（缺 scope），
///     否则跨租户探测会从「不存在」变成「无权限」，泄露 id 是否存在；</description></item>
///   <item><description><b>业务写入一律不给客户端</b>：发起 / 办理 / 批量办理与 scope 无关地 403
///     （原语面不写审批结论），必须走 <c>/workflow/v1</c>。</description></item>
/// </list>
/// <para>
/// 另有一条反向护栏：<b>未启用凭据的开发模式与旧 API Key 不受影响</b>。
/// 这是「只对注册客户端生效」的口径 —— 不这么做，一次性收紧会打断全部既有调用方。
/// </para>
/// </remarks>
public sealed class EngineSurfaceScopeTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "engine_scope_demo";
    private const string LegacyApiKey = "netbpm-legacy-engine-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 办理是<b>业务写入</b>：引擎原语面的 <c>complete</c> 不校验 <c>optionId</c>、
    /// 不写审批时间线与表单快照，办结后无法回答「批了什么」。
    /// 所以注册客户端一律改走平台面 —— <b>有没有 <c>workflow:task:submit</c> 都一样</b>。
    /// </summary>
    [Fact]
    public async Task Complete_Should403_ForClient_EvenWithTaskSubmit()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-complete-closed",
            [PlatformScopes.InstanceRead, PlatformScopes.TaskRead, PlatformScopes.TaskSubmit]);

        var (instanceId, taskId) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/tasks/{taskId}/complete",
            secret,
            "lisi");

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // 文案必须指出替代入口：只说「没权限」等于让接入方在引擎面上反复试 scope
        Assert.Contains(
            "/workflow/v1/tasks/{taskId}/submit",
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        // 拒绝必须发生在「办掉」之前：任务还在，实例还在跑
        Assert.True(await TaskExistsAsync(host, instanceId, taskId));
    }

    /// <summary>批量办理同属业务写入，同样不对客户端开放。</summary>
    [Fact]
    public async Task BatchComplete_Should403_ForClient_EvenWithTaskSubmit()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-batch-complete-closed",
            [PlatformScopes.InstanceRead, PlatformScopes.TaskRead, PlatformScopes.TaskSubmit]);

        var (_, taskId) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/tasks/batch/complete",
            secret,
            "lisi",
            new { taskIds = new[] { taskId } });

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Contains(
            "/workflow/v1/tasks/{taskId}/submit",
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 发起实例同属业务写入：原语面的 start 不写 <c>INSTANCE_STARTED</c> 时间线、
    /// 不过参数 schema 与 <c>writableBy</c>，客户端必须走 <c>POST /workflow/v1/instances</c>。
    /// </summary>
    [Fact]
    public async Task StartInstance_Should403_ForClient_EvenWithInstanceStart()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-start-closed",
            [PlatformScopes.InstanceRead, PlatformScopes.InstanceStart]);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/runtime/process-instances",
            secret,
            "lisi",
            new { processDefinitionKey = ProcessKey });

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        Assert.Contains(
            "/workflow/v1/instances",
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        // 拒绝必须发生在「发起」之前：不能先落实例再 403
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var instances = await engine.FindProcessInstancesAsync(
            new Runtime.ProcessInstanceQuery(ProcessDefinitionKey: ProcessKey),
            Ct);

        Assert.Empty(instances);
    }

    /// <summary>
    /// 引擎管理动作按 <c>workflow:admin:instance</c> 记账：
    /// 客户端拿不到 <c>netbpm.admin.instance.suspend</c>，靠这条翻译才能挂起实例。
    /// </summary>
    [Fact]
    public async Task Suspend_Should204_WhenClientHoldsAdminInstance()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-suspend-allowed",
            [PlatformScopes.InstanceRead, PlatformScopes.AdminInstance]);

        var (instanceId, _) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/process-instances/{instanceId}/suspend",
            secret,
            "ops");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Suspend_Should403_WhenClientLacksAdminInstance()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "engine-suspend-denied", [PlatformScopes.InstanceRead]);

        var (instanceId, _) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/runtime/process-instances/{instanceId}/suspend",
            secret,
            "ops");

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // 文案必须给平台话术：客户端按 netbpm.admin.* 去配只会一直 403
        Assert.Contains(
            PlatformScopes.AdminInstance,
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 直写引擎变量等于越过参数 schema 与 <c>writableBy</c>，只对持
    /// <c>workflow:admin:variables</c> 的客户端开放（默认不授予任何客户端）。
    /// </summary>
    [Fact]
    public async Task WriteVariables_Should403_WhenClientLacksAdminVariables()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-vars-denied",
            [PlatformScopes.InstanceRead, PlatformScopes.InstanceStart]);

        var (instanceId, _) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/runtime/process-instances/{instanceId}/variables",
            secret,
            "lisi",
            new Dictionary<string, string> { ["days"] = "7" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WriteVariables_Should204_WhenClientHoldsAdminVariables()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(
            client,
            "engine-vars-allowed",
            [PlatformScopes.InstanceRead, PlatformScopes.AdminVariables]);

        var (instanceId, _) = await StartAndFindTaskAsync(host);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"/api/runtime/process-instances/{instanceId}/variables",
            secret,
            "lisi",
            new Dictionary<string, string> { ["days"] = "7" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        Assert.Equal("7", variables["days"]);
    }

    [Fact]
    public async Task DesignerDeploy_Should403_WhenClientLacksAdminProcess()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "engine-deploy-denied", [PlatformScopes.ProcessRead]);

        using var response = await PostXmlAsync(
            client,
            "/api/designer/deploy",
            secret,
            "designer",
            Xml());

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PlatformScopes.AdminProcess, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>读定义只要 <c>workflow:process:read</c>：<c>netbpm.admin.model.read</c> 翻译过来就是它。</summary>
    [Fact]
    public async Task DesignerDefinitions_Should200_WhenClientHoldsProcessRead()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "engine-definitions-allowed", [PlatformScopes.ProcessRead]);

        using var response = await SendGetAsync(
            client,
            "/api/designer/definitions",
            secret,
            "reader");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Identity_Should403_WhenClientLacksTaskRead()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "engine-identity-denied", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(client, "/api/identity/users/lisi/groups", secret, "lisi");

        var error = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(PlatformScopes.TaskRead, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 顺序契约：不存在的实例先得到 404，而不是「缺 scope」的 403。
    /// 反过来就等于告诉调用方「这个 id 存在，只是你没权限」。
    /// </summary>
    [Fact]
    public async Task MissingInstance_Should404_Not403()
    {
        using var host = BuildHost();
        using var client = host.CreateClient();

        var secret = await RegisterAsync(client, "engine-missing-instance", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(
            client,
            "/api/runtime/process-instances/no-such-instance",
            secret,
            "lisi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 反向护栏：未启用凭据（开发 / 单机演示）与旧 API Key 不受这层 scope 影响。
    /// </summary>
    [Fact]
    public async Task Complete_ShouldStillWork_WhenCredentialNotRequired()
    {
        using var host = BuildHost();
        await DeployAsync(host);
        using var client = host.CreateClient();

        var (instanceId, taskId) = await StartAndFindTaskAsync(host);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/runtime/tasks/{taskId}/complete", UriKind.Relative));

        request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TaskExistsAsync(host, instanceId, taskId));
    }

    /// <summary>
    /// 反向护栏的另一半：旧 API Key（单值密钥代表「整个可信内网」）仍然能办理。
    /// 只对注册客户端收口的意义就在这里 —— 已上线的既有集成不会因为这次收紧而全线 403。
    /// </summary>
    [Fact]
    public async Task Complete_Should204_ForLegacyApiKey()
    {
        using var host = BuildHost(builder =>
            builder.UseSetting("NetBpm:Security:ApiKey", LegacyApiKey));

        await DeployAsync(host);
        using var client = host.CreateClient();

        var (instanceId, taskId) = await StartAndFindTaskAsync(host);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/api/runtime/tasks/{taskId}/complete", UriKind.Relative));

        request.Headers.Add("X-Api-Key", LegacyApiKey);
        request.Headers.Add("X-Subject-Id", "lisi");
        request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TaskExistsAsync(host, instanceId, taskId));
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static WebApplicationFactory<Program> BuildHost(Action<IWebHostBuilder>? extra = null) =>
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

            extra?.Invoke(builder);
        });

    private static string Xml() => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                          xmlns:activiti="http://activiti.org/bpmn"
                          targetNamespace="netbpm-test">
          <bpmn:process id="{{ProcessKey}}" name="引擎面 scope 演示" isExecutable="true">
            <bpmn:startEvent id="start" name="Start" />
            <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
            <bpmn:userTask id="task_apply" name="办理" activiti:assignee="lisi">
              <bpmn:incoming>flow1</bpmn:incoming>
              <bpmn:outgoing>flow2</bpmn:outgoing>
            </bpmn:userTask>
            <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="end" />
            <bpmn:endEvent id="end" name="End" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml()));

        // 必须落在客户端所属租户上：客户端的租户由注册记录钉死，实体租户不符会按「不存在」404，
        // 那样的用例根本走不到 scope 闸门
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", tenantId: DefaultTenant, ct: Ct);
    }

    private static async Task<(string InstanceId, string TaskId)> StartAndFindTaskAsync(
        WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "lisi",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = "3" },
            tenantId: DefaultTenant,
            ct: Ct);

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var task = Assert.Single(tasks);

        return (instanceId, task.Id);
    }

    private static async Task<bool> TaskExistsAsync(
        WebApplicationFactory<Program> host,
        string instanceId,
        string taskId)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);

        return tasks.Any(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
    {
        // 带 X-Subject-Id 的请求在鉴权中间件就要求 onbehalf（代客调用）；那是中间件的独立规则，
        // 与端点自身的 scope 闸门不相干，所以统一补上。
        var effective = scopes
            .Append(PlatformScopes.TaskSubmitOnBehalf)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new { name, tenantId = DefaultTenant, scopes = effective },
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
        string subject)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", subject);

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        string subject,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", subject);
        request.Content = JsonContent.Create(body ?? new { });

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> PostXmlAsync(
        HttpClient client,
        string path,
        string secret,
        string subject,
        string xml)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", subject);
        request.Content = new StringContent(xml, Encoding.UTF8, "text/xml");

        return client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
