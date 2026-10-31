using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 实例列表（<c>GET /workflow/v1/instances</c>）端到端：过滤（流程 / 业务主键 / 状态）、
/// 游标分页（不重不漏）、人员范围（非管理员只能看自己的）、以及 scope / 参数校验。
/// </summary>
/// <remarks>
/// <para>
/// 跑在内存宿主（历史关闭）上：列表的运行态一侧来自 <c>ACT_RU_EXECUTION</c> 的根执行，
/// 因此这里只覆盖<b>运行中</b>的实例。已结束实例需要历史，见
/// <see cref="InstanceListCompletedTests"/>（MySQL + 历史宿主）。
/// </para>
/// <para>
/// 分页用例刻意把每页压到 2 条：游标的 tie-break（开始时间相同时按实例 id）只有在同刻多条时
/// 才会暴露问题，而真正的生产事故恰恰出在这种边界上。
/// </para>
/// </remarks>
public sealed class InstanceListTests
{
    private const string DefaultTenant = "default";
    private const string LeaveKey = "list_leave";
    private const string OrderKey = "list_order";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_ShouldReturnMyInstances_WithPendingTaskInfo()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        await harness.StartAsync(LeaveKey, "zhangsan");
        await harness.StartAsync(LeaveKey, "zhangsan");

        using var response = await harness.GetAsync("/workflow/v1/instances", "zhangsan");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(LeaveKey, items[0].GetProperty("processKey").GetString());
        Assert.Equal("RUNNING", items[0].GetProperty("status").GetString());
        Assert.Equal("zhangsan", items[0].GetProperty("startedBy").GetString());

        // 运行中实例：当前环节 + 已认领待办的办理人
        Assert.Equal("task_approve", items[0].GetProperty("currentActivityIds")[0].GetString());
        Assert.Equal("lisi", items[0].GetProperty("currentAssignees")[0].GetString());

