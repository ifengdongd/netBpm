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
/// 发起流程（<c>POST /workflow/v1/instances</c>）端到端：验证闸门（存在性 / scope）、
/// 参数镜像与校验（<c>param.</c> 剥前缀 / 保留名冲突 / 必填 / 类型）、业务主键发号，
/// 以及成功后返回的实例视图（<see cref="InstanceView"/> 单一出口）。
/// </summary>
/// <remarks>
/// 跑在内存模式宿主（历史关闭）。发起流程停在一个 userTask 上，实例仍在运行，
/// 因此 <see cref="StartInstanceResult.Instance"/> 能完整组装。
/// </remarks>
public sealed class InstanceStartTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "start_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_ShouldCreateInstance_WithViewAndMirroredParameters()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.InstanceRead,
            PlatformScopes.TaskRead, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = ProcessKey,
            parameters = new Dictionary<string, string?> { ["param.days"] = "3", ["param.reason"] = "年假" },
        });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var instanceId = body.GetProperty("instanceId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instanceId));

        // 未传 businessKey → 服务端发号，返回非空
        var businessKey = body.GetProperty("businessKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(businessKey));

        // 发起后的完整实例视图（InstanceView 单一出口）
        var instance = body.GetProperty("instance");
        Assert.Equal("RUNNING", instance.GetProperty("status").GetString());
        Assert.Equal("zhangsan", instance.GetProperty("startedBy").GetString());
        Assert.Equal(ProcessKey, instance.GetProperty("processKey").GetString());

        var tasks = instance.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Single(tasks);
        Assert.Equal("task_approve", tasks[0].GetProperty("activityId").GetString());

        // 参数镜像：对外键 param.days 剥前缀后以裸名落引擎变量
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var variables = await engine.GetVariablesAsync(instanceId!, Ct);

        Assert.Equal("3", variables["days"]);
        Assert.Equal("年假", variables["reason"]);
        Assert.False(variables.ContainsKey("param.days"));
    }

    [Fact]
    public async Task Start_ShouldUseProvidedBusinessKey()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.InstanceRead, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = ProcessKey,
            businessKey = "BILL-2026-0001",
            parameters = new Dictionary<string, string?> { ["param.days"] = "1" },
        });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("BILL-2026-0001", body.GetProperty("businessKey").GetString());
        Assert.Equal("BILL-2026-0001", body.GetProperty("instance").GetProperty("businessKey").GetString());
    }

    [Fact]
    public async Task Start_Should404_ForUnknownProcess()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = "no_such_process",
        });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.ProcessNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Start_Should422_WhenRequiredParameterMissing()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = ProcessKey,
            parameters = new Dictionary<string, string?> { ["param.reason"] = "忘了填天数" },
        });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterRequiredMissing, problem.GetProperty("code").GetString());

        // 校验失败发生在推进流程之前：不产生任何运行中实例
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var instances = await engine.FindProcessInstancesAsync(new ProcessInstanceQuery(), Ct);

        Assert.Empty(instances);
    }

    [Fact]
    public async Task Start_Should400_WhenParameterConflictsWithReserved()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = ProcessKey,
            parameters = new Dictionary<string, string?> { ["param.applyUser"] = "wangwu" },
        });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterConflictWithReserved, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Start_Should422_WhenParameterTypeInvalid()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [
            PlatformScopes.InstanceStart, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendStartAsync(client, secret, subject: "zhangsan", new
        {
            processKey = ProcessKey,
            parameters = new Dictionary<string, string?> { ["param.days"] = "abc" },
        });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterTypeInvalid, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Start_Should403_WithoutStartScope()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();

        // 只授予 instance:read，不授予 instance:start → 403（scope 是能力上限）。
        // 不声明 subject：避免触发中间件「声明操作人需 onbehalf」的 403，测的是端点自身的 scope 闸门。
        var secret = await RegisterAsync(client, "read-only", [PlatformScopes.InstanceRead]);

        using var response = await SendStartAsync(client, secret, subject: null, new
        {
            processKey = ProcessKey,
        });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 业务主键超过列宽（255）必须是 400，而不是落到数据库后变成 503。
    /// </summary>
    /// <remarks>
    /// 不挡的话，驱动抛出的 <c>DbException</c> 会被全局异常处理器映射成「数据库暂时不可用」，
    /// 调用方会去查库、查连接池，而真正原因是自己拼了个超长单号。
    /// </remarks>
    [Fact]
    public async Task Start_Should400_ForOverlongBusinessKey()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.InstanceStart]);

        using var response = await SendStartAsync(client, secret, subject: null, new
        {
            processKey = ProcessKey,
            businessKey = new string('x', 256),
        });

        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
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
    /// 部署一个带参数声明的审批流程：startEvent → 审批 userTask → endEvent。
    /// 参数 schema 由 formProperty 派生：<c>param.days</c>（long，必填）、<c>param.reason</c>（string，可选）。
    /// </summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="发起演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="lisi">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="days" name="请假天数" type="long" required="true" variable="days" />
                    <activiti:formProperty id="reason" name="请假事由" type="string" variable="reason" />
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
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string name,
        string[] scopes)
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

    private static Task<HttpResponseMessage> SendStartAsync(
        HttpClient client,
        string secret,
        string? subject,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/workflow/v1/instances", UriKind.Relative));
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
