using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P9 验收：<b>一条完整业务流（申请 → 审批 → 并行会签 → 归档）经 HTTP 端到端跑通</b>，
/// 以及「Swagger 全端点可用」。
/// <para>
/// 这条用例横跨 P5（变量持久化/历史）、P6（并行网关同步）、P7（用户任务）、P9（REST 宿主），
/// 是当前唯一同时压这四层的验收点。其中<b>会签未全部完成前归档任务不得出现</b>
/// 是风险 R1（并行 Join 语义）在端到端链路上的体现。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class RestEndToEndTests : IClassFixture<RestEngineFactory>
{
    private readonly RestEngineFactory _factory;

    public RestEndToEndTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FullBusinessFlow_ApplyReviewParallelSignoffArchive_ShouldRunThrough()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        // ① 部署（设计器契约）
        var processDefinitionId = await DeployAsync(client);

        // ② 启动实例，并带上流程变量
        var processInstanceId = await StartAsync(client, processDefinitionId);

        // ③ 申请 → 审批（审批时写入一个变量，验证「完成任务的变量落到流程实例上」）
        await CompleteOnlyTaskOfAsync(client, "applicant");

        await CompleteAsync(
            client,
            await SingleTaskOfAsync(client, "manager"),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["approved"] = true });

        // ④ 并行会签：两条分支同时各产生一个任务
        var hrSignOff = await SingleTaskOfAsync(client, "alice");
        var bossSignOff = await SingleTaskOfAsync(client, "bob");

        // 🔑 R1 语义：会签未齐时并行 Join 不达成，归档任务绝不能出现
        Assert.Empty(await TasksOfAsync(client, "archivist"));

        await CompleteAsync(client, hrSignOff);

        // 只完成一个会签人仍然不达成 Join
        Assert.Empty(await TasksOfAsync(client, "archivist"));

        await CompleteAsync(client, bossSignOff);

        // 两个会签人都完成 → Join 达成 → 归档任务出现
        var archiveTask = await SingleTaskOfAsync(client, "archivist");

        // ⑤ 变量在跨了 4 个命令之后依然可读（P5 变量持久化）
        Assert.Equal(3L, await VariableAsync(client, processInstanceId, "days"));

        var variables = await GetJsonAsync(client, $"/api/runtime/process-instances/{processInstanceId}/variables");
        Assert.Equal(3L, variables.GetProperty("days").GetInt64());
        Assert.True(variables.GetProperty("approved").GetBoolean());

        // ⑥ 归档 → 实例结束
        await CompleteAsync(client, archiveTask);

        // 运行中的执行行已删除
        using (var gone = await client.GetAsync(new Uri($"/api/runtime/process-instances/{processInstanceId}", UriKind.Relative), Ct))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
        }

        // ⑦ 历史：实例已结束，起始/结束节点与耗时齐全
        var historic = await GetJsonAsync(client, $"/api/runtime/history/process-instances/{processInstanceId}");

        Assert.False(historic.GetProperty("isRunning").GetBoolean());
        Assert.Equal("start", historic.GetProperty("startActivityId").GetString());
        Assert.Equal("end", historic.GetProperty("endActivityId").GetString());
        Assert.Equal(JsonValueKind.Null, historic.GetProperty("deleteReason").ValueKind);
        Assert.Equal("BIZ-P9-001", historic.GetProperty("businessKey").GetString());
        Assert.Equal("tester", historic.GetProperty("startUserId").GetString());

        // ⑧ 历史任务：5 条（申请/审批/2 个会签/归档），全部结束
        var historicTasks = await JsonArrayAsync(client, $"/api/runtime/history/process-instances/{processInstanceId}/tasks");

        Assert.Equal(
            ["applicant", "manager", "alice", "bob", "archivist"],
            historicTasks.Select(task => task.GetProperty("assignee").GetString()));
        Assert.All(historicTasks, task =>
        {
            Assert.NotEqual(JsonValueKind.Null, task.GetProperty("endTime").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, task.GetProperty("durationInMillis").ValueKind);
        });

        // ⑨ 历史活动：并行分支留下的两条会签记录都在
        var activities = await JsonArrayAsync(client, $"/api/runtime/history/process-instances/{processInstanceId}/activities");
        var activityIds = activities.Select(activity => activity.GetProperty("activityId").GetString()).ToList();

        Assert.Contains("apply", activityIds);
        Assert.Contains("review", activityIds);
        Assert.Contains("fork", activityIds);
        Assert.Contains("signHr", activityIds);
        Assert.Contains("signBoss", activityIds);
        Assert.Contains("join", activityIds);
        Assert.Contains("archive", activityIds);
        Assert.Contains("end", activityIds);
        Assert.All(activities, activity => Assert.False(activity.GetProperty("isActive").GetBoolean()));

        // ⑩ 流程图数据：XML + 高亮序列一次取全（只读实例视图）
        var diagram = await GetJsonAsync(client, $"/api/runtime/process-instances/{processInstanceId}/diagram");

        Assert.False(diagram.GetProperty("isRunning").GetBoolean());
        Assert.Contains("id=\"apply\"", diagram.GetProperty("bpmnXml").GetString(), StringComparison.Ordinal);
        Assert.NotEmpty(diagram.GetProperty("activities").EnumerateArray());
    }

    [Fact]
    public async Task OpenApi_ShouldExposeEveryEndpoint()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var client = _factory.CreateClient();

        var document = await GetJsonAsync(client, "/openapi/v1.json");
        var paths = document.GetProperty("paths").EnumerateObject().Select(property => property.Name).ToList();

        // P9 端点清单（§542 行）+ 设计器两个契约端点 + 健康检查
        Assert.Contains("/api/designer/deploy", paths);
        Assert.Contains("/api/designer/definition/{processDefinitionId}/xml", paths);
        Assert.Contains("/api/runtime/process-instances", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/variables", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/diagram", paths);
        Assert.Contains("/api/runtime/tasks", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/claim", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/complete", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}/activities", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}/tasks", paths);
        Assert.Contains("/health", paths);
    }

    [Fact]
    public async Task DesignerDefinitions_AfterDeploy_ShouldListDefinitionWithDeployTime()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        var processDefinitionId = await DeployAsync(client);

        using var response = await client.GetAsync(new Uri("/api/designer/definitions", UriKind.Relative), Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"部署列表失败：{response.StatusCode} {body}");

        var definitions = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();

        var deployed = definitions.Single(definition =>
            definition.GetProperty("processDefinitionId").GetString() == processDefinitionId);

        Assert.Equal("leave-approval", deployed.GetProperty("key").GetString());
        Assert.Equal("请假申请", deployed.GetProperty("name").GetString());
        Assert.Equal(1, deployed.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.String, deployed.GetProperty("deployTime").ValueKind);
    }

    // ─────────────────────────── HTTP 辅助 ───────────────────────────

    private static async Task<string> DeployAsync(HttpClient client)
    {
        using var content = new StringContent(ApprovalProcess(), Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync(new Uri("/api/designer/deploy", UriKind.Relative), content, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"部署失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("processDefinitionId").GetString()!;
    }

    private static async Task<string> StartAsync(HttpClient client, string processDefinitionId)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances", UriKind.Relative),
            new
            {
                processDefinitionId,
                businessKey = "BIZ-P9-001",
                startUserId = "tester",
                variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["days"] = 3 },
            },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"启动失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("processInstanceId").GetString()!;
    }

    private static async Task<IReadOnlyList<string>> TasksOfAsync(HttpClient client, string assignee)
    {
        var tasks = await JsonArrayAsync(client, $"/api/runtime/tasks?assignee={Uri.EscapeDataString(assignee)}");

        return [.. tasks.Select(task => task.GetProperty("id").GetString()!)];
    }

    private static async Task<string> SingleTaskOfAsync(HttpClient client, string assignee)
    {
        var tasks = await TasksOfAsync(client, assignee);

        Assert.True(tasks.Count == 1, $"期望 '{assignee}' 恰好有 1 个待办，实际 {tasks.Count} 个。");

        return tasks[0];
    }

    private static async Task CompleteOnlyTaskOfAsync(HttpClient client, string assignee) =>
        await CompleteAsync(client, await SingleTaskOfAsync(client, assignee));

    private static async Task CompleteAsync(
        HttpClient client,
        string taskId,
        IDictionary<string, object?>? variables = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/complete", UriKind.Relative),
            new { variables },
            Ct);

        Assert.True(
            response.IsSuccessStatusCode,
            $"完成任务 {taskId} 失败：{response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} 失败：{response.StatusCode} {body}");

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> JsonArrayAsync(HttpClient client, string path)
    {
        var root = await GetJsonAsync(client, path);

        return [.. root.EnumerateArray().Select(element => element.Clone())];
    }

    private static async Task<long?> VariableAsync(HttpClient client, string processInstanceId, string name)
    {
        var root = await GetJsonAsync(client, $"/api/runtime/process-instances/{processInstanceId}/variables");

        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    /// <summary>申请 → 审批 → 并行会签（人事/总监）→ 归档。并行分支只有在两条都到达时才汇合。</summary>
    private static string ApprovalProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="leave-approval" name="请假申请" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" name="申请" activiti:assignee="applicant" />
            <userTask id="review" name="审批" activiti:assignee="manager" />
            <parallelGateway id="fork" />
            <userTask id="signHr" name="会签-人事" activiti:assignee="alice" />
            <userTask id="signBoss" name="会签-总监" activiti:assignee="bob" />
            <parallelGateway id="join" />
            <userTask id="archive" name="归档" activiti:assignee="archivist" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="review" />
            <sequenceFlow id="f3" sourceRef="review" targetRef="fork" />
            <sequenceFlow id="f4" sourceRef="fork" targetRef="signHr" />
            <sequenceFlow id="f5" sourceRef="fork" targetRef="signBoss" />
            <sequenceFlow id="f6" sourceRef="signHr" targetRef="join" />
            <sequenceFlow id="f7" sourceRef="signBoss" targetRef="join" />
            <sequenceFlow id="f8" sourceRef="join" targetRef="archive" />
            <sequenceFlow id="f9" sourceRef="archive" targetRef="end" />
          </process>
        </definitions>
        """;
}
