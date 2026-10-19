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

        // ⑤-1 运行期写变量（纠错）：单值与批量
        using (var setVariable = await client.PutAsJsonAsync(
            new Uri($"/api/runtime/process-instances/{processInstanceId}/variables/days", UriKind.Relative),
            4,
            Ct))
        {
            Assert.True(setVariable.IsSuccessStatusCode, $"写变量失败：{setVariable.StatusCode}");
        }

        using (var setVariables = await client.PutAsJsonAsync(
            new Uri($"/api/runtime/process-instances/{processInstanceId}/variables", UriKind.Relative),
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["urgency"] = "high" },
            Ct))
        {
            Assert.True(setVariables.IsSuccessStatusCode, $"批量写变量失败：{setVariables.StatusCode}");
        }

        Assert.Equal(4L, await VariableAsync(client, processInstanceId, "days"));

        // ⑤-2 在办监控：按业务主键反查运行中的实例
        var running = await JsonArrayAsync(
            client,
            "/api/runtime/process-instances?businessKey=BIZ-P9-001");
        Assert.Contains(running, instance => instance.GetProperty("id").GetString() == processInstanceId);

        // ⑤-3 任务详情（含身份关系）
        var archiveDetail = await GetJsonAsync(client, $"/api/runtime/tasks/{archiveTask}");
        Assert.Equal("archivist", archiveDetail.GetProperty("assignee").GetString());
        Assert.Equal("archive", archiveDetail.GetProperty("taskDefinitionKey").GetString());
        Assert.Equal("BIZ-P9-001", archiveDetail.GetProperty("businessKey").GetString());

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

        // ⑧-1 跨实例已办：不要求 processInstanceId
        var myFinished = await JsonArrayAsync(
            client,
            "/api/runtime/history/tasks?assignee=archivist&onlyFinished=true");
        Assert.Single(myFinished);
        Assert.Equal(processInstanceId, myFinished[0].GetProperty("processInstanceId").GetString());

        // ⑧-2 历史实例检索：按业务主键 + 已结束
        var finishedInstances = await JsonArrayAsync(
            client,
            "/api/runtime/history/process-instances?businessKey=BIZ-P9-001&finished=true");
        Assert.Equal(processInstanceId, Assert.Single(finishedInstances).GetProperty("processInstanceId").GetString());

        // ⑧-3 历史变量：运行期写入的最终值仍可审计
        var historicVariables = await JsonArrayAsync(
            client,
            $"/api/runtime/history/process-instances/{processInstanceId}/variables");
        var days = historicVariables.Single(variable => variable.GetProperty("name").GetString() == "days");
        Assert.Equal(4L, days.GetProperty("value").GetInt64());
        Assert.Equal("long", days.GetProperty("type").GetString());

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
        Assert.Contains("/api/designer/validate", paths);
        Assert.Contains("/api/designer/definitions", paths);
        Assert.Contains("/api/designer/definitions/{processDefinitionKey}/versions", paths);
        Assert.Contains("/api/designer/definition/{processDefinitionId}/xml", paths);
        Assert.Contains("/api/designer/deployments", paths);
        Assert.Contains("/api/designer/deployments/{deploymentId}/resources", paths);
        Assert.Contains("/api/admin/info", paths);
        Assert.Contains("/health/live", paths);
        Assert.Contains("/health/ready", paths);
        Assert.Contains("/api/identity/users/{userId}/groups", paths);
        Assert.Contains("/api/identity/groups/{groupId}", paths);
        Assert.Contains("/api/identity/groups/{groupId}/users", paths);
        Assert.Contains("/api/runtime/process-instances", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/variables", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/variables/{name}", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/jump", paths);
        Assert.Contains("/api/runtime/process-instances/{processInstanceId}/diagram", paths);
        Assert.Contains("/api/runtime/tasks", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/identity-links", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/assignee", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/candidate-users", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/candidate-users/{userId}", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/candidate-groups", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/candidate-groups/{groupId}", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/delegate", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/resolve", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/form", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/claim", paths);
        Assert.Contains("/api/runtime/tasks/{taskId}/complete", paths);
        Assert.Contains("/api/runtime/tasks/stats", paths);
        Assert.Contains("/api/runtime/tasks/batch/assignee", paths);
        Assert.Contains("/api/runtime/process-instances/batch/delete", paths);
        Assert.Contains("/api/runtime/history/process-instances", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}/activities", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}/tasks", paths);
        Assert.Contains("/api/runtime/history/process-instances/{processInstanceId}/variables", paths);
        Assert.Contains("/api/runtime/history/tasks", paths);
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

    [Fact]
    public async Task ManagementEndpoints_AssignJumpVersions_ShouldWork()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        var processDefinitionId = await DeployAsync(client);

        // 定义列表分页总数（X-Total-Count）+ 按 key 查版本
        using (var definitions = await client.GetAsync(
            new Uri("/api/designer/definitions?firstResult=0&maxResults=1", UriKind.Relative), Ct))
        {
            Assert.True(definitions.IsSuccessStatusCode);
            Assert.Equal("1", definitions.Headers.GetValues("X-Total-Count").Single());
        }

        var versions = await JsonArrayAsync(client, "/api/designer/definitions/leave-approval/versions");
        Assert.Equal(processDefinitionId, Assert.Single(versions).GetProperty("processDefinitionId").GetString());

        // 启动实例：当前停在 applicant
        var processInstanceId = await StartAsync(client, processDefinitionId);
        var applicantTask = await SingleTaskOfAsync(client, "applicant");

        // 任务详情 + 身份关系
        var detail = await GetJsonAsync(client, $"/api/runtime/tasks/{applicantTask}");
        Assert.Equal("apply", detail.GetProperty("taskDefinitionKey").GetString());
        Assert.Equal("BIZ-P9-001", detail.GetProperty("businessKey").GetString());
        Assert.Equal(JsonValueKind.Array, detail.GetProperty("identityLinks").ValueKind);

        await JsonArrayAsync(client, $"/api/runtime/tasks/{applicantTask}/identity-links");

        // 转办：applicant → substitute
        using (var reassign = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{applicantTask}/assignee", UriKind.Relative),
            new { assignee = "substitute" },
            Ct))
        {
            Assert.True(reassign.IsSuccessStatusCode, $"转办失败：{reassign.StatusCode}");
        }

        Assert.Empty(await TasksOfAsync(client, "applicant"));
        Assert.Equal(applicantTask, await SingleTaskOfAsync(client, "substitute"));

        // 跳转：把实例从 apply 直接搬到 review
        using (var jump = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/process-instances/{processInstanceId}/jump", UriKind.Relative),
            new { targetActivityId = "review", reason = "集成测试跳转" },
            Ct))
        {
            var body = await jump.Content.ReadAsStringAsync(Ct);
            Assert.True(jump.IsSuccessStatusCode, $"跳转失败：{jump.StatusCode} {body}");
        }

        // 原待办已随跳转删除，实例停在 review（manager）
        Assert.Empty(await TasksOfAsync(client, "substitute"));
        await SingleTaskOfAsync(client, "manager");

        // 在办实例按业务主键可查
        var running = await JsonArrayAsync(client, "/api/runtime/process-instances?businessKey=BIZ-P9-001");
        Assert.Equal(processInstanceId, Assert.Single(running).GetProperty("id").GetString());
    }

    [Fact]
    public async Task TaskAdministration_UpdateDelegateCandidatesForm_ShouldWork()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        // 部署带表单声明与固定办理人的流程
        using (var content = new StringContent(FormProcess(), Encoding.UTF8, "text/xml"))
        using (var deploy = await client.PostAsync(
            new Uri("/api/designer/deploy", UriKind.Relative), content, Ct))
        {
            Assert.True(deploy.IsSuccessStatusCode, $"部署失败：{deploy.StatusCode}");
        }

        // 试校验（不落库）：合法 XML 返回 isValid = true
        using (var content = new StringContent(FormProcess(), Encoding.UTF8, "text/xml"))
        using (var validate = await client.PostAsync(
            new Uri("/api/designer/validate", UriKind.Relative), content, Ct))
        {
            var body = await validate.Content.ReadAsStringAsync(Ct);
            Assert.True(validate.IsSuccessStatusCode, $"校验失败：{validate.StatusCode} {body}");
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("isValid").GetBoolean());
        }

        using (var start = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances", UriKind.Relative),
            new { processDefinitionKey = "facade-form", startUserId = "tester" },
            Ct))
        {
            var body = await start.Content.ReadAsStringAsync(Ct);
            Assert.True(start.IsSuccessStatusCode, $"启动失败：{start.StatusCode} {body}");
        }

        var taskId = (await JsonArrayAsync(client, "/api/runtime/tasks?assignee=applicant"))[0]
            .GetProperty("id").GetString()!;

        // 表单元数据只读回传
        var form = await GetJsonAsync(client, $"/api/runtime/tasks/{taskId}/form");
        Assert.Equal("leaveForm", form.GetProperty("formKey").GetString());
        var properties = form.GetProperty("formProperties").EnumerateArray().ToList();
        Assert.Equal("days", Assert.Single(properties).GetProperty("id").GetString());

        // 修改任务字段
        using (var patch = await client.PatchAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}", UriKind.Relative),
            new { priority = 50, description = "请优先处理", category = "urgent" },
            Ct))
        {
            Assert.True(patch.IsSuccessStatusCode, $"修改任务失败：{patch.StatusCode}");
        }

        var updated = await GetJsonAsync(client, $"/api/runtime/tasks/{taskId}");
        Assert.Equal(50, updated.GetProperty("priority").GetInt32());
        Assert.Equal("请优先处理", updated.GetProperty("description").GetString());
        Assert.Equal("urgent", updated.GetProperty("category").GetString());

        // 委派 → PENDING → resolve 回到原办理人
        using (var delegateResponse = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/delegate", UriKind.Relative),
            new { userId = "deputy" },
            Ct))
        {
            Assert.True(delegateResponse.IsSuccessStatusCode, $"委派失败：{delegateResponse.StatusCode}");
        }

        var delegated = await GetJsonAsync(client, $"/api/runtime/tasks/{taskId}");
        Assert.Equal("deputy", delegated.GetProperty("assignee").GetString());
        Assert.Equal("applicant", delegated.GetProperty("owner").GetString());
        Assert.Equal("PENDING", delegated.GetProperty("delegationState").GetString());

        using (var resolve = await client.PostAsync(
            new Uri($"/api/runtime/tasks/{taskId}/resolve", UriKind.Relative), null, Ct))
        {
            Assert.True(resolve.IsSuccessStatusCode, $"resolve 失败：{resolve.StatusCode}");
        }

        var resolved = await GetJsonAsync(client, $"/api/runtime/tasks/{taskId}");
        Assert.Equal("applicant", resolved.GetProperty("assignee").GetString());
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("owner").ValueKind);
        Assert.Equal("RESOLVED", resolved.GetProperty("delegationState").GetString());

        // 候选关系只对「未签收」的任务可见：先取消认领
        using (var unclaim = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/claim", UriKind.Relative),
            new { userId = (string?)null },
            Ct))
        {
            Assert.True(unclaim.IsSuccessStatusCode, $"取消认领失败：{unclaim.StatusCode}");
        }

        // 候选用户增删
        using (var add = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/candidate-users", UriKind.Relative),
            new { userId = "alice" },
            Ct))
        {
            Assert.True(add.IsSuccessStatusCode, $"加候选人失败：{add.StatusCode}");
        }

        Assert.Equal(taskId, Assert.Single(await JsonArrayAsync(
            client, "/api/runtime/tasks?candidateUser=alice")).GetProperty("id").GetString());

        using (var remove = await client.DeleteAsync(
            new Uri($"/api/runtime/tasks/{taskId}/candidate-users/alice", UriKind.Relative), Ct))
        {
            Assert.True(remove.IsSuccessStatusCode, $"移除候选人失败：{remove.StatusCode}");
        }

        Assert.Empty(await JsonArrayAsync(client, "/api/runtime/tasks?candidateUser=alice"));

        // 候选组增删
        using (var addGroup = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{taskId}/candidate-groups", UriKind.Relative),
            new { groupId = "finance" },
            Ct))
        {
            Assert.True(addGroup.IsSuccessStatusCode, $"加候选组失败：{addGroup.StatusCode}");
        }

        Assert.Single(await JsonArrayAsync(client, "/api/runtime/tasks?candidateGroup=finance"));

        using (var removeGroup = await client.DeleteAsync(
            new Uri($"/api/runtime/tasks/{taskId}/candidate-groups/finance", UriKind.Relative), Ct))
        {
            Assert.True(removeGroup.IsSuccessStatusCode, $"移除候选组失败：{removeGroup.StatusCode}");
        }

        Assert.Empty(await JsonArrayAsync(client, "/api/runtime/tasks?candidateGroup=finance"));

        // 身份模块只读端点（默认空实现：用户组为空，组查询 404）
        Assert.Empty(await JsonArrayAsync(client, "/api/identity/users/tester/groups"));

        using (var missingGroup = await client.GetAsync(
            new Uri("/api/identity/groups/no-such-group", UriKind.Relative), Ct))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missingGroup.StatusCode);
        }
    }

    [Fact]
    public async Task OperationalEndpoints_DeploymentsStatsBatchHealth_ShouldWork()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        var approvalDefinitionId = await DeployAsync(client);

        using (var content = new StringContent(FormProcess(), Encoding.UTF8, "text/xml"))
        using (var deploy = await client.PostAsync(
            new Uri("/api/designer/deploy", UriKind.Relative), content, Ct))
        {
            Assert.True(deploy.IsSuccessStatusCode, $"部署表单流程失败：{deploy.StatusCode}");
        }

        // 两个流程各启动一个实例，起始任务都落在 applicant
        await StartAsync(client, approvalDefinitionId);

        string formInstanceId;

        using (var start = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances", UriKind.Relative),
            new { processDefinitionKey = "facade-form", businessKey = "BIZ-FORM-1", startUserId = "tester" },
            Ct))
        {
            var body = await start.Content.ReadAsStringAsync(Ct);
            Assert.True(start.IsSuccessStatusCode, $"启动失败：{start.StatusCode} {body}");
            formInstanceId = JsonDocument.Parse(body).RootElement.GetProperty("processInstanceId").GetString()!;
        }

        // 部署维度：两次部署 + 各自的资源清单
        var deployments = await JsonArrayAsync(client, "/api/designer/deployments");
        Assert.Equal(2, deployments.Length);

        var deploymentId = deployments[0].GetProperty("deploymentId").GetString()!;
        var resources = await JsonArrayAsync(
            client, $"/api/designer/deployments/{deploymentId}/resources");
        Assert.Single(resources);

        // 统计：applicant 名下 2 条待办
        var stats = await GetJsonAsync(client, "/api/runtime/tasks/stats?assignee=applicant");
        Assert.Equal(2, stats.GetProperty("total").GetInt32());
        Assert.Equal(0, stats.GetProperty("overdue").GetInt32());

        var applicantTasks = await JsonArrayAsync(client, "/api/runtime/tasks?assignee=applicant");
        Assert.Equal(2, applicantTasks.Length);

        // 把一条任务改成逾期 → 统计应变成 1
        using (var patch = await client.PatchAsJsonAsync(
            new Uri($"/api/runtime/tasks/{applicantTasks[0].GetProperty("id").GetString()}", UriKind.Relative),
            new { dueDate = DateTime.UtcNow.AddDays(-1) },
            Ct))
        {
            Assert.True(patch.IsSuccessStatusCode, $"改到期时间失败：{patch.StatusCode}");
        }

        stats = await GetJsonAsync(client, "/api/runtime/tasks/stats?assignee=applicant");
        Assert.Equal(1, stats.GetProperty("overdue").GetInt32());

        // 批量转办：两条都成功
        using (var batch = await client.PostAsJsonAsync(
            new Uri("/api/runtime/tasks/batch/assignee", UriKind.Relative),
            new
            {
                taskIds = applicantTasks.Select(task => task.GetProperty("id").GetString()).ToArray(),
                assignee = "backup",
            },
            Ct))
        {
            var body = await batch.Content.ReadAsStringAsync(Ct);
            Assert.True(batch.IsSuccessStatusCode, $"批量转办失败：{batch.StatusCode} {body}");

            var result = JsonDocument.Parse(body).RootElement;
            Assert.Equal(2, result.GetProperty("total").GetInt32());
            Assert.Equal(2, result.GetProperty("succeeded").GetInt32());
        }

        Assert.Equal(2, (await JsonArrayAsync(client, "/api/runtime/tasks?assignee=backup")).Length);

        // 乐观并发：过期 revision → 409；当前 revision → 成功
        var targetTaskId = (await JsonArrayAsync(client, "/api/runtime/tasks?assignee=backup"))[0]
            .GetProperty("id").GetString()!;

        var detail = await GetJsonAsync(client, $"/api/runtime/tasks/{targetTaskId}");
        var revision = detail.GetProperty("revision").GetInt32();

        using (var stale = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{targetTaskId}/complete", UriKind.Relative),
            new { expectedRevision = revision - 1 },
            Ct))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, stale.StatusCode);
        }

        using (var complete = await client.PostAsJsonAsync(
            new Uri($"/api/runtime/tasks/{targetTaskId}/complete", UriKind.Relative),
            new { expectedRevision = revision },
            Ct))
        {
            Assert.True(complete.IsSuccessStatusCode, $"按版本完成失败：{complete.StatusCode}");
        }

        // 批量删除：表单流程实例
        using (var batchDelete = await client.PostAsJsonAsync(
            new Uri("/api/runtime/process-instances/batch/delete", UriKind.Relative),
            new { processInstanceIds = new[] { formInstanceId }, deleteReason = "集成测试清理" },
            Ct))
        {
            var body = await batchDelete.Content.ReadAsStringAsync(Ct);
            Assert.True(batchDelete.IsSuccessStatusCode, $"批量删除失败：{batchDelete.StatusCode} {body}");
            Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("succeeded").GetInt32());
        }

        var running = await JsonArrayAsync(client, "/api/runtime/process-instances");
        Assert.DoesNotContain(running, instance => instance.GetProperty("id").GetString() == formInstanceId);

        // 健康 / 运维
        using (var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct))
        {
            Assert.True(live.IsSuccessStatusCode);
        }

        using (var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct))
        {
            Assert.True(ready.IsSuccessStatusCode);
            var body = await ready.Content.ReadAsStringAsync(Ct);
            Assert.Equal("ready", JsonDocument.Parse(body).RootElement.GetProperty("status").GetString());
        }

        var info = await GetJsonAsync(client, "/api/admin/info");
        Assert.False(string.IsNullOrEmpty(info.GetProperty("schemaVersion").GetString()));
        Assert.True(info.GetProperty("historyEnabled").GetBoolean());
    }

    [Fact]
    public async Task Timestamps_ShouldSerializeWithUtcDesignator()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        using var client = _factory.CreateClient();

        var processDefinitionId = await DeployAsync(client);
        var instanceId = await StartAsync(client, processDefinitionId);

        var instance = await GetJsonAsync(client, $"/api/runtime/process-instances/{instanceId}");
        var startTime = instance.GetProperty("startTime").GetString();

        Assert.NotNull(startTime);
        Assert.EndsWith("Z", startTime, StringComparison.Ordinal);

        var parsed = DateTime.Parse(startTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
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

    /// <summary>带表单声明（formKey + formProperty）的单节点流程，用于任务管理端点。</summary>
    private static string FormProcess() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:activiti="http://activiti.org/bpmn"
                     targetNamespace="http://netbpm/test">
          <process id="facade-form" name="表单流程" isExecutable="true">
            <startEvent id="start" />
            <userTask id="apply" name="申请" activiti:assignee="applicant" activiti:formKey="leaveForm">
              <extensionElements>
                <activiti:formProperty id="days" name="天数" type="long" variable="days" required="true" />
              </extensionElements>
            </userTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="apply" />
            <sequenceFlow id="f2" sourceRef="apply" targetRef="end" />
          </process>
        </definitions>
        """;

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
