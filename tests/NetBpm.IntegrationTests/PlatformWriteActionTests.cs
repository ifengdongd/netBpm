using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 平台面写动作的共用夹具：一条「申请（候选两个人）→ 审批（指定人）」的两环节流程。
/// </summary>
/// <remarks>
/// 第一个环节刻意用 <c>candidateUsers</c> 而不是 <c>assignee</c>：认领 / 转办测的正是
/// 「待办在候选池里时谁有权拿走它」，直接指定办理人就没有候选范围可测了。
/// </remarks>
internal static class WriteActionHarness
{
    internal const string DefaultTenant = "default";
    internal const string ProcessKey = "assign_demo";

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    /// <summary>部署两环节流程：申请（候选人 lisi / wangwu）→ 审批（办理人 manager）。</summary>
    internal static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ProcessKey}}" name="归属演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:candidateUsers="lisi,wangwu" />
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager" />
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ProcessKey + ".bpmn20.xml", ct: Ct);
    }

    /// <summary>发起实例（发起人 zhangsan），返回实例 id 与停在申请环节的任务 id。</summary>
    internal static async Task<(string InstanceId, string TaskId)> StartAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ProcessKey,
            startUserId: "zhangsan",
            variables: null,
            ct: Ct);

        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var task = Assert.Single(tasks);

        Assert.Null(task.Assignee);

        return (instanceId, task.Id);
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

    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        string? subject,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return client.SendAsync(request, Ct);
    }

    internal static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>读实例时间线的条目类型（按序号升序）。</summary>
    /// <summary>读实例视图（详情端点）。</summary>
    internal static async Task<HttpResponseMessage> GetInstanceAsync(
        HttpClient client,
        string secret,
        string instanceId,
        string? subject)
    {
        return await SendAsync(
            client,
            HttpMethod.Get,
            $"/workflow/v1/instances/{instanceId}",
            secret,
            subject);
    }

    /// <summary>
    /// 直接读时间线存储里的条目类型（绕开 HTTP）。
    /// </summary>
    /// <remarks>
    /// 终止后实例已不在运行态，而本夹具关闭了历史（<c>EnableHistory=false</c>），
    /// 实例视图查不到已结束实例 → <c>/history</c> 端点只能回答 404。
    /// 要验「时间线上有没有写进去」就得直接问存储——审计留痕本身与实例是否还可见无关。
    /// </remarks>
    internal static async Task<List<TimelineEntryKind>> ReadKindsFromStoreAsync(
        WebApplicationFactory<Program> host,
        string instanceId)
    {
        var store = host.Services.GetRequiredService<IApprovalTimelineStore>();
        var entries = await store.ListAsync(instanceId, 0, 100, Ct);

        return [.. entries.Select(entry => entry.Kind)];
    }

    /// <summary>读实例时间线的条目类型（按序号升序）。</summary>
    internal static async Task<List<string>> ReadKindsAsync(
        HttpClient client,
        string secret,
        string instanceId,
        string subject)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            subject);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadJsonAsync(response);
        var entries = body.GetProperty("entries").EnumerateArray();

        return [.. entries.Select(entry => entry.GetProperty("kind").GetString()!)];
    }
}

/// <summary>
/// 待办归属：认领 / 取消认领 / 转办（<c>POST /workflow/v1/tasks/{id}/{claim|unclaim|transfer}</c>）。
/// </summary>
/// <remarks>
/// 这组分两组断言：<b>归属是否正确</b>（HTTP 结果）与<b>是否留痕</b>（时间线条目）。
/// 后者是本轮的重点——管理干预类动作过去只有管理动作审计、没有实例时间线，
/// 「这条待办为什么换了人」在审批记录里查不到。
/// </remarks>
public sealed class TaskAssignmentTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.TaskRead,
        PlatformScopes.TaskAssign,
        PlatformScopes.HistoryRead,
        PlatformScopes.InstanceRead,
        // 开放模式下带 X-Subject-Id 等同于代客调用，必须显式持有 onbehalf
        PlatformScopes.TaskSubmitOnBehalf,
    ];

    [Fact]
    public async Task Claim_ShouldAssignToSubject_AndWriteTimeline()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, taskId) = await WriteActionHarness.StartAsync(host);

        using var response = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await WriteActionHarness.ReadJsonAsync(response);
        Assert.Equal("lisi", body.GetProperty("assignee").GetString());

        var kinds = await WriteActionHarness.ReadKindsAsync(client, secret, instanceId, "lisi");

        Assert.Contains("TASK_CLAIMED", kinds);
    }

    [Fact]
    public async Task Claim_Should409_WhenAlreadyClaimedByOther()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await WriteActionHarness.StartAsync(host);

        using var first = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 已被 lisi 认领：wangwu 再来应当 409（他看得到这条待办，「被谁拿走了」不是秘密）
        using var second = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "wangwu");
        var problem = await WriteActionHarness.ReadJsonAsync(second);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ErrorCodes.TaskAlreadyClaimed, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Claim_Should404_WhenNotInCandidates()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await WriteActionHarness.StartAsync(host);

        // zhaoliu 不在候选里：与「待办不存在」同口径，避免泄露待办存在性
        using var response = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "zhaoliu");
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.TaskNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Claim_Should403_ForPureServiceToken()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "batch-system", Scopes);
        var (_, taskId) = await WriteActionHarness.StartAsync(host);

        // 不带 X-Subject-Id：归属变化必须能追到一个人
        using var response = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: null);
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthSubjectRequired, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unclaim_ShouldReturnToCandidatePool_AndWriteTimeline()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, taskId) = await WriteActionHarness.StartAsync(host);

        using var claimed = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        using var response = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/unclaim", secret, subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await WriteActionHarness.ReadJsonAsync(response);
        Assert.Null(body.GetProperty("assignee").ValueKind is JsonValueKind.Null ? null : body.GetProperty("assignee").GetString());

        var kinds = await WriteActionHarness.ReadKindsAsync(client, secret, instanceId, "zhangsan");

        Assert.Contains("TASK_CLAIMED", kinds);
        Assert.Contains("TASK_UNCLAIMED", kinds);
    }

    [Fact]
    public async Task Unclaim_Should404_WhenNotAssignee()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await WriteActionHarness.StartAsync(host);

        using var claimed = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/claim", secret, subject: "lisi");

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        // wangwu 不是办理人：不能把 lisi 的待办退回候选池（要改别人的归属走 transfer）
        using var response = await WriteActionHarness.SendAsync(
            client, HttpMethod.Post, $"/workflow/v1/tasks/{taskId}/unclaim", secret, subject: "wangwu");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transfer_ShouldChangeAssignee_AndWriteTimeline()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, taskId) = await WriteActionHarness.StartAsync(host);

        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/transfer",
            secret,
            subject: "lisi",
            body: new { assignee = "wangwu", comment = "我休假，请代为处理" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await WriteActionHarness.ReadJsonAsync(response);
        Assert.Equal("wangwu", body.GetProperty("assignee").GetString());

        var kinds = await WriteActionHarness.ReadKindsAsync(client, secret, instanceId, "wangwu");

        Assert.Contains("TASK_TRANSFERRED", kinds);
    }

    [Fact]
    public async Task Transfer_Should422_WhenAssigneeMissing()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (_, taskId) = await WriteActionHarness.StartAsync(host);

        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/transfer",
            secret,
            subject: "lisi",
            body: new { assignee = "  " });
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterRequiredMissing, problem.GetProperty("code").GetString());
    }
}