        // 运行中实例没有结论（结论只对已结束的实例给出）
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("conclusion").ValueKind);

        // 倒序：最新的在前
        var first = DateTimeOffset.Parse(
            items[0].GetProperty("startedAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var second = DateTimeOffset.Parse(
            items[1].GetProperty("startedAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(first >= second, "列表应按下发时间倒序（最新的在前）。");
    }

    [Fact]
    public async Task List_ShouldFilterByProcessKey()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);
        await harness.DeployAsync(OrderKey);

        await harness.StartAsync(LeaveKey, "zhangsan");
        await harness.StartAsync(OrderKey, "zhangsan");

        using var response = await harness.GetAsync(
            $"/workflow/v1/instances?processKey={OrderKey}",
            "zhangsan");
        var body = await ReadJsonAsync(response);

        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(OrderKey, items[0].GetProperty("processKey").GetString());
    }

    [Fact]
    public async Task List_ShouldFilterByBusinessKey_AndStatus()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        await harness.StartAsync(LeaveKey, "zhangsan", businessKey: "BILL-A");
        await harness.StartAsync(LeaveKey, "zhangsan", businessKey: "BILL-B");

        using (var byBusinessKey = await harness.GetAsync(
            "/workflow/v1/instances?businessKey=BILL-B",
            "zhangsan"))
        {
            var body = await ReadJsonAsync(byBusinessKey);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Single(items);
            Assert.Equal("BILL-B", items[0].GetProperty("businessKey").GetString());
        }

        // status=RUNNING 命中；status=COMPLETED 没有（内存宿主下没有已结束实例）
        using (var running = await harness.GetAsync("/workflow/v1/instances?status=RUNNING", "zhangsan"))
        {
            var body = await ReadJsonAsync(running);

            Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        }

        using (var completed = await harness.GetAsync("/workflow/v1/instances?status=COMPLETED", "zhangsan"))
        {
            var body = await ReadJsonAsync(completed);

            Assert.Equal(0, body.GetProperty("items").GetArrayLength());
            Assert.False(body.GetProperty("hasMore").GetBoolean());
        }
    }

    /// <summary>
    /// 非管理员：不传 <c>startedBy</c> 时<b>默认只返回本人</b>发起的实例。
    /// </summary>
    /// <remarks>
    /// 这是最容易漏掉的一条授权：漏了它，接入方随便调一次列表就能拿到整个租户的单据，
    /// 而且不会有任何人报 bug（功能「看起来是好的」）。
    /// </remarks>
    [Fact]
    public async Task List_ShouldNarrowToCaller_WhenNotAdmin()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        await harness.StartAsync(LeaveKey, "zhangsan");
        await harness.StartAsync(LeaveKey, "zhangsan");
        await harness.StartAsync(LeaveKey, "lisi");

        using var response = await harness.GetAsync("/workflow/v1/instances", "zhangsan");
        var body = await ReadJsonAsync(response);

        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("zhangsan", item.GetProperty("startedBy").GetString()));
    }

    [Fact]
    public async Task List_Should403_WhenAskingForOthersInstances()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        await harness.StartAsync(LeaveKey, "lisi");

        using var response = await harness.GetAsync("/workflow/v1/instances?startedBy=lisi", "zhangsan");
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// 持跨人查看权限（<c>workflow:history:read:all</c>）时可以看别人的单据。
    /// </summary>
    /// <remarks>
    /// 用 <c>workflow:history:read:all</c> 而不是 <c>netbpm.admin</c>：后者是旧宿主的超级管理范围，
    /// 不在 <see cref="PlatformScopes.All"/> 里，注册客户端时会被 <c>client.scope_unknown</c> 拒绝。
    /// 两个都能让 <see cref="InstanceListCatalog"/> 放开人员范围，但只有前者是平台面向外发放的。
    /// </remarks>
    [Fact]
    public async Task List_ShouldSeeOthersInstances_WithCrossPersonScope()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead, PlatformScopes.HistoryReadAll]);
        await harness.DeployAsync(LeaveKey);

        await harness.StartAsync(LeaveKey, "zhangsan");
        await harness.StartAsync(LeaveKey, "lisi");

        // 不传 startedBy：有跨人权限 → 不做人员收窄
        using var all = await harness.GetAsync("/workflow/v1/instances", "zhangsan");
        var allBody = await ReadJsonAsync(all);

        Assert.Equal(2, allBody.GetProperty("items").GetArrayLength());

        // 显式指定别人也允许
        using var byOthers = await harness.GetAsync("/workflow/v1/instances?startedBy=lisi", "zhangsan");
        var filtered = await ReadJsonAsync(byOthers);

        Assert.Single(filtered.GetProperty("items").EnumerateArray().ToList());
    }

    [Fact]
    public async Task List_ShouldPageWithoutDuplicates()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        for (var index = 0; index < 3; index++)
        {
            await harness.StartAsync(LeaveKey, "zhangsan", businessKey: $"BILL-P{index}");
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        while (pages < 5)
        {
            var path = cursor is null
                ? "/workflow/v1/instances?limit=2"
                : $"/workflow/v1/instances?limit=2&cursor={Uri.EscapeDataString(cursor)}";

            using var response = await harness.GetAsync(path, "zhangsan");
            var body = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var items = body.GetProperty("items").EnumerateArray().ToList();

            seen.AddRange(items.Select(item => item.GetProperty("instanceId").GetString()!));

            pages++;

            if (!body.GetProperty("hasMore").GetBoolean())
            {
                Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);

                break;
            }

            cursor = body.GetProperty("nextCursor").GetString();

            Assert.False(string.IsNullOrWhiteSpace(cursor), "hasMore=true 时必须给出 nextCursor。");
        }

        // 3 条分两页（2 + 1），不重不漏
        Assert.Equal(2, pages);
        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task List_Should400_WhenCursorMalformed()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        using var response = await harness.GetAsync(
            "/workflow/v1/instances?cursor=not-a-cursor",
            "zhangsan");
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
    }

    /// <summary>升序被明确拒绝，而不是安静地返回一个「看起来对、其实是全表扫描」的分页。</summary>
    [Fact]
    public async Task List_Should400_WhenAscending()
    {
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceRead]);
        await harness.DeployAsync(LeaveKey);

        using var response = await harness.GetAsync(
            "/workflow/v1/instances?direction=ASCENDING",
            "zhangsan");
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.RequestMalformed, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task List_Should403_WithoutReadScope()
    {
        // 只授予发起，不授予读 → 列表 403
        using var harness = await Harness.StartAsync([PlatformScopes.InstanceStart]);
        await harness.DeployAsync(LeaveKey);

        using var response = await harness.GetAsync("/workflow/v1/instances", subject: null);
        var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.AuthInsufficientScope, problem.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>内存宿主（历史关闭）+ 已注册客户端 + 已部署流程的测试夹具。</summary>
    private sealed class Harness : IDisposable
    {
        private Harness(WebApplicationFactory<Program> host, HttpClient client, string secret)
        {
            Host = host;
            Client = client;
            Secret = secret;
        }

        private WebApplicationFactory<Program> Host { get; }

        private HttpClient Client { get; }

        private string Secret { get; }

        private IProcessEngine Engine => Host.Services.GetRequiredService<IProcessEngine>();

        internal static async Task<Harness> StartAsync(string[] scopes)
        {
            var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

            var client = host.CreateClient();

            try
            {
                // 带 X-Subject-Id 的请求在鉴权中间件就要求 onbehalf（代客调用）；
                // 这是中间件的独立规则，与端点自身的 scope 闸门不相干，所以统一补上。
                var secret = await RegisterAsync(client, [.. scopes, PlatformScopes.TaskSubmitOnBehalf]);

                return new Harness(host, client, secret);
            }
            catch
            {
                client.Dispose();
                await host.DisposeAsync().ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>部署单环节审批流程（startEvent → 审批 userTask（固定办理人 lisi）→ endEvent）。</summary>
        internal async Task DeployAsync(string processKey)
        {
            var xml = $$"""
                <?xml version="1.0" encoding="UTF-8"?>
                <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                                  xmlns:activiti="http://activiti.org/bpmn"
                                  targetNamespace="netbpm-test">
                  <bpmn:process id="{{processKey}}" name="列表演示" isExecutable="true">
                    <bpmn:startEvent id="start" name="Start" />
                    <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                    <bpmn:userTask id="task_approve" name="审批" activiti:assignee="lisi" />
                    <bpmn:sequenceFlow id="flow2" sourceRef="task_approve" targetRef="end" />
                    <bpmn:endEvent id="end" name="End" />
                  </bpmn:process>
                </bpmn:definitions>
                """;

            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            await Engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);
        }

        /// <summary>直接走引擎发起：列表用例关心的是「已存在的实例」，不需要过平台发起端点的参数校验。</summary>
        internal async Task<string> StartAsync(string processKey, string startedBy, string? businessKey = null) =>
            await Engine.StartProcessInstanceByKeyAsync(
                processKey,
                businessKey: businessKey,
                startUserId: startedBy,
                ct: Ct);

        internal Task<HttpResponseMessage> GetAsync(string path, string? subject)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.Add("X-Api-Key", Secret);

            if (subject is not null)
            {
                request.Headers.Add("X-Subject-Id", subject);
            }

            return Client.SendAsync(request, Ct);
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
        }

        private static async Task<string> RegisterAsync(HttpClient client, string[] scopes)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
                new { name = "list-client", tenantId = DefaultTenant, scopes },
                Ct);

            var body = await response.Content.ReadAsStringAsync(Ct);

            Assert.True(
                response.StatusCode == HttpStatusCode.Created,
                $"注册客户端失败：{response.StatusCode} {body}");

            using var json = JsonDocument.Parse(body);

            return json.RootElement.GetProperty("clientSecret").GetString()!;
        }
    }
}

