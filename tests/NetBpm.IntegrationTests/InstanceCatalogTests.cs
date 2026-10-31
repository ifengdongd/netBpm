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
/// 实例读模型（<c>/workflow/v1/instances/{id}</c>）端到端：发起实例 → 查实例视图，
/// 验证 <c>status</c> / <c>currentActivityIds</c> / <c>tasks</c> 由引擎执行树 + 待办读模型组装。
/// </summary>
/// <remarks>
/// 跑在内存模式宿主（历史关闭）。因此本类只覆盖<b>运行中</b>实例的视图组装与 404 口径；
/// 结束实例的状态与结论推导（依赖 <c>ACT_HI_*</c>）在 <see cref="InstanceConclusionTests"/>
/// 里用 MySQL 宿主覆盖。
/// </remarks>
public sealed class InstanceCatalogTests
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "instance_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InstanceView_ShouldReportRunning_WithCurrentActivityAndTasks()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.InstanceRead, PlatformScopes.TaskRead, PlatformScopes.TaskSubmitOnBehalf]);

        var instanceId = await StartAsync(host);

        using var response = await SendAsync(client, $"/workflow/v1/instances/{instanceId}", secret, subject: "lisi");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal(ApprovalKey, body.GetProperty("processKey").GetString());
        Assert.Equal("RUNNING", body.GetProperty("status").GetString());

        // 当前停留在审批环节
        var activities = body.GetProperty("currentActivityIds").EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();

        Assert.Contains("task_approve", activities);

        // 待办由待办读模型组装（与 GET /tasks/{id} 同口径）
        var tasks = body.GetProperty("tasks").EnumerateArray().ToList();

        Assert.Single(tasks);
        Assert.Equal("task_approve", tasks[0].GetProperty("activityId").GetString());

        // 运行中实例没有结论
        Assert.Equal(JsonValueKind.Null, body.GetProperty("conclusion").ValueKind);
    }

    [Fact]
    public async Task InstanceView_Should404_ForUnknownOrUnauthorized()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.InstanceRead, PlatformScopes.TaskSubmitOnBehalf]);

        // 不存在的实例 → 404（与 ProcessNotFound / TaskNotFound 同口径，不泄露存在性）
        using var response = await SendAsync(client, "/workflow/v1/instances/nonexistent-instance", secret, subject: "lisi");
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.InstanceNotFound, problem.GetProperty("code").GetString());
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

    /// <summary>部署一个极简审批流程（startEvent → 审批 userTask → endEvent）。</summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="实例演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="${approver}">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
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
        await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        return await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["approver"] = "lisi" },
            ct: Ct);
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

    private static Task<HttpResponseMessage> SendAsync(
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
