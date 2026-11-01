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
/// 只读端点的共用夹具：一条带表单字段、附件与流程图的「申请 → 审批」流程。
/// </summary>
/// <remarks>
/// 与写动作夹具的区别只有一处：这里的 BPMN 带 <c>activiti:formProperty</c>（表单快照只在有字段时才会写）
/// 与 <c>bpmndi</c> 图形信息（没有图形信息就渲染不出图，流程图画不出来这件事不等于接口坏了）。
/// </remarks>
internal static class ReadEndpointHarness
{
    internal const string DefaultTenant = "default";
    internal const string ProcessKey = "read_demo";

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>宿主：与写动作夹具同一套开关（内存执行树、关历史、关限流、无凭据）。</summary>
    internal static WebApplicationFactory<Program> BuildHost() =>
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
    /// 真实数据库宿主：只有它才记 <c>ACT_HI_ACTINST</c>，也就只有它能验「走过的节点标红」。
    /// </summary>
    /// <remarks>
    /// 内存宿主（<see cref="BuildHost"/>）没有历史存储，图只能降级成「只标当前环节」——
    /// 两条用例分别钉住这两种形态，避免哪天降级路径挂掉却没人发现。
    /// </remarks>
    internal static WebApplicationFactory<Program> BuildDatabaseHost() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("NetBpm:DatabaseType", "MySql");
            builder.UseSetting("NetBpm:ConnectionString", MySqlTestDatabase.ConnectionString);
            builder.UseSetting("NetBpm:Security:RequireCredential", "false");
            builder.UseSetting("NetBpm:UseInMemoryExecutionTree", "false");
            builder.UseSetting("NetBpm:EnableHistory", "true");
            builder.UseSetting("NetBpm:ValidateSchemaOnStartup", "false");
            builder.UseSetting("NetBpm:AutoMigrate", "false");
            builder.UseSetting("NetBpm:Timer:Enabled", "false");
            builder.UseSetting("NetBpm:Jobs:Enabled", "false");
            builder.UseSetting("NetBpm:RateLimit:Enabled", "false");
            builder.UseSetting("NetBpm:Security:Tenants:Default", DefaultTenant);
        });

    internal static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                              xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                              xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="只读端点演示" isExecutable="true">
                <bpmn:startEvent id="start" name="开始" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:candidateUsers="lisi,wangwu">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
                    <activiti:formProperty id="days" name="请假天数" type="long" variable="days" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批意见" type="string" variable="comment" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="结束" />
              </bpmn:process>
              <bpmndi:BPMNDiagram id="Diagram_read_demo">
                <bpmndi:BPMNPlane id="Plane_read_demo" bpmnElement="{{ProcessKey}}">
                  <bpmndi:BPMNShape id="shape_start" bpmnElement="start">
                    <dc:Bounds x="60" y="120" width="36" height="36" />
                  </bpmndi:BPMNShape>
                  <bpmndi:BPMNShape id="shape_apply" bpmnElement="task_apply">
                    <dc:Bounds x="150" y="98" width="120" height="80" />
                  </bpmndi:BPMNShape>
                  <bpmndi:BPMNShape id="shape_approve" bpmnElement="task_approve">
                    <dc:Bounds x="330" y="98" width="120" height="80" />
                  </bpmndi:BPMNShape>
                  <bpmndi:BPMNShape id="shape_end" bpmnElement="end">
                    <dc:Bounds x="510" y="120" width="36" height="36" />
                  </bpmndi:BPMNShape>
                  <bpmndi:BPMNEdge id="edge_flow1" bpmnElement="flow1">
                    <di:waypoint x="96" y="138" />
                    <di:waypoint x="150" y="138" />
                  </bpmndi:BPMNEdge>
                  <bpmndi:BPMNEdge id="edge_flow2" bpmnElement="flow2">
                    <di:waypoint x="270" y="138" />
                    <di:waypoint x="330" y="138" />
                  </bpmndi:BPMNEdge>
                  <bpmndi:BPMNEdge id="edge_flow3" bpmnElement="flow3">
                    <di:waypoint x="450" y="138" />
                    <di:waypoint x="510" y="138" />
                  </bpmndi:BPMNEdge>
                </bpmndi:BPMNPlane>
              </bpmndi:BPMNDiagram>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    /// <summary>发起实例（发起人 zhangsan），返回实例 id 与停在申请环节的任务 id。</summary>
    internal static async Task<(string InstanceId, string TaskId)> StartAsync(
        WebApplicationFactory<Program> host,
        IDictionary<string, object?>? variables = null)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "zhangsan",
            variables: variables,
            ct: Ct);

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);

        return (instanceId, Assert.Single(tasks).Id);
    }

    internal static async Task<string> RegisterAsync(HttpClient client, string name, string[] scopes)
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

    internal static Task<HttpResponseMessage> GetAsync(
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

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}

