using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Platform.Contracts;
using NetBpm.Runtime;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 结束实例的实例读模型（<c>GET /workflow/v1/instances/{id}</c>）——在 <b>MySQL + 历史开启</b>的宿主上
/// 覆盖「实例结束后 <c>status</c> 判定 + 结论推导」。这是内存模式（历史关闭）覆盖不到的盲区：
/// 实例一结束，运行态执行行被删、<c>ACT_HI_*</c> 又没开，<see cref="IInstanceCatalog"/> 只能 404。
/// </summary>
/// <remarks>
/// <para>
/// 结论推导（<see cref="InstanceCatalog"/> 的<b>最后决定</b>这一级）依赖 <c>result</c> 流程变量：
/// 提交审批时 <c>result</c> 落为选项 id（<c>1</c> 通过 / <c>2</c> 拒绝），实例结束后据此推导语义与文案。
/// </para>
/// <para>
/// 与 <see cref="InstanceCatalogTests"/>（内存宿主，只覆盖运行中实例）互补；两者合起来才是实例读模型的完整覆盖。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class InstanceConclusionTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";
    private const string ApprovalKey = "conclusion_demo";

    private readonly RestEngineFactory _factory;

    public InstanceConclusionTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CompletedInstance_ShouldReportCompleted_AndDeriveApproveConclusion()
    {
        SkipUnlessReady();

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "lisi");
        var task = Assert.Single(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = "1", ["comment"] = "同意" },
            ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "conclusion-approve", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(instanceId, body.GetProperty("instanceId").GetString());
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());

        // 结束实例：没有活动待办 / 当前环节，有结束时间
        Assert.Empty(body.GetProperty("currentActivityIds").EnumerateArray());
        Assert.Empty(body.GetProperty("tasks").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("endedAt").ValueKind);

        // 结论由 result=1 推导（最后决定这一级）
        var conclusion = body.GetProperty("conclusion");
        Assert.Equal("APPROVE", conclusion.GetProperty("semantic").GetString());
        Assert.Equal("1", conclusion.GetProperty("optionId").GetString());
        Assert.Equal("已通过", conclusion.GetProperty("label").GetString());
        Assert.Equal("LAST_DECISION", conclusion.GetProperty("source").GetString());
    }

    [Fact]
    public async Task CompletedInstance_ShouldDeriveRejectConclusion_FromResultVariable()
    {
        SkipUnlessReady();

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "lisi");
        var task = Assert.Single(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["result"] = "2", ["comment"] = "不同意" },
            ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "conclusion-reject", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());

        // result=2 → REJECT / 已驳回，证明结论确实跟着 result 变量走，而不是硬编码「通过」
        var conclusion = body.GetProperty("conclusion");
        Assert.Equal("REJECT", conclusion.GetProperty("semantic").GetString());
        Assert.Equal("2", conclusion.GetProperty("optionId").GetString());
        Assert.Equal("已驳回", conclusion.GetProperty("label").GetString());
        Assert.Equal("LAST_DECISION", conclusion.GetProperty("source").GetString());
    }

    /// <summary>
    /// 三级回落的第一级：流程显式声明（变量 <c>netbpm_outcome</c>）优先于最后一条审批决定。
    /// </summary>
    /// <remarks>
    /// 本用例刻意让两者<b>矛盾</b>（<c>result=1</c> 说通过、<c>netbpm_outcome=REJECT</c> 说驳回）：
    /// 只有矛盾时才能证明「谁优先」，两者一致时无法区分是走了哪一级。
    /// </remarks>
    [Fact]
    public async Task CompletedInstance_ShouldPreferDeclaredOutcome_OverResultVariable()
    {
        SkipUnlessReady();

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "lisi");
        var task = Assert.Single(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        await engine.CompleteTaskAsync(
            task.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["result"] = "1",
                [PlatformVariables.Outcome] = "REJECT",
            },
            ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "conclusion-declared", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var conclusion = body.GetProperty("conclusion");

        // 声明优先：REJECT / 已驳回，且来源标为 DECLARED
        Assert.Equal("REJECT", conclusion.GetProperty("semantic").GetString());
        Assert.Equal("已驳回", conclusion.GetProperty("label").GetString());
        Assert.Equal("DECLARED", conclusion.GetProperty("source").GetString());

        // 显式声明没有「导致它的选项」，optionId 必须为空——不能拿 result 冒充
        Assert.Equal(JsonValueKind.Null, conclusion.GetProperty("optionId").ValueKind);
    }

    /// <summary>
    /// 三级回落的最后一级：兜底 <c>CLOSED</c>（流程走完但没有任何审批决定）。
    /// </summary>
    /// <remarks>
    /// 这一级最容易被做成「假装通过」：实例结束了就给个 <c>APPROVE</c>。
    /// 断言 <c>CLOSED</c> + <c>FALLBACK</c> 是为了把「平台只知道它结束了」这件事如实告诉调用方。
    /// </remarks>
    [Fact]
    public async Task CompletedInstance_ShouldFallbackToClosed_WhenNoDecision()
    {
        SkipUnlessReady();

        using (var session = MySqlTestDatabase.CreateSessionFactory().Create())
        {
            MySqlTestDatabase.Reset(session);
        }

        var engine = _factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine);

        var instanceId = await StartAsync(engine, "lisi");
        var task = Assert.Single(await engine.FindTasksAsync(new Runtime.TaskQuery(ProcessInstanceId: instanceId), Ct));

        // 不带任何审批变量直接完成任务：模拟「人工跳转到结束 / 管理员干预办结」
        await engine.CompleteTaskAsync(task.Id, ct: Ct);

        using var client = _factory.CreateClient();
        var secret = await RegisterAsync(client, "conclusion-fallback", [PlatformScopes.InstanceRead]);

        using var response = await SendGetAsync(client, $"/workflow/v1/instances/{instanceId}", secret);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("COMPLETED", body.GetProperty("status").GetString());

        var conclusion = body.GetProperty("conclusion");

        Assert.Equal("CLOSED", conclusion.GetProperty("semantic").GetString());
        Assert.Equal("已办结", conclusion.GetProperty("label").GetString());
        Assert.Equal("FALLBACK", conclusion.GetProperty("source").GetString());
    }

    // ─────────────────────────── 测试基础设施 ───────────────────────────

    /// <summary>
    /// 平台端点依赖 <c>nb_tenant</c> / <c>nb_api_client</c>；老库缺表时给可行动的提示，而不是神秘 500。
    /// </summary>
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

    /// <summary>部署单环节审批流程（startEvent → 审批 userTask → endEvent）。</summary>
    private static async Task DeployAsync(IProcessEngine engine)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-test">
              <bpmn:process id="{{ApprovalKey}}" name="结论演示" isExecutable="true">
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

    private static async Task<string> StartAsync(IProcessEngine engine, string approver) =>
        await engine.StartProcessInstanceByKeyAsync(
            ApprovalKey,
            startUserId: "zhangsan",
            variables: new Dictionary<string, object?>(StringComparer.Ordinal) { ["approver"] = approver },
            ct: Ct);

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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }
}
