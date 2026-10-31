using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Platform;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 参与者维度（<c>ACT_HI_IDENTITYLINK</c> 参与人索引）端到端——让「我的已办」与
/// 「我参与过的单据」第一次有了<b>服务端</b>的答案。
/// </summary>
/// <remarks>
/// <para>
/// 必须跑在 <b>MySQL + 历史开启</b>的宿主上： MEMORY 宿主没有历史，
/// <c>ACT_HI_IDENTITYLINK</c> / <c>ACT_HI_TASKINST</c> 一行都不会写，
/// 而这两个列表恰恰全是「已经结束或已经办过」的数据——内存宿主上只会得到空列表，
/// 那是正确但不完整的答案。
/// </para>
/// <para>
/// 三条有代表性的参与路径各占一个断言：
/// <list type="bullet">
///   <item><description><b>发起人</b>：<c>ACT_HI_PROCINST.START_USER_ID_</c>；</description></item>
///   <item><description><b>审批人</b>：历史任务的 <c>ASSIGNEE_</c>（已办，可能实例都已结束）；</description></item>
///   <item><description><b>候选命中</b>：历史身份关系，含<b>候选组</b>——只有这一条会踩到
///     「任务级关系行没有实例 id」那个坑（要靠 <c>TASK_ID_</c> 反查）。</description></item>
/// </list>
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class ParticipantIndexTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "participant_demo";

    private readonly RestEngineFactory _factory;

    public ParticipantIndexTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 「我参与过的单据」：发起人 / 审批人 / 候选人三种关系各自能被索引命中，
    /// 且无关人员查不到这条单据。
    /// </summary>
    [Fact]
    public async Task InvolvedUser_ShouldCoverStarterApproverAndCandidate()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "BILL-100", "lisi");

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "participant-involved", [PlatformScopes.InstanceRead]);

        // ① 发起人
        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances?involvedUser=zhangsan", secret))
        {
            var body = await ReadJsonAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(items);
            Assert.Equal(instanceId, items[0].GetProperty("instanceId").GetString());
            Assert.Equal("STARTER", items[0].GetProperty("myRoles")[0].GetString());
        }

        // ② 审批人（任务已指派给 lisi，但还没办）
        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances?involvedUser=lisi", secret))
        {
            var body = await ReadJsonAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Single(items);
            Assert.Equal(instanceId, items[0].GetProperty("instanceId").GetString());
            Assert.Contains("APPROVER", Roles(items[0]));
        }

        // ③ 无关人员：查不到这条单据（不是「返回全部」这一最典型的越权形状）
        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances?involvedUser=zhaoliu", secret))
        {
            var body = await ReadJsonAsync(response);

            Assert.Empty(body.GetProperty("items").EnumerateArray().ToList());
        }

        // 推进到复核环节：候选人 wangwu 与候选组 auditors 才登场
        await CompleteFirstTaskAsync(engine, instanceId, "lisi");

        using (var response = await SendGetAsync(
            client, $"/workflow/v1/instances?involvedUser=wangwu", secret))
        {
            var body = await ReadJsonAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Single(items);
            Assert.Equal(instanceId, items[0].GetProperty("instanceId").GetString());
            Assert.Contains("CANDIDATE", Roles(items[0]));
        }

        // ④ 候选组命中的是「组织」而不是「人」：服务令牌没有 groups 声明，走 HTTP 测不到，
        // 因此直接调服务验证 SQL 这一层（任务级行没有 PROC_INST_ID_，只能靠 TASK_ID_ 反查）
        using var scope = _factory.Services.CreateScope();
        var participation = scope.ServiceProvider.GetRequiredService<IInstanceParticipation>();
        var involvement = await participation.FindAsync("nobody", ["auditors"], Ct);

        Assert.True(involvement.IsInvolved(instanceId), "候选组命中应该能被参与者索引解析到实例。");
        Assert.Contains(ParticipationRole.Candidate, involvement.RolesFor(instanceId));
    }

    /// <summary>
    /// 「我的已办」：<c>GET /workflow/v1/tasks?scope=DONE</c> 只回来自己办过的任务，且带办结时间。
    /// </summary>
    [Fact]
    public async Task DoneScope_ShouldReturnMyCompletedTasks_WithCompletedAt()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "zhangsan", "BILL-200", "lisi");
        await CompleteFirstTaskAsync(engine, instanceId, "lisi");

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "participant-done",
            [PlatformScopes.TaskRead, PlatformScopes.HistoryRead, PlatformScopes.TaskSubmitOnBehalf]);

        // lisi 办过一张单；wangwu 只是复核候选人（还没办），不应出现在他的已办里
        using (var response = await SendGetAsync(
            client, "/workflow/v1/tasks?scope=DONE", secret, "lisi"))
        {
            var body = await ReadJsonAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Single(items);
            Assert.Equal(instanceId, items[0].GetProperty("instanceId").GetString());
            Assert.Equal(ApprovalKey, items[0].GetProperty("processKey").GetString());
            Assert.Equal("主管审批", items[0].GetProperty("activityName").GetString());
            Assert.Equal("lisi", items[0].GetProperty("assignee").GetString());
            Assert.Equal("BILL-200", items[0].GetProperty("businessKey").GetString());

            // 已办的标识：有办结时间
            Assert.NotEqual(JsonValueKind.Null, items[0].GetProperty("completedAt").ValueKind);
        }

        // wangwu 还没有办过任何环节
        using (var response = await SendGetAsync(
            client, "/workflow/v1/tasks?scope=DONE", secret, "wangwu"))
        {
            var body = await ReadJsonAsync(response);

            Assert.Empty(body.GetProperty("items").EnumerateArray().ToList());
        }

        // 他的复核待办仍在待办箱里（已办与待办是两个数据源，不该互相污染）
        using (var response = await SendGetAsync(
            client, "/workflow/v1/tasks?scope=MINE", secret, "wangwu"))
        {
            var body = await ReadJsonAsync(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();

            Assert.Single(items);
            Assert.Equal("复核", items[0].GetProperty("activityName").GetString());
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("completedAt").ValueKind);
        }
    }

    /// <summary>
    /// 看别人的已办 / 参与记录需要历史全读权限：没有则返回 403，而不是「返回自己的」。
    /// </summary>
    [Fact]
    public async Task InvolvedUser_ShouldRejectOtherPeople_WithoutCrossPersonScope()
    {
        SkipUnlessReady();
        Reset();

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        await StartAsync(engine, "zhangsan", "BILL-300", "lisi");

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(
            client,
            "participant-scope",
            [PlatformScopes.InstanceRead, PlatformScopes.TaskSubmitOnBehalf]);

        using var response = await SendGetAsync(
            client, "/workflow/v1/instances?involvedUser=lisi", secret, "zhangsan");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private static IEnumerable<string> Roles(JsonElement item) =>
        item.GetProperty("myRoles").EnumerateArray().Select(role => role.GetString()!);

    private static void Reset()
    {
        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        MySqlTestDatabase.Reset(session);
    }

    /// <summary>部署两环节审批流程：主管审批（固定办理人）→ 复核（候选用户 + 候选组）。</summary>
    private static async Task DeployAsync(IProcessEngine engine)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="参与者演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_owner" />
                <bpmn:userTask id="task_owner" name="主管审批" activiti:assignee="${owner}" />
                <bpmn:sequenceFlow id="flow2" sourceRef="task_owner" targetRef="task_review" />
                <bpmn:userTask id="task_review" name="复核"
                               activiti:candidateUsers="wangwu"
                               activiti:candidateGroups="auditors" />
                <bpmn:sequenceFlow id="flow3" sourceRef="task_review" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, ApprovalKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> StartAsync(
        IProcessEngine engine,
        string startedBy,
        string businessKey,
        string owner) =>
        await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            businessKey: businessKey,
            startUserId: startedBy,
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = owner },
            ct: Ct);

    /// <summary>办掉当前环节（第一个环节 = 主管审批）。</summary>
    private static async Task CompleteFirstTaskAsync(IProcessEngine engine, string instanceId, string approver)
    {
        var task = Assert.Single(
            await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        Assert.Equal(approver, task.Assignee);

        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = "1", ["comment"] = "同意" },
            ct: Ct);
    }

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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
