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
/// 提交审批（<c>POST /workflow/v1/tasks/{id}/submit</c>）端到端：发起实例 → 提交审批，
/// 验证消费 <c>TaskView.options</c> 的 422 校验（选项集 / 语义一致性 / 意见 / 目标节点）、
/// 归属 404、纯服务 403，以及成功后返回的推进结果。
/// </summary>
/// <remarks>
/// 跑在内存模式宿主。用<b>两环节</b>流程（申请 → 审批）而非单环节，是为了让「提交成功」后实例仍在运行
/// （内存模式历史关闭，实例一旦结束运行态行被删、实例视图查不到）。
/// </remarks>
public sealed class TaskSubmitTests
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "submit_demo";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Submit_ShouldCompleteTask_AndReturnNextTasks()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.InstanceRead]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "1", comment = "同意" });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 推进到下一环节（申请 → 审批），实例仍在运行
        Assert.Equal("RUNNING", body.GetProperty("instance").GetProperty("status").GetString());

        var nextTasks = body.GetProperty("nextTasks").EnumerateArray().ToList();

        Assert.Single(nextTasks);
        Assert.Equal("task_approve", nextTasks[0].GetProperty("activityId").GetString());
        Assert.Equal("manager", nextTasks[0].GetProperty("assignee").GetString());

        // 事件类型：只完成了任务，实例未结束
        var events = body.GetProperty("events").EnumerateArray().Select(element => element.GetString()).ToArray();

        Assert.Contains("task.completed", events);
        Assert.DoesNotContain("instance.completed", events);

        // 审批结论已写入引擎变量（result = optionId）
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var instanceId = body.GetProperty("instance").GetProperty("instanceId").GetString()!;
        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        Assert.Equal("1", variables["result"]);
        Assert.Equal("同意", variables["comment"]);
    }

    [Fact]
    public async Task Submit_Should422_WhenOptionNotInSet()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "9" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.DecisionNotInOptionSet, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_Should422_WhenSemanticMismatch()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        // optionId=1 的语义是 APPROVE，调用方却声明 REJECT → 422
        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "1", semantic = "REJECT" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.DecisionSemanticMismatch, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_Should422_WhenCommentRequired()
    {
        using var host = BuildHost();
        await DeclareRequiresCommentAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "3" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.DecisionCommentRequired, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_Should422_WhenTargetNodeRequired()
    {
        using var host = BuildHost();
        await DeclareRequiresTargetNodeAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "3", comment = "退回" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.DecisionTargetNodeRequired, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_Should422_WhenTargetNodeInvalid()
    {
        using var host = BuildHost();
        await DeclareRequiresTargetNodeAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: "lisi", new { optionId = "3", comment = "退回", targetNodeId = "nowhere" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ErrorCodes.DecisionTargetNodeInvalid, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_ShouldMoveBackToTargetNode_WhenDecisionCarriesTargetNode()
    {
        using var host = BuildHost();
        await DeclareApproveReturnTargetAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.InstanceRead]);

        var (instanceId, approveTaskId) = await StartAndAdvanceToApproveAsync(host, client, secret);

        // 审批环节驳回：选项 3（驳回修改）指定回到 task_apply
        using var response = await SendSubmitAsync(
            client, approveTaskId, secret, subject: "manager",
            new { optionId = "3", comment = "材料不全，退回修改", targetNodeId = "task_apply" });

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 结论已经落地（驳回也是一次审批决定，必须留痕）
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        Assert.Equal("3", variables["result"]);
        Assert.Equal("材料不全，退回修改", variables["comment"]);

        // 流程真的回到了申请环节，而不是继续往归档走
        var nextTasks = body.GetProperty("nextTasks").EnumerateArray().ToList();

        Assert.Single(nextTasks);
        Assert.Equal("task_apply", nextTasks[0].GetProperty("activityId").GetString());
        Assert.Equal("lisi", nextTasks[0].GetProperty("assignee").GetString());
        Assert.Equal("RUNNING", body.GetProperty("instance").GetProperty("status").GetString());

        var events = body.GetProperty("events").EnumerateArray().Select(element => element.GetString()).ToArray();

        Assert.Contains("task.completed", events);
        Assert.Contains("instance.jumped", events);

        // 跳转单独成条：历史页要能回答「这张单为什么又回到填写环节」
        var delta = body.GetProperty("timelineDelta").EnumerateArray().ToList();

        Assert.Contains(
            delta,
            entry => entry.GetProperty("kind").GetString() == "INSTANCE_JUMPED"
                && entry.GetProperty("targetNodeId").GetString() == "task_apply");
    }

    [Fact]
    public async Task Submit_ShouldKeepDecision_WhenJumpNotPossible()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(
            client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf, PlatformScopes.InstanceRead]);

        var (instanceId, approveTaskId) = await StartAndAdvanceToApproveAsync(host, client, secret);

        // 目标节点不在流程定义里 → 引擎侧改道失败。取舍是：审批结论已生效，提交照常成功，
        // 流程停在流转后的位置（归档环节），改道失败只体现在日志与「没有 instance.jumped」上。
        using var response = await SendSubmitAsync(
            client, approveTaskId, secret, subject: "manager",
            new { optionId = "3", comment = "退回修改", targetNodeId = "nowhere" });

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var variables = await engine.GetVariablesAsync(instanceId, Ct);

        Assert.Equal("3", variables["result"]);

        var nextTasks = body.GetProperty("nextTasks").EnumerateArray().ToList();

        Assert.Single(nextTasks);
        Assert.Equal("task_notify", nextTasks[0].GetProperty("activityId").GetString());

        var events = body.GetProperty("events").EnumerateArray().Select(element => element.GetString()).ToArray();

        Assert.Contains("task.completed", events);
        Assert.DoesNotContain("instance.jumped", events);
    }

    [Fact]
    public async Task Submit_Should404_ForUnknownOrNotAssignee()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await RegisterAsync(client, "oa-portal", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit, PlatformScopes.TaskSubmitOnBehalf]);

        // 不存在的任务 → 404
        using var missing = await SendSubmitAsync(client, "nonexistent-task", secret, subject: "lisi", new { optionId = "1" });
        var missingProblem = await ReadJsonAsync(missing);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(ErrorCodes.TaskNotFound, missingProblem.GetProperty("code").GetString());

        // 非办理人（wangwu 不是 assignee=lisi 的申请环节办理人）→ 与「不存在」同口径 404
        var taskId = await StartAndGetTaskAsync(host);

        using var forbidden = await SendSubmitAsync(client, taskId, secret, subject: "wangwu", new { optionId = "1" });
        var forbiddenProblem = await ReadJsonAsync(forbidden);

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(ErrorCodes.TaskNotFound, forbiddenProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_Should403_ForPureServiceToken()
    {
        using var host = BuildHost();
        await DeployAsync(host);

        using var client = host.CreateClient();

        // 只授予 submit（不带 onbehalf），且不带 X-Subject-Id → 纯服务令牌，审批必须落到真人
        var secret = await RegisterAsync(client, "batch-system", [PlatformScopes.TaskRead, PlatformScopes.TaskSubmit]);

        var taskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, taskId, secret, subject: null, new { optionId = "1" });
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthSubjectRequired, problem.GetProperty("code").GetString());
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
    /// 部署三环节流程：申请（assignee=applicant）→ 审批（assignee=approver）→ 归档（assignee=archivist）。
    /// </summary>
    /// <remarks>
    /// 为什么要有第三环节：驳回是「完成当前任务 → 改道到目标节点」，改道的落点是完成后的
    /// <b>下一个等待点</b>。审批环节后面直接挂结束事件的话，提交一完成实例就结束了，
    /// 没有等待点可供改道（跳转只能失败）。归档环节既是真实流程里常见的「驳回也要走到的地方」，
    /// 也是这条用例需要的改道落点。
    /// </remarks>
    private static async Task DeployAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="提交演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="${applicant}">
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
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="${approver}">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="task_notify" />
                <bpmn:userTask id="task_notify" name="归档" activiti:assignee="archivist">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                      <activiti:value id="3" name="驳回修改" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow3</bpmn:incoming>
                  <bpmn:outgoing>flow4</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow4" sourceRef="task_notify" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);
    }

    /// <summary>把选项 3（驳回修改）声明为「必须填写意见」。</summary>
    private static async Task DeclareRequiresCommentAsync(WebApplicationFactory<Program> host)
    {
        await DeployAsync(host);
        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, ApprovalKey, NodeId: "task_apply", "3",
            ApprovalSemantic.Return, ApprovalOutcomeClass.Negative,
            "驳回修改", RequiresComment: true, RequiresTargetNode: false,
            AllowedTargetNodes: null, Attributes: null, now, now));
    }

    /// <summary>把选项 3（驳回修改）声明为「必须指定目标节点」，白名单只有 task_apply。</summary>
    private static async Task DeclareRequiresTargetNodeAsync(WebApplicationFactory<Program> host)
    {
        await DeployAsync(host);
        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, ApprovalKey, NodeId: "task_apply", "3",
            ApprovalSemantic.Return, ApprovalOutcomeClass.Negative,
            "驳回修改", RequiresComment: false, RequiresTargetNode: true,
            AllowedTargetNodes: ["task_apply"], Attributes: null, now, now));
    }

    /// <summary>发起实例（申请人 = lisi），返回停在申请环节的任务 id。</summary>
    private static async Task<string> StartAndGetTaskAsync(WebApplicationFactory<Program> host)
    {
        var engine = host.Services.GetRequiredService<IProcessEngine>();

        var instanceId = await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["applicant"] = "lisi",
                ["approver"] = "manager",
            },
            ct: Ct);

        var tasks = await engine.FindTasksAsync(
            new Runtime.TaskQuery(ProcessInstanceId: instanceId),
            Ct);

        var task = Assert.Single(tasks);
        Assert.Equal("task_apply", task.TaskDefinitionKey);

        return task.Id;
    }

    /// <summary>把<b>审批环节</b>的选项 3（驳回修改）声明为「必须指定目标节点」，白名单只有 task_apply。</summary>
    private static async Task DeclareApproveReturnTargetAsync(WebApplicationFactory<Program> host)
    {
        await DeployAsync(host);
        var store = Assert.IsType<InMemoryApprovalDictionaryStore>(
            host.Services.GetRequiredService<IApprovalDictionaryStore>());

        var now = DateTimeOffset.UtcNow;

        store.Upsert(new ApprovalDictionaryEntry(
            DefaultTenant, ApprovalKey, NodeId: "task_approve", "3",
            ApprovalSemantic.Return, ApprovalOutcomeClass.Negative,
            "驳回修改", RequiresComment: false, RequiresTargetNode: true,
            AllowedTargetNodes: ["task_apply"], Attributes: null, now, now));
    }

    /// <summary>发起并完成申请环节，返回实例 id 与停在审批环节的任务 id。</summary>
    private static async Task<(string InstanceId, string TaskId)> StartAndAdvanceToApproveAsync(
        WebApplicationFactory<Program> host,
        HttpClient client,
        string secret) =>
        await AdvanceAsync(host, client, secret, "lisi", "task_approve");

    private static async Task<(string InstanceId, string TaskId)> AdvanceAsync(
        WebApplicationFactory<Program> host,
        HttpClient client,
        string secret,
        string subject,
        string expectedActivityId)
    {
        var firstTaskId = await StartAndGetTaskAsync(host);

        using var response = await SendSubmitAsync(client, firstTaskId, secret, subject, new { optionId = "1", comment = "提交" });
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var instanceId = body.GetProperty("instance").GetProperty("instanceId").GetString()!;

        var engine = host.Services.GetRequiredService<IProcessEngine>();
        var tasks = await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct);
        var next = Assert.Single(tasks);

        Assert.Equal(expectedActivityId, next.TaskDefinitionKey);

        return (instanceId, next.Id);
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

    private static Task<HttpResponseMessage> SendSubmitAsync(
        HttpClient client,
        string taskId,
        string secret,
        string? subject,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"/workflow/v1/tasks/{taskId}/submit", UriKind.Relative));
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