/// <summary>
/// 流程表单规范（<c>GET /workflow/v1/processes/{key}/form/schema</c>）。
/// </summary>
/// <remarks>
/// 这组用例守的是<b>两件容易退化的事</b>：① 审批结论字段不能混进表单字段（它走 options）；
/// ② 没有字段的环节不能出现（空集合会被读成「有表单但是空的」）。
/// </remarks>
public sealed class ProcessFormSchemaReadTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.ProcessRead,
        PlatformScopes.TaskRead,
    ];

    [Fact]
    public async Task FormSchema_ShouldListActivityFields_WithoutApprovalField()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/processes/{ReadEndpointHarness.ProcessKey}/form/schema",
            secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ReadEndpointHarness.ProcessKey, body.GetProperty("processKey").GetString());
        Assert.Equal(1, body.GetProperty("definitionVersion").GetInt32());

        var activities = body.GetProperty("activities").EnumerateArray().ToList();

        // 两个环节都有字段，所以两个都在
        Assert.Equal(2, activities.Count);
        Assert.Equal("task_apply", activities[0].GetProperty("activityId").GetString());

        var fields = activities[0].GetProperty("fields").EnumerateArray().ToList();
        var fieldIds = fields.Select(field => field.GetProperty("fieldId").GetString()).ToArray();

        Assert.Equal(["comment", "days"], fieldIds);

        // 审批结论字段是「按钮的数据源」，不是表单字段
        Assert.DoesNotContain("approvalResult", fieldIds);

        var days = fields.Single(field => field.GetProperty("fieldId").GetString() == "days");

        Assert.Equal("LONG", days.GetProperty("type").GetString());
        Assert.Equal("请假天数", days.GetProperty("label").GetString());
    }

    [Fact]
    public async Task FormSchema_Should422_WhenProcessUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/workflow/v1/processes/no_such_process/form/schema",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// 附件引用（<c>GET /workflow/v1/tasks/{id}/attachments</c>）。
/// </summary>
/// <remarks>
/// 平台不做附件存储，只回放「当时提交了哪些材料」。因此本组用例的重点是
/// <b>快照有没有把它冻住</b>，而不是文件本身能不能下载。
/// </remarks>
public sealed class TaskAttachmentReadTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.TaskRead,
        PlatformScopes.TaskSubmit,
        PlatformScopes.TaskSubmitOnBehalf,
        PlatformScopes.InstanceRead,
    ];

    [Fact]
    public async Task Attachments_Should404_WhenTaskNeverSubmitted()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await ReadEndpointHarness.StartAsync(host);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/attachments",
            secret,
            subject: "lisi");

        // 没有快照就没有「当时的材料」这回事；回 200 + 空数组会被读成「提交过但没传附件」
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(ErrorCodes.TaskNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Attachments_ShouldReturnCapturedRefs_AfterSubmit()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await ReadEndpointHarness.StartAsync(host);

        using var submit = await SendSubmitAsync(client, taskId, secret);
        var submitBody = await ReadEndpointHarness.ReadJsonAsync(submit);

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var nextTasks = submitBody.GetProperty("nextTasks").EnumerateArray().ToList();

        Assert.Single(nextTasks);
        Assert.Equal("task_approve", nextTasks[0].GetProperty("activityId").GetString());

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/attachments",
            secret,
            subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var attachments = (await ReadEndpointHarness.ReadJsonAsync(response)).EnumerateArray().ToList();

        var attachment = Assert.Single(attachments);

        Assert.Equal("请假单.pdf", attachment.GetProperty("name").GetString());
        Assert.Equal("https://files.example.com/leave/123.pdf", attachment.GetProperty("url").GetString());
        Assert.Equal(2048, attachment.GetProperty("size").GetInt64());
        Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
    }

    private static Task<HttpResponseMessage> SendSubmitAsync(HttpClient client, string taskId, string secret)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/workflow/v1/tasks/{taskId}/submit", UriKind.Relative));

        request.Headers.Add("X-Api-Key", secret);
        request.Headers.Add("X-Subject-Id", "lisi");
        request.Content = JsonContent.Create(new
        {
            optionId = "1",
            comment = "同意",
            formValues = new Dictionary<string, string?> { ["days"] = "3" },
            attachments = new[]
            {
                new
                {
                    name = "请假单.pdf",
                    url = "https://files.example.com/leave/123.pdf",
                    size = 2048,
                    contentType = "application/pdf",
                    sha256 = "9f2c",
                },
            },
        });

        return client.SendAsync(request, ReadEndpointHarness.Ct);
    }
}

