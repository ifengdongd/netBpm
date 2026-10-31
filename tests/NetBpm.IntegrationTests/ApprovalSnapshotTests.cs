using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// §4.5 表单快照（<c>nb_approval_snapshot</c>）与「已办带当时的结论」端到端。
/// </summary>
/// <remarks>
/// <para>
/// 这两件事共用一条事实：<b>引擎不认识表单值，也不认识审批意见</b>。
/// <c>ACT_HI_TASKINST</c> 只记「谁在几点把任务点了完成」，
/// 「当时这个环节让填什么、这个人填了什么、批了哪个选项」从来没进过引擎的表。
/// 所以它们只能被<b>写时定稿</b>到平台自己的表里——这也是本组用例唯一能断言的地方。
/// </para>
/// <para>
/// 跑 MySQL：快照表与已办列表都要读库；内存宿主虽然也能跑，
/// 但「同一份数据在两个进程里结论一致」这件事必须对着真库才有说服力。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class ApprovalSnapshotTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "snapshot_demo";

    private readonly RestEngineFactory _factory;

    public ApprovalSnapshotTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 提交时冻住「当时的字段定义 + 表单值」，历史回访能按当时的样子渲染。
    /// </summary>
    [Fact]
    public async Task FormSnapshot_ShouldCaptureSchemaAndValues()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "SNAP-100");
        var taskId = await FindFirstTaskAsync(engine, instanceId);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "snapshot-app",
            [
                PlatformScopes.TaskRead,
                PlatformScopes.TaskSubmit,
                PlatformScopes.TaskSubmitOnBehalf,
                PlatformScopes.InstanceRead,
                PlatformScopes.HistoryRead,
            ]);

        // 走平台提交端点（只有它会写快照：快照的职责就是「解释这一次提交」）
        using (var submit = await SendPostAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new
            {
                optionId = "1",
                comment = "同意",
                formValues = new Dictionary<string, string?> { ["days"] = "3", ["reason"] = "家中有事" },
            }))
        {
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        }

        using var response = await SendGetAsync(client, $"/workflow/v1/tasks/{taskId}/form", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(taskId, body.GetProperty("taskId").GetString());
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal("主管审批", body.GetProperty("activityName").GetString());
        Assert.Equal("manager", body.GetProperty("actorId").GetString());
        Assert.Equal("1", body.GetProperty("optionId").GetString());
        Assert.True(body.GetProperty("definitionVersion").GetInt32() >= 1);

        // 值：键是字段 id
        var values = body.GetProperty("values");

        Assert.Equal("3", values.GetProperty("days").GetString());
        Assert.Equal("家中有事", values.GetProperty("reason").GetString());

        // 定义：审批结果（approvalResult）属于语义字典而不是表单字段，不该出现在快照的定义里
        var fields = body.GetProperty("schema").GetProperty("fields").EnumerateArray().ToList();

        Assert.Contains(fields, field => field.GetProperty("fieldId").GetString() == "days");
        Assert.DoesNotContain(fields, field => field.GetProperty("fieldId").GetString() == "approvalResult");
    }

    /// <summary>时间线条目用 <c>formSnapshotTaskId</c> 指向快照——它们是同一次提交的两半。</summary>
    [Fact]
    public async Task Timeline_ShouldPointAtFormSnapshot()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "SNAP-200");
        var taskId = await FindFirstTaskAsync(engine, instanceId);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "snapshot-timeline",
            [
                PlatformScopes.TaskRead,
                PlatformScopes.TaskSubmit,
                PlatformScopes.TaskSubmitOnBehalf,
                PlatformScopes.InstanceRead,
                PlatformScopes.HistoryRead,
            ]);

        using var submit = await SendPostAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new { optionId = "1", comment = "同意", formValues = new Dictionary<string, string?> { ["days"] = "2" } });

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        using var response = await SendGetAsync(
            client,
            $"/workflow/v1/instances/{instanceId}/history",
            secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var completed = body.GetProperty("entries")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("kind").GetString() == "TASK_COMPLETED");

        Assert.Equal(taskId, completed.GetProperty("taskId").GetString());
        Assert.Equal(taskId, completed.GetProperty("formSnapshotTaskId").GetString());
    }

    /// <summary>没有快照的任务按「不存在」处理：404 而不是 200 + 空对象。</summary>
    [Fact]
    public async Task FormSnapshot_ShouldBeNotFound_WhenMissing()
    {
        SkipUnlessReady();
        Reset();

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "snapshot-missing", [PlatformScopes.TaskRead]);

        using var response = await SendGetAsync(client, "/workflow/v1/tasks/no_such_task/form", secret);
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.TaskNotFound, problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 「我的已办」要带回<b>我当时的结论</b>：选项、语义与审批意见。
    /// </summary>
    [Fact]
    public async Task DoneTasks_ShouldCarryMyDecision()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "SNAP-300");
        var taskId = await FindFirstTaskAsync(engine, instanceId);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "snapshot-done",
            [
                PlatformScopes.TaskRead,
                PlatformScopes.TaskSubmit,
                PlatformScopes.TaskSubmitOnBehalf,
                PlatformScopes.InstanceRead,
                PlatformScopes.HistoryRead,
            ]);

        using (var submit = await SendPostAsync(
            client,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            "manager",
            new { optionId = "1", comment = "同意，注意交接" }))
        {
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        }

        using var response = await SendGetAsync(client, "/workflow/v1/tasks?scope=DONE", secret, "manager");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(taskId, items[0].GetProperty("taskId").GetString());

        var decision = items[0].GetProperty("decision");

        Assert.Equal("1", decision.GetProperty("optionId").GetString());
        Assert.Equal("APPROVE", decision.GetProperty("semantic").GetString());
        Assert.Equal("同意，注意交接", decision.GetProperty("comment").GetString());
        Assert.NotEqual(JsonValueKind.Null, decision.GetProperty("decidedAt").ValueKind);
    }

    /// <summary>没走过平台提交端点的任务（引擎侧直接办结）：已办仍在，但没有「当时的结论」。</summary>
    [Fact]
    public async Task DoneTasks_ShouldSkipDecision_WhenNotCaptured()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "SNAP-400");
        var task = Assert.Single(
            await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        // 绕过平台端点：引擎永远不会知道「批了什么」，表里也不会有对应时间线条目
        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = "1" },
            ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "snapshot-notdone",
            [
                PlatformScopes.TaskRead,
                PlatformScopes.InstanceRead,
                PlatformScopes.HistoryRead,
                PlatformScopes.TaskSubmitOnBehalf,
            ]);

        using var response = await SendGetAsync(client, "/workflow/v1/tasks?scope=DONE", secret, task.Assignee);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(
            JsonValueKind.Null,
            items[0].TryGetProperty("decision", out var decision)
                ? decision.ValueKind
                : JsonValueKind.Null);
    }

    /// <summary>
    /// 保留期清理：只删 <c>captured_at</c> 早于切点的快照。
    /// </summary>
    /// <remarks>
    /// 切点与历史归档<b>必须来自同一个配置</b>（<c>AdminArchiveCleanupService</c> 只读一个键）：
    /// 快照靠 <c>task_id</c> 与历史行对齐，两边各配一个保留期迟早会被人调到错位。
    /// </remarks>
    [Fact]
    public async Task Snapshot_ShouldBePrunedByCapturedAt()
    {
        SkipUnlessReady();
        Reset();

        var store = _factory.Services.GetRequiredService<IApprovalSnapshotStore>();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(NewRecord("prune-old-1", now.AddDays(-400)), Ct);
        await store.AppendAsync(NewRecord("prune-old-2", now.AddDays(-30)), Ct);
        await store.AppendAsync(NewRecord("prune-new-1", now.AddDays(-1)), Ct);

        var removed = await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct);

        Assert.Equal(2, removed);
        Assert.Null(await store.FindAsync("prune-old-1", Ct));
        Assert.Null(await store.FindAsync("prune-old-2", Ct));
        Assert.NotNull(await store.FindAsync("prune-new-1", Ct));
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    /// <summary>构造一份最小快照（只关心 <c>task_id</c> 与定稿时间）。</summary>
    private static ApprovalSnapshotRecord NewRecord(string taskId, DateTimeOffset capturedAt) =>
        new(
            TaskId: taskId,
            InstanceId: "prune-instance",
            TenantId: DefaultTenant,
            ProcessKey: ApprovalKey,
            ProcessDefinitionId: null,
            DefinitionVersion: 1,
            ActivityId: "task_owner",
            ActivityName: "主管审批",
            ActorId: "manager",
            ActorCallerId: null,
            OptionId: "1",
            Fields: [],
            Values: new Dictionary<string, string?>(StringComparer.Ordinal),
            Attachments: null,
            CapturedAt: capturedAt);

    /// <summary>
    /// 部署两环节流程：主管审批（固定办理人 manager）→ 备案（lisi）。
    /// 第一环节带 <c>days</c> / <c>reason</c> 两个表单字段——它们就是快照要冻住的东西。
    /// </summary>
    private static async Task DeployAsync(IProcessEngine engine)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="快照演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_owner" />
                <bpmn:userTask id="task_owner" name="主管审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="days" name="天数" type="long" />
                    <activiti:formProperty id="reason" name="事由" type="string" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_owner" targetRef="task_record" />
                <bpmn:userTask id="task_record" name="备案" activiti:assignee="lisi">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_record" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(IProcessEngine engine, string startedBy, string businessKey) =>
        await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            businessKey: businessKey,
            startUserId: startedBy,
            ct: Ct);

    private static async Task<string> FindFirstTaskAsync(IProcessEngine engine, string instanceId)
    {
        var task = Assert.Single(
            await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        Assert.Equal("task_owner", task.TaskDefinitionKey);

        return task.Id;
    }

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        try
        {
            using var session = MySqlTestDatabase.CreateSessionFactory().Create();
            var count = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables " +
                "where table_schema = database() and table_name in ('nb_tenant', 'nb_approval_snapshot')");

            Assert.SkipUnless(
                count >= 2,
                "本库缺平台表（nb_tenant / nb_approval_snapshot）。请把 NETBPM_MYSQL_CONNECTION 指向 netbpm_ittest，" +
                "或对目标库执行 DbMigrate migrate 补齐 nb_* 表。");
        }
        catch
        {
            Assert.Skip("无法探测平台表，跳过。");
        }
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
