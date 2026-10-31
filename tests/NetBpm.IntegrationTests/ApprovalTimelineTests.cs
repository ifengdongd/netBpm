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
/// 审批记录时间线（<c>GET /workflow/v1/instances/{id}/history</c>）端到端：
/// 发起写 <c>INSTANCE_STARTED</c>、提交写 <c>TASK_COMPLETED</c>、增量拉取与分页、
/// 以及「未知实例 404 / 缺 scope 403」两条闸门。
/// </summary>
/// <remarks>
/// <para>
/// 用<b>两环节</b>流程（申请 → 审批）：内存宿主历史关闭，实例一旦结束就查不到视图，
/// 而本组用例要验证的正是「还在运行的实例」上的时间线。
/// </para>
/// <para>
/// 时间线条目的序号是<b>实例内单调</b>的（1、2、3…），所以断言可以直接比对字面值——
/// 这也是调用方 <c>?afterSequence=</c> 增量拉取能成立的前提。
/// </para>
/// </remarks>
public sealed class ApprovalTimelineTests
{
    private const string DefaultTenant = "default";
    private const string ProcessKey = "timeline_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_ShouldRecordInstanceStarted_AfterStart()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.InstanceStart, PlatformScopes.HistoryRead, PlatformScopes.InstanceRead]);

