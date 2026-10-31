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
/// 待办读模型（<c>/workflow/v1/tasks</c>）端到端：发起实例 → 查待办 → 验证
/// <c>TaskView.options</c>（三码语义）、<c>currentUserCanSubmit</c>（服务端判定）、
/// 白名单过滤与 scope 校验。
/// </summary>
/// <remarks>
/// 全部跑在内存模式宿主。待办的核心价值是「options 由服务端算好」，因此用内联审批 BPMN
/// （不依赖 Demo 的执行监听器），发起后停在 <c>task_approve</c> 即可验证。
/// </remarks>
public sealed class TaskCatalogTests
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "approval_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TaskView_ShouldResolveOptions_FromSemanticDictionary()
    {
        using var host = BuildHost();
        await DeployApprovalAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendAsync(client, $"/workflow/v1/tasks/{taskId}", secret, subject: "lisi");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(taskId, body.GetProperty("taskId").GetString());
        Assert.Equal("task_approve", body.GetProperty("activityId").GetString());
        Assert.Equal("审批", body.GetProperty("activityName").GetString());

        // 办理人来自 BPMN 的 ${approver} 表达式解析（发起时传 approver=lisi）
        Assert.Equal("lisi", body.GetProperty("assignee").GetString());

        // options 由语义字典解析：1/2/3 → APPROVE/REJECT/RETURN（调用方按 semantic 渲染按钮）
        var options = body.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, options.Count);

        var byId = options.ToDictionary(
            element => element.GetProperty("optionId").GetString()!,
            element => element,
            StringComparer.Ordinal);

        Assert.Equal("APPROVE", byId["1"].GetProperty("semantic").GetString());
        Assert.Equal("POSITIVE", byId["1"].GetProperty("outcomeClass").GetString());
        Assert.Equal("通过", byId["1"].GetProperty("label").GetString());

        Assert.Equal("REJECT", byId["2"].GetProperty("semantic").GetString());
        Assert.Equal("NEGATIVE", byId["2"].GetProperty("outcomeClass").GetString());

        Assert.Equal("RETURN", byId["3"].GetProperty("semantic").GetString());
        Assert.Equal("NEGATIVE", byId["3"].GetProperty("outcomeClass").GetString());

        // 表单字段：comment 保留（业务参数），approvalResult 排除（走 options）
        var formFieldIds = body.GetProperty("form")
            .EnumerateArray()
            .Select(element => element.GetProperty("fieldId").GetString())
            .ToArray();

        Assert.Contains("comment", formFieldIds);
        Assert.DoesNotContain("approvalResult", formFieldIds);
    }

    [Fact]
    public async Task TaskView_ShouldMarkCurrentUserCanSubmit_ByAssignee()
    {
        using var host = BuildHost();
        await DeployApprovalAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        // assignee 命中：lisi 是办理人 → 服务端判定可办理
        using var assignee = await SendAsync(client, $"/workflow/v1/tasks/{taskId}", secret, subject: "lisi");
        var assigneeBody = await ReadJsonAsync(assignee);

        Assert.Equal(HttpStatusCode.OK, assignee.StatusCode);
        Assert.True(assigneeBody.GetProperty("currentUserCanSubmit").GetBoolean());

        // 非办理人：wangwu 不是 assignee → 服务端判定不可办理（调用方不必自己猜）
        using var stranger = await SendAsync(client, $"/workflow/v1/tasks/{taskId}", secret, subject: "wangwu");
        var strangerBody = await ReadJsonAsync(stranger);

        Assert.Equal(HttpStatusCode.OK, stranger.StatusCode);
        Assert.False(strangerBody.GetProperty("currentUserCanSubmit").GetBoolean());
    }

    [Fact]
    public async Task TaskList_ShouldReturnAssigneeTasks_WhenScopeClaimed()
    {
        using var host = BuildHost();
        await DeployApprovalAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        // scope=CLAIMED 查「我已认领的」（= assignee = 我）
        using var claimed = await SendAsync(client, "/workflow/v1/tasks?scope=CLAIMED", secret, subject: "lisi");
        var claimedBody = await ReadJsonAsync(claimed);

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        var items = claimedBody.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(taskId, items[0].GetProperty("taskId").GetString());
        Assert.Equal(ApprovalKey, items[0].GetProperty("processKey").GetString());
        Assert.Equal("审批演示", items[0].GetProperty("processName").GetString());
        Assert.Equal("审批", items[0].GetProperty("activityName").GetString());

        // 非办理人用 CLAIMED 查不到这条待办
        using var stranger = await SendAsync(client, "/workflow/v1/tasks?scope=CLAIMED", secret, subject: "wangwu");
        var strangerBody = await ReadJsonAsync(stranger);

        Assert.Equal(HttpStatusCode.OK, stranger.StatusCode);
        Assert.Equal(0, strangerBody.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task TaskView_Should404_ForUnknownOrUnauthorized()
    {
        using var host = BuildHost();
        await DeployApprovalAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmitOnBehalf]);

        using var missing = await SendAsync(client, "/workflow/v1/tasks/nonexistent-task", secret, subject: "lisi");
        var problem = await ReadJsonAsync(missing);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(ErrorCodes.TaskNotFound, problem.GetProperty("code").GetString());
        Assert.StartsWith("https://netbpm.dev/problems/", problem.GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskList_ShouldForbid_WithoutTaskReadScope()
    {
        using var host = BuildHost();
        await DeployApprovalAsync(host);

        using var client = host.CreateClient();

        // 只有流程读权限，没有待办读权限
        var secret = await RegisterAsync(client, "reporting", [PlatformScopes.ProcessRead]);

        // 不带 subject 头：避免触发「代客调用需要 onbehalf scope」的中间件 403，
        // 这里验证的是「端点级 scope 校验」——没有 workflow:task:read 就该 403。
        using var response = await SendAsync(client, "/workflow/v1/tasks", secret);
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

    /// <summary>部署一个极简审批流程（startEvent → 审批 userTask → endEvent），返回定义 id。</summary>
    private static async Task<string> DeployApprovalAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="审批演示" isExecutable="true">
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
        var definition = await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);

        return definition.Id;
    }

    /// <summary>发起实例（办理人 = lisi），返回停在审批环节的任务 id。</summary>
    private static async Task<string> StartAndGetTaskAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        // 无租户部署（tenantId 缺省 = null → TENANT_ID_=''），发起也不传租户，
        // 与目录的「无租户 = 全局共享」口径一致。
        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["approver"] = "lisi" },
            ct: Ct);

        var tasks = await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId),
            Ct);

        var task = Assert.Single(tasks);

        return task.Id;
    }

    private static async Task<string> RegisterAsync(
        HttpClient client,
        string name,
        string[] scopes,
        string[]? allowedProcessKeys = null)
    {
        // 代客调用（onbehalf）是高危 scope，注册时必须显式确认
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name,
                tenantId = DefaultTenant,
                scopes,
                allowedProcessKeys,
            },
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