/// <summary>
/// 终止实例（<c>POST /workflow/v1/instances/{id}/terminate</c>）。
/// </summary>
/// <remarks>
/// 终止是清单里点名的<b>唯一「有授权但没记录」</b>的口子：管理动作审计挂在调用方身上，
/// 实例自己的审批记录里什么都没有。本组用例除了验状态码，还验时间线上一定多出
/// <c>INSTANCE_TERMINATED</c> 一行。
/// </remarks>
public sealed class InstanceTerminationTests
{
    private static string[] Scopes =>
    [
        PlatformScopes.InstanceRead,
        PlatformScopes.InstanceTerminate,
        PlatformScopes.HistoryRead,
        PlatformScopes.TaskSubmitOnBehalf,
    ];

    [Fact]
    public async Task Terminate_ShouldStopInstance_AndWriteTimeline()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await WriteActionHarness.StartAsync(host);

        // 前置条件：实例对发起人可见（否则后面的 404 说明不了「终止」这件事）
        using var probe = await WriteActionHarness.GetInstanceAsync(client, secret, instanceId, "zhangsan");
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);

        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/instances/{instanceId}/terminate",
            secret,
            subject: "zhangsan",
            body: new { reason = "申请人撤回" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await WriteActionHarness.ReadJsonAsync(response);
        Assert.Equal("TERMINATED", body.GetProperty("status").GetString());
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());

        // 待办应当随实例一起消失
        Assert.Empty(body.GetProperty("tasks").EnumerateArray());

        var kinds = await WriteActionHarness.ReadKindsFromStoreAsync(host, instanceId);

        Assert.Contains(TimelineEntryKind.InstanceTerminated, kinds);
    }

    [Fact]
    public async Task Terminate_Should422_WhenReasonBlank()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await WriteActionHarness.StartAsync(host);

        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/instances/{instanceId}/terminate",
            secret,
            subject: "zhangsan",
            body: new { reason = "   " });
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.ParameterRequiredMissing, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Terminate_Should404_WhenNotStarter()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await WriteActionHarness.StartAsync(host);

        // 非发起人、非管理视角：终止等于替所有待办人做决定，不能只凭 scope
        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/instances/{instanceId}/terminate",
            secret,
            subject: "lisi",
            body: new { reason = "看不顺眼" });
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.InstanceNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Terminate_Should403_WithoutScope()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();

        // 只给 instance:read，没给 instance:terminate
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", [PlatformScopes.InstanceRead]);
        var (instanceId, _) = await WriteActionHarness.StartAsync(host);

        // 不带 X-Subject-Id：本例验的是 scope，不是代客调用资格
        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/instances/{instanceId}/terminate",
            secret,
            subject: null,
            body: new { reason = "撤回" });
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Terminate_Should409_WhenExpectedRevisionStale()
    {
        using var host = WriteActionHarness.BuildHost();
        await WriteActionHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await WriteActionHarness.RegisterAsync(client, "oa-portal", Scopes);
        var (instanceId, _) = await WriteActionHarness.StartAsync(host);

        using var response = await WriteActionHarness.SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/instances/{instanceId}/terminate",
            secret,
            subject: "zhangsan",
            body: new { reason = "撤回", expectedRevision = 99 });
        var problem = await WriteActionHarness.ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.ConcurrencyRevisionMismatch, problem.GetProperty("code").GetString());
    }
}