/// <summary>
/// 实例列表的<b>已结束</b>一侧——必须开历史，因此跑在 MySQL 宿主上。
/// </summary>
/// <remarks>
/// 内存宿主（历史关闭）看不见已结束的实例：运行态行已删、<c>ACT_HI_PROCINST</c> 没开。
/// 而「我的单据」列表里绝大多数条目恰恰是已结束的，所以这条路径必须单独覆盖，
/// 否则列表功能在最常用的场景上是「看起来能用、实际是空的」。
/// </remarks>
[Collection("MySql")]
public sealed class InstanceListCompletedTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "list_completed_demo";

    private readonly RestEngineFactory _factory;

    public InstanceListCompletedTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_ShouldIncludeCompletedInstance_WithConclusion()
    {
        SkipUnlessReady();

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var runningId = await StartAsync(engine, "lisi", "BILL-RUNNING");

        var completedId = await StartAsync(engine, "lisi", "BILL-DONE");
        var task = Assert.Single(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: completedId), Ct));

        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = "1", ["comment"] = "同意" },
            ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "list-completed");

        // 不带 status：两侧合并后应同时看到在办与已办
        using (var all = await SendGetAsync(client, "/workflow/v1/instances", secret))
        {
            var body = await ReadJsonAsync(all);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Equal(2, items.Count);

            var completed = items.Single(item => item.GetProperty("instanceId").GetString() == completedId);

            Assert.Equal("COMPLETED", completed.GetProperty("status").GetString());
            Assert.NotEqual(JsonValueKind.Null, completed.GetProperty("endedAt").ValueKind);
            Assert.Equal("BILL-DONE", completed.GetProperty("businessKey").GetString());

            // 结束实例没有当前环节 / 办理人，但有结论（与实例详情同一套推导规则）
            Assert.Empty(completed.GetProperty("currentActivityIds").EnumerateArray());
            Assert.Empty(completed.GetProperty("currentAssignees").EnumerateArray());

            var conclusion = completed.GetProperty("conclusion");

            Assert.Equal("APPROVE", conclusion.GetProperty("semantic").GetString());
            Assert.Equal("已通过", conclusion.GetProperty("label").GetString());
            Assert.Equal("LAST_DECISION", conclusion.GetProperty("source").GetString());

            // 在办的那条仍在列表里，且结论为空
            var running = items.Single(item => item.GetProperty("instanceId").GetString() == runningId);

            Assert.Equal("RUNNING", running.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, running.GetProperty("conclusion").ValueKind);
        }

        // status=COMPLETED 只剩已办；status=RUNNING 只剩在办
        using (var completedOnly = await SendGetAsync(
            client,
            "/workflow/v1/instances?status=COMPLETED",
            secret))
        {
            var body = await ReadJsonAsync(completedOnly);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Single(items);
            Assert.Equal(completedId, items[0].GetProperty("instanceId").GetString());
        }

        using (var runningOnly = await SendGetAsync(
            client,
            "/workflow/v1/instances?status=RUNNING",
            secret))
        {
            var body = await ReadJsonAsync(runningOnly);

            Assert.Single(body.GetProperty("items").EnumerateArray().ToList());
        }
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        try
        {
            using var session = MySqlTestDatabase.CreateSessionFactory().Create();
            var count = session.Connection.ExecuteScalar<long>(
                "select count(*) from information_schema.tables " +
                "where table_schema = database() and table_name in ('nb_tenant', 'nb_api_client')");

            Assert.SkipUnless(
                count >= 2,
                "本库缺平台接入表（nb_tenant / nb_api_client）。请把 NETBPM_MYSQL_CONNECTION 指向 netbpm_ittest，" +
                "或对目标库执行 DbMigrate migrate 补齐 nb_* 表。");
        }
        catch
        {
            Assert.Skip("无法探测平台接入表，跳过。");
        }
    }

    private static async Task DeployAsync(IProcessEngine engine)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="列表（已办）演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="${approver}" />
                <bpmn:sequenceFlow id="flow2" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(IProcessEngine engine, string approver, string businessKey) =>
        await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            businessKey: businessKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["approver"] = approver },
            ct: Ct);

    private static async Task<string> RegisterAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name,
                tenantId = DefaultTenant,
                // 纯服务令牌（无 subject）：人员范围不做收窄，能看到全租户的在办与已办
                scopes = new[] { PlatformScopes.InstanceRead },
            },
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
