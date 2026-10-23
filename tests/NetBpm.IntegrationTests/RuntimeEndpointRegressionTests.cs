using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 运行态端点拆分（Service + 授权过滤器 + 错误码收敛）后的<b>端点级回归</b>。
/// <para>
/// 拆分把 2200 行单文件切成路由表 + 4 个域文件，并把 16 处 <c>EnsureInstanceAccessAsync</c>、
/// 12 处 <c>TaskAuthorization.*</c>、7 处变量授权搬进 <c>AddEndpointFilter</c>。
/// 这类重排最容易出的问题不是编译错误，而是<b>某个路由悄悄少了授权、或 404 变成了 500 / 403</b>，
/// 因此这里按「路由清单」逐条打靶：不存在的 id 必须 404（而不是 500、也不允许前端拿到成功）。
/// </para>
/// <para>
/// 另一部分用例覆盖此前只有手工验证的路径：<c>start-before</c> 活动干预、批量挂起 / 激活 / 迁移、
/// 任务局部变量往返、批量逐项隔离。全部走真实宿主 + 真实 MySQL；数据库不可用时动态跳过。
/// </para>
/// </summary>
[Collection("MySql")]
public sealed class RuntimeEndpointRegressionTests : IClassFixture<RestEngineFactory>
{
    private const string MissingInstanceId = "999999991";
    private const string MissingTaskId = "999999992";

    private readonly RestEngineFactory _factory;

    public RuntimeEndpointRegressionTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 路由级打靶：所有「按 id 取对象」的端点对不存在的 id 必须回 404。
    /// <para>
    /// 这条用例锁定的是过滤器契约：一旦某条路由漏挂 <c>RequireInstanceAccess</c> /
    /// <c>RequireTaskAccess</c>，handler 会直接对空对象解引用，结果变成 500 或 200（空成功）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task MissingIds_ShouldReturn404_OnEveryIdBasedRoute()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, $"/api/runtime/process-instances/{MissingInstanceId}", null),
            (HttpMethod.Delete, $"/api/runtime/process-instances/{MissingInstanceId}", null),
            (HttpMethod.Post, $"/api/runtime/process-instances/{MissingInstanceId}/suspend", null),
            (HttpMethod.Post, $"/api/runtime/process-instances/{MissingInstanceId}/activate", null),
            (HttpMethod.Post, $"/api/runtime/process-instances/{MissingInstanceId}/jump", new { targetActivityId = "end" }),
            (HttpMethod.Post, $"/api/runtime/process-instances/{MissingInstanceId}/migrate", new { targetProcessDefinitionId = "missing-process:1:1" }),
            (HttpMethod.Get, $"/api/runtime/process-instances/{MissingInstanceId}/identity-links", null),
            (HttpMethod.Get, $"/api/runtime/process-instances/{MissingInstanceId}/variables", null),
            (HttpMethod.Get, $"/api/runtime/process-instances/{MissingInstanceId}/diagram", null),
            (HttpMethod.Post,
                $"/api/runtime/process-instances/{MissingInstanceId}/activities/apply/cancel",
                null),
            (HttpMethod.Post,
                $"/api/runtime/process-instances/{MissingInstanceId}/activities/apply/start-before",
                null),
            (HttpMethod.Get, $"/api/runtime/history/process-instances/{MissingInstanceId}", null),
            (HttpMethod.Get, $"/api/runtime/tasks/{MissingTaskId}", null),
            (HttpMethod.Get, $"/api/runtime/tasks/{MissingTaskId}/identity-links", null),
            (HttpMethod.Get, $"/api/runtime/tasks/{MissingTaskId}/form", null),
            (HttpMethod.Get, $"/api/runtime/tasks/{MissingTaskId}/variables", null),
            (HttpMethod.Patch, $"/api/runtime/tasks/{MissingTaskId}", new { priority = 1 }),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/assignee", new { assignee = "someone" }),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/candidate-users", new { userId = "someone" }),
            (HttpMethod.Delete, $"/api/runtime/tasks/{MissingTaskId}/candidate-users/someone", null),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/candidate-groups", new { groupId = "some-group" }),
            (HttpMethod.Delete, $"/api/runtime/tasks/{MissingTaskId}/candidate-groups/some-group", null),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/delegate", new { userId = "someone" }),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/resolve", null),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/claim", null),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/unclaim", null),
            (HttpMethod.Post, $"/api/runtime/tasks/{MissingTaskId}/complete", null),
        };

        foreach (var (method, path, payload) in routes)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

            if (payload is not null)
            {
                request.Content = JsonContent.Create(payload);
            }

            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{method} {path} 期望 404，实际 {(int)response.StatusCode} {body}");
        }

        // 迁移记录是「审计类查询」：未知实例返回空表而不是 404（不区分「没有记录」与「实例不存在」）
        Assert.Empty(await JsonArrayAsync(client, $"/api/runtime/process-instances/{MissingInstanceId}/migrations"));
    }

    /// <summary>
    /// 错误码收敛：对象不存在统一为 <c>not_found</c>（不再有 <c>task_not_found</c> /
    /// <c>process_instance_not_found</c> 两种同义写法）。
    /// </summary>
    [Fact]
    public async Task MissingObjects_ShouldUseCanonicalNotFoundCode()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        var instanceBody = await GetJsonAsync(client, $"/api/runtime/process-instances/{MissingInstanceId}", HttpStatusCode.NotFound);
        Assert.Equal("not_found", instanceBody.GetProperty("code").GetString());

        var taskBody = await GetJsonAsync(client, $"/api/runtime/tasks/{MissingTaskId}", HttpStatusCode.NotFound);
        Assert.Equal("not_found", taskBody.GetProperty("code").GetString());
    }

    /// <summary>
    /// <c>start-before</c>：不搬动现有令牌，额外挂一条并发分支（目标活动会真的开始执行）。
    /// <para>拆分前这条路径没有自动化覆盖，只有手工验证。</para>
    /// </summary>
    [Fact]
    public async Task StartBeforeActivity_ShouldAddConcurrentToken()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();
        await DeployAsync(client, TwoTaskProcess);
        var instanceId = await StartAsync(client, processDefinitionKey: "regression-two");

        // 先推进到 review（reviewer 手上有 1 条）
        var applyTask = Assert.Single(await TasksOfAsync(client, "applicant"));
        await CompleteAsync(client, applyTask);

        Assert.Empty(await TasksOfAsync(client, "applicant"));
        Assert.Single(await TasksOfAsync(client, "reviewer"));

        // 在 apply 之前启动一个新令牌 → applicant 重新拿到 1 条，reviewer 不受影响
        using (var response = await client.PostAsJsonAsync(
            new Uri(
                $"/api/runtime/process-instances/{instanceId}/activities/apply/start-before",
                UriKind.Relative),
            new { reason = "回归：并发分支" },
            Ct))
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, $"start-before 失败：{response.StatusCode} {body}");
        }

        Assert.Single(await TasksOfAsync(client, "applicant"));
        Assert.Single(await TasksOfAsync(client, "reviewer"));
    }

    /// <summary>任务局部变量往返：写 → 读 → 删 → 再删 404。</summary>
    [Fact]
    public async Task TaskLocalVariables_ShouldRoundTrip()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();
        await DeployAsync(client, SingleTaskProcess);
        var instanceId = await StartAsync(client, processDefinitionKey: "regression-single");

        var taskId = Assert.Single(await TasksOfAsync(client, "applicant"));

        using (var put = await client.PutAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/variables/local", UriKind.Relative),
            "v1",
            Ct))
        {
            var body = await put.Content.ReadAsStringAsync(Ct);
            Assert.True(put.IsSuccessStatusCode, $"写任务局部变量失败：{put.StatusCode} {body}");
        }

        var variables = await GetJsonAsync(client, $"/api/runtime/tasks/{taskId}/variables");
        Assert.Equal("v1", variables.GetProperty("local").GetString());

        // 任务局部变量不写回流程实例作用域
        var instanceVariables = await GetJsonAsync(
            client,
            $"/api/runtime/process-instances/{instanceId}/variables");
        Assert.False(instanceVariables.TryGetProperty("local", out _));

        using (var delete = await client.DeleteAsync(
            new Uri($"/api/runtime/tasks/{taskId}/variables/local", UriKind.Relative),
            Ct))
        {
            Assert.True(delete.IsSuccessStatusCode, $"删除任务局部变量失败：{delete.StatusCode}");
        }

        using (var deleteAgain = await client.DeleteAsync(
            new Uri($"/api/runtime/tasks/{taskId}/variables/local", UriKind.Relative),
            Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        }
    }

    /// <summary>
    /// 批量挂起 / 激活 / 删除：逐项结果 + 未知 id 只让该项失败（逐项隔离，不整体中断）。
    /// </summary>
    [Fact]
    public async Task BatchLifecycle_ShouldIsolateFailuresPerItem()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();
        await DeployAsync(client, SingleTaskProcess);

        var first = await StartAsync(client, processDefinitionKey: "regression-single");
        var second = await StartAsync(client, processDefinitionKey: "regression-single");
        var ids = new[] { first, second };

        var suspended = await PostJsonAsync(client, "/api/runtime/process-instances/batch/suspend", new { processInstanceIds = ids });
        Assert.Equal(2, suspended.GetProperty("succeeded").GetInt32());

        foreach (var id in ids)
        {
            var instance = await GetJsonAsync(client, $"/api/runtime/process-instances/{id}");
            Assert.True(instance.GetProperty("isSuspended").GetBoolean(), $"实例 {id} 未挂起");
        }

        var activated = await PostJsonAsync(client, "/api/runtime/process-instances/batch/activate", new { processInstanceIds = ids });
        Assert.Equal(2, activated.GetProperty("succeeded").GetInt32());

        // 第三条是未知 id：整体仍是 200，但该项失败
        var deleted = await PostJsonAsync(
            client,
            "/api/runtime/process-instances/batch/delete",
            new { processInstanceIds = new[] { first, second, MissingInstanceId }, deleteReason = "回归清理" });

        Assert.Equal(3, deleted.GetProperty("total").GetInt32());
        Assert.Equal(2, deleted.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, deleted.GetProperty("failed").GetInt32());
    }

    /// <summary>批量迁移：同一批共用一个目标版本，活动映射随请求下发。</summary>
    [Fact]
    public async Task BatchMigrate_ShouldMoveAllInstancesToTargetVersion()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        var v1 = await DeployAsync(client, MigrateV1);
        var v2 = await DeployAsync(client, MigrateV2, processDefinitionKey: "regression-migrate");

        var first = await StartAsync(client, processDefinitionId: v1);
        var second = await StartAsync(client, processDefinitionId: v1);

        var result = await PostJsonAsync(
            client,
            "/api/runtime/process-instances/batch/migrate",
            new
            {
                processInstanceIds = new[] { first, second },
                targetProcessDefinitionId = v2,
                activityMappings = new Dictionary<string, string> { ["task_approve"] = "task_review" },
                reason = "回归批量迁移",
            });

        Assert.Equal(2, result.GetProperty("succeeded").GetInt32());

        foreach (var id in new[] { first, second })
        {
            var instance = await GetJsonAsync(client, $"/api/runtime/process-instances/{id}");
            Assert.Equal(v2, instance.GetProperty("processDefinitionId").GetString());
        }
    }

    /// <summary>批量参数校验：空列表 / 缺目标定义 → 400 <c>validation_error</c>（先校验、不落任何写）。</summary>
    [Fact]
    public async Task BatchValidation_ShouldReturn400WithValidationCode()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        Reset();

        using var client = _factory.CreateClient();

        var emptyDelete = await PostJsonAsync(
            client,
            "/api/runtime/process-instances/batch/delete",
            new { processInstanceIds = Array.Empty<string>() },
            HttpStatusCode.BadRequest);

        Assert.Equal("validation_error", emptyDelete.GetProperty("code").GetString());

        var missingTarget = await PostJsonAsync(
            client,
            "/api/runtime/process-instances/batch/migrate",
            new { processInstanceIds = new[] { MissingInstanceId } },
            HttpStatusCode.BadRequest);

        Assert.Equal("validation_error", missingTarget.GetProperty("code").GetString());
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    private static async Task<string> DeployAsync(
        HttpClient client,
        string xml,
        string? processDefinitionKey = null)
    {
        using var content = new StringContent(xml, Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync(
            new Uri("/api/designer/deploy", UriKind.Relative), content, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"部署失败：{response.StatusCode} {body}");

        var definitionId = JsonDocument.Parse(body).RootElement
            .GetProperty("processDefinitionId").GetString()!;

        if (processDefinitionKey is not null)
        {
            Assert.StartsWith($"{processDefinitionKey}:", definitionId);
        }

        return definitionId;
    }

    private static async Task<string> StartAsync(
        HttpClient client,
        string? processDefinitionId = null,
        string? processDefinitionKey = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["businessKey"] = "BIZ-REGRESSION",
            ["startUserId"] = "tester",
        };

        if (processDefinitionId is not null)
        {
            payload["processDefinitionId"] = processDefinitionId;
        }

        if (processDefinitionKey is not null)
        {
            payload["processDefinitionKey"] = processDefinitionKey;
        }

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances", UriKind.Relative), payload, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"启动失败：{response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("processInstanceId").GetString()!;
    }

    private static async Task<string[]> TasksOfAsync(HttpClient client, string assignee) =>
        [.. (await JsonArrayAsync(client, $"/api/runtime/tasks?assignee={assignee}"))
            .Select(task => task.GetProperty("id").GetString()!)];

    private static async Task CompleteAsync(HttpClient client, string taskId)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/complete", UriKind.Relative),
            new { },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"完成任务失败：{response.StatusCode} {body}");
    }

    private static async Task<JsonElement> PostJsonAsync(
        HttpClient client,
        string path,
        object payload,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), payload, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == expected, $"{path} 期望 {(int)expected}，实际 {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> GetJsonAsync(
        HttpClient client,
        string path,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == expected, $"{path} 期望 {(int)expected}，实际 {(int)response.StatusCode} {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement[]> JsonArrayAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.IsSuccessStatusCode, $"{path} 失败：{response.StatusCode} {body}");

        return [.. JsonDocument.Parse(body).RootElement.EnumerateArray().Select(element => element.Clone())];
    }

    private const string SingleTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="regression-single" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" name="申请" activiti:assignee="applicant" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;

    private const string TwoTaskProcess = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="regression-two" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" name="申请" activiti:assignee="applicant" />
            <userTask id="review" name="审批" activiti:assignee="reviewer" />
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="review" />
            <sequenceFlow id="f3" sourceRef="review" targetRef="end" />
          </process>
        </definitions>
        """;

    private const string MigrateV1 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="regression-migrate" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_approve" />
            <userTask id="task_approve" name="审批" activiti:assignee="alice" />
            <sequenceFlow id="f2" sourceRef="task_approve" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;

    private const string MigrateV2 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="regression-migrate" isExecutable="true">
            <startEvent id="start" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="task_review" />
            <userTask id="task_review" name="复核" activiti:assignee="bob" />
            <sequenceFlow id="f2" sourceRef="task_review" targetRef="end" />
            <endEvent id="end" />
          </process>
        </definitions>
        """;
}