/// <summary>
/// 实例参数面板（<c>GET /workflow/v1/instances/{id}/parameters</c>）。
/// </summary>
/// <remarks>
/// 守两件事：① 声明过的参数即使还没填也要出现（否则调用方渲染不出空输入框）；
/// ② <b>引擎里有值但规范里没声明</b>的变量也要出现并标 <c>declared=false</c>——
/// 只列声明等于对历史遗留变量失明。
/// </remarks>
public sealed class InstanceParameterReadTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.InstanceRead,
        PlatformScopes.TaskRead,
        // 开放模式下带 X-Subject-Id 等同于代客调用，必须显式持有 onbehalf
        PlatformScopes.TaskSubmitOnBehalf,
    ];

    [Fact]
    public async Task Parameters_ShouldMergeDeclaredAndUndeclared()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);

        var (instanceId, _) = await ReadEndpointHarness.StartAsync(
            host,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["days"] = 3,
                ["legacyFlag"] = "legacy",
            });

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/parameters",
            secret,
            subject: "zhangsan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadEndpointHarness.ReadJsonAsync(response);

        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal(ReadEndpointHarness.ProcessKey, body.GetProperty("processKey").GetString());

        var rows = body.GetProperty("parameters").EnumerateArray().ToList();

        var days = rows.Single(row => row.GetProperty("apiName").GetString() == "param.days");

        Assert.True(days.GetProperty("declared").GetBoolean());
        Assert.Equal("3", days.GetProperty("value").GetString());
        Assert.Equal("请假天数", days.GetProperty("displayName").GetString());

        // 声明过但没填：必须出现，值为 null（调用方据此渲染空输入框）
        var comment = rows.Single(row => row.GetProperty("apiName").GetString() == "param.comment");

        Assert.True(comment.GetProperty("declared").GetBoolean());
        Assert.Equal(JsonValueKind.Null, comment.GetProperty("value").ValueKind);

        // 规范里没声明但引擎里存在的变量：补一行，且排在声明之后
        var legacy = rows.Single(row => row.GetProperty("apiName").GetString() == "param.legacyFlag");

        Assert.False(legacy.GetProperty("declared").GetBoolean());
        Assert.Equal("legacy", legacy.GetProperty("value").GetString());
        Assert.Equal(rows.Count - 1, rows.IndexOf(legacy));
    }

    [Fact]
    public async Task Parameters_Should404_WhenInstanceUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/workflow/v1/instances/no-such-instance/parameters",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>
/// 实例流转状态图（<c>GET /workflow/v1/instances/{id}/diagram.svg</c>）。
/// </summary>
/// <remarks>
/// 断言落在<b>着色</b>上而不是「有没有 SVG」：一张不带任何标记的流程图与流程定义图没有区别，
/// 那正是这个端点不该退化成的东西。
/// </remarks>
public sealed class InstanceDiagramReadTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.InstanceRead,
        PlatformScopes.TaskRead,
        // 开放模式下带 X-Subject-Id 等同于代客调用，必须显式持有 onbehalf
        PlatformScopes.TaskSubmitOnBehalf,
    ];

    [Fact]
    public async Task Diagram_ShouldHighlightCurrentActivity_WhenHistoryDisabled()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await ReadEndpointHarness.StartAsync(host);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/diagram.svg",
            secret,
            subject: "zhangsan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);

        var svg = await response.Content.ReadAsStringAsync(ReadEndpointHarness.Ct);

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("申请", svg, StringComparison.Ordinal);

        // 内存宿主没开历史 → 走过哪些节点无从得知，但「现在停在哪」仍然答得出来：
        // 当前环节加粗 + 浅红填充（#fee2e2）。这是降级而不是失败。
        Assert.Contains("#fee2e2", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagram_ShouldHighlightVisitedActivities_OnRealDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var host = ReadEndpointHarness.BuildDatabaseHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await ReadEndpointHarness.StartAsync(host);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/diagram.svg",
            secret,
            subject: "zhangsan");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var svg = await response.Content.ReadAsStringAsync(ReadEndpointHarness.Ct);

        // 有历史（ACT_HI_ACTINST）时走过的节点与连线都要标红：
        // 开始事件走过 → #fef2f2；申请环节正停着 → #fee2e2
        Assert.Contains("#fef2f2", svg, StringComparison.Ordinal);
        Assert.Contains("#fee2e2", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagram_Should404_WhenInstanceUnknown()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "oa-portal", Scopes);

        using var response = await ReadEndpointHarness.GetAsync(
            client,
            "/workflow/v1/instances/no-such-instance/diagram.svg",
            secret);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