        var instanceId = await StartAsync(client, secret);

        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}/history", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal(ProcessKey, body.GetProperty("processKey").GetString());
        Assert.Equal("RUNNING", body.GetProperty("status").GetString());

        var entries = body.GetProperty("entries").EnumerateArray().ToList();

        Assert.Single(entries);
        Assert.Equal(1, entries[0].GetProperty("sequence").GetInt64());
        Assert.Equal("INSTANCE_STARTED", entries[0].GetProperty("kind").GetString());

        // 本次发起用的是纯服务令牌（没有 X-Subject-Id）：actorId 必须为空，
        // 而不是拿客户端名字顶上——合规报表要把「系统自己发起的」单列出来。
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("actorId").ValueKind);
        Assert.False(
            string.IsNullOrEmpty(entries[0].GetProperty("actorCallerId").GetString()),
            "INSTANCE_STARTED 缺少 actorCallerId（调用系统）。");

        // 运行中的实例没有结论（三级回落只对结束实例生效）
        Assert.Equal(JsonValueKind.Null, body.GetProperty("conclusion").ValueKind);
    }

    [Fact]
    public async Task History_ShouldAppendTaskCompleted_OnSubmit()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.InstanceStart, PlatformScopes.InstanceRead, PlatformScopes.TaskRead,
             PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.HistoryRead]);

        var instanceId = await StartAsync(client, secret);
        var taskId = await FindFirstTaskAsync(host, instanceId);

        using var submit = await SendSubmitAsync(
            client,
            taskId,
            secret,
            subject: "zhangsan",
            new { optionId = "1", comment = "同意" });
        var submitted = await ReadJsonAsync(submit);

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        // 提交结果直接带回本次新增的时间线，调用方不需要再拉一次
        var delta = submitted.GetProperty("timelineDelta").EnumerateArray().ToList();

        Assert.Single(delta);
        Assert.Equal(2, delta[0].GetProperty("sequence").GetInt64());
        Assert.Equal("TASK_COMPLETED", delta[0].GetProperty("kind").GetString());
        Assert.Equal("task_apply", delta[0].GetProperty("activityId").GetString());
        Assert.Equal("1", delta[0].GetProperty("optionId").GetString());
        Assert.Equal("APPROVE", delta[0].GetProperty("semantic").GetString());
        Assert.Equal("同意", delta[0].GetProperty("comment").GetString());
        Assert.Equal("zhangsan", delta[0].GetProperty("actorId").GetString());

        // 双身份：提交它的系统也要记下来（故障定位与合规报表都要用）
        Assert.False(
            string.IsNullOrEmpty(delta[0].GetProperty("actorCallerId").GetString()),
            "TASK_COMPLETED 缺少 actorCallerId（调用系统）。");

        // 再从历史端点读一遍：两条按序号升序
        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}/history", secret);
        var body = await ReadJsonAsync(response);
        var entries = body.GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Equal("INSTANCE_STARTED", entries[0].GetProperty("kind").GetString());
        Assert.Equal("TASK_COMPLETED", entries[1].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task History_ShouldSupportIncrementalPull_ByAfterSequence()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.InstanceStart, PlatformScopes.InstanceRead, PlatformScopes.TaskRead,
             PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.HistoryRead]);

        var instanceId = await StartAsync(client, secret);
        var taskId = await FindFirstTaskAsync(host, instanceId);

        using var submit = await SendSubmitAsync(
            client,
            taskId,
            secret,
            subject: "zhangsan",
            new { optionId = "1" });
        submit.EnsureSuccessStatusCode();

        // afterSequence=1：只要第 2 条，不重复返回已经拿过的发起记录
        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history?afterSequence=1",
            secret);
        var body = await ReadJsonAsync(response);
        var entries = body.GetProperty("entries").EnumerateArray().ToList();

        Assert.Single(entries);
        Assert.Equal(2, entries[0].GetProperty("sequence").GetInt64());
        Assert.Equal("TASK_COMPLETED", entries[0].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task History_ShouldPage_AndReturnNextCursor()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client,
            "oa-portal",
            [PlatformScopes.InstanceStart, PlatformScopes.InstanceRead, PlatformScopes.TaskRead,
             PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.HistoryRead]);

        var instanceId = await StartAsync(client, secret);
        var taskId = await FindFirstTaskAsync(host, instanceId);

        using var submit = await SendSubmitAsync(
            client,
            taskId,
            secret,
            subject: "zhangsan",
            new { optionId = "1" });
        submit.EnsureSuccessStatusCode();

        // 第一页取 1 条：必须有 nextCursor（= 本页最后一条的序号）
        using var first = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history?limit=1",
            secret);
        var firstBody = await ReadJsonAsync(first);
        var firstEntries = firstBody.GetProperty("entries").EnumerateArray().ToList();

        Assert.Single(firstEntries);
        Assert.Equal(1, firstEntries[0].GetProperty("sequence").GetInt64());
        Assert.Equal("1", firstBody.GetProperty("nextCursor").GetString());

        // 顺着游标取第二页：不重不漏，且没有下一页
        using var second = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history?limit=1&afterSequence=1",
            secret);
        var secondBody = await ReadJsonAsync(second);
        var secondEntries = secondBody.GetProperty("entries").EnumerateArray().ToList();

        Assert.Single(secondEntries);
        Assert.Equal(2, secondEntries[0].GetProperty("sequence").GetInt64());
        Assert.Equal(JsonValueKind.Null, secondBody.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task History_Should404_WhenInstanceUnknown()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.HistoryRead]);

        using var response = await SendGetAsync(
            client,
            "/workflow/v1/instances/no_such_instance/history",
            secret);
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.InstanceNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task History_Should403_WhenMissingScope()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();

        // 只有实例读权限，没有历史读权限 → 403（scope 是能力上限）
        var secret = await RegisterAsync(client, "read-only", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(
            client,
            "/workflow/v1/instances/whatever/history",
            secret);
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
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
    /// 部署两环节流程：申请（assignee=zhangsan）→ 审批（assignee=manager）。
    /// 两个环节都带 enum 选项，否则提交时选项集为空会 422（不是本组用例要测的东西）。
    /// </summary>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="时间线演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="zhangsan">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
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
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
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

    /// <summary>走平台发起端点（只有它会把 <c>INSTANCE_STARTED</c> 写进时间线）。</summary>
    private static async Task<string> StartAsync(HttpClient client, string secret)
    {
        using var response = await SendPostAsync(
            client,
            "/workflow/v1/instances",
            secret,
            subject: null,
            new { processKey = ProcessKey });

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return body.GetProperty("instanceId").GetString()!;
    }

    private static async Task<string> FindFirstTaskAsync(WebApplicationFactory<Program> host, string instanceId)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var task = Assert.Single(tasks);

        Assert.Equal("task_apply", task.TaskDefinitionKey);

        return task.Id;
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

    private static Task<HttpResponseMessage> SendGetAsync(HttpClient client, string path, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

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

    private static Task<HttpResponseMessage> SendSubmitAsync(
        HttpClient client,
        string taskId,
        string secret,
        string? subject,
        object body) =>
        SendPostAsync(client, $"/workflow/v1/tasks/{taskId}/submit", secret, subject, body);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
