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
/// 双形态等价性：<b>同一份 BPMN、同一套操作序列</b>，分别跑在演示宿主（<c>NetBpm.Rest</c>，嵌入式形态）
/// 与独立部署宿主（<c>NetBpm.Platform.Service</c>）上，时间线与实例视图的<b>语义</b>必须一致。
/// </summary>
/// <remarks>
/// <para>
/// 为什么值得单写一组：两个宿主共用 <c>AddNetBpmHost</c> 组合根，但<b>入口不同</b>——
/// 独立宿主多一段启动自检与数据库启动校验，默认值也相反（凭据 / 数据库）。
/// 「组合根相同」不等于「行为相同」：少挂一个中间件、少写一条时间线、把默认租户写错，
/// 都能让同一个流程在两种形态下给出不同的历史。这类差异只在切换部署形态时暴露，
/// 而切换正是这套平台化改造的目的，所以它必须有门禁。
/// </para>
/// <para>
/// <b>只比语义，不比字面</b>：实例 id、时间戳、<c>actorCallerId</c>（两个宿主各注册各的客户端）、
/// <c>processKey</c>（为了不互相干扰用了不同 key）都不进入比较。
/// 比的是「谁在什么时候做了什么、算第几轮、得出什么结论」。
/// </para>
/// <para>
/// 两个宿主都关掉 <c>EnableHistory</c>：这里要隔离的变量是<b>宿主装配</b>，不是历史存储。
/// 历史相关口径由 <c>NodeOutcomeAggregationTests</c>（真库 + 引擎历史）单独覆盖。
/// </para>
/// </remarks>
public sealed class DualModeTests
{
    internal const string DefaultTenant = "default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TimelineAndView_ShouldBeEquivalent_AcrossHosts()
    {
        // 两个宿主都跑内存执行树：唯一的变量是「谁来装配与托管这个引擎」
        var embedded = await RunAsync(BuildEmbeddedHost(), "dual_embedded");

        var standalone = await RunAsync(new PlatformServiceEngineFactory(), "dual_standalone");

        // 先钉住「场景本身是完整的」——否则两边都是空的也会「相等」
        Assert.Equal(
            ["INSTANCE_STARTED", "TASK_COMPLETED"],
            embedded.Timeline.Select(entry => entry.Kind).ToArray());
        Assert.Equal("RUNNING", embedded.Status);
        Assert.Equal(["task_approve"], embedded.CurrentActivityIds);

        // 聚合也要真的算出来（否则 Outcomes 双侧为空同样「相等」）
        Assert.Contains(
            embedded.Outcomes,
            outcome => outcome.StartsWith("task_apply|", StringComparison.Ordinal));

        Assert.Equal(embedded.Timeline, standalone.Timeline);
        Assert.Equal(embedded.Status, standalone.Status);
        Assert.Equal(embedded.CurrentActivityIds, standalone.CurrentActivityIds);
        Assert.Equal(embedded.Outcomes, standalone.Outcomes);
        Assert.Equal(embedded.Conclusion, standalone.Conclusion);
    }

    [Fact]
    public async Task Timeline_ShouldMatch_WhenStandaloneHostRunsOnDatabase()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        // 真库形态：独立宿主连 MySQL（生产形态），演示宿主仍是嵌入式内存 —— 这正是切换时的两个端点
        var embedded = await RunAsync(
            BuildEmbeddedHost(),
            "dualmode_mem_" + Guid.NewGuid().ToString("N")[..8]);

        var standalone = await RunAsync(
            new PlatformServiceEngineFactory(useDatabase: true, MySqlTestDatabase.ConnectionString),
            "dualmode_db_" + Guid.NewGuid().ToString("N")[..8]);

        Assert.Equal(embedded.Timeline, standalone.Timeline);
        Assert.Equal(embedded.Status, standalone.Status);
        Assert.Equal(embedded.CurrentActivityIds, standalone.CurrentActivityIds);
    }

    // ─────────────────────────── 场景 ───────────────────────────

    /// <summary>跑一遍「发起 → 提交第一个环节」，把两种形态下可比的部分抽成快照。</summary>
    private static async Task<Snapshot> RunAsync<T>(
        WebApplicationFactory<T> host,
        string processKey)
        where T : class
    {
        using var factory = host;

        var engine = factory.Services.GetRequiredService<IProcessEngine>();
        await DeployAsync(engine, processKey);

        using var client = factory.CreateClient();
        var secret = await RegisterAsync(client, "dual-mode-" + processKey);

        var instanceId = await StartAsync(client, secret, processKey);
        var taskId = await FindFirstTaskAsync(engine, instanceId);
        await SubmitAsync(client, taskId, secret);

        var timeline = await GetTimelineAsync(client, instanceId, secret);
        var view = await GetInstanceAsync(client, instanceId, secret);

        return new Snapshot(
            timeline,
            view.GetProperty("status").GetString()!,
            view.GetProperty("currentActivityIds").EnumerateArray()
                .Select(item => item.GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            view.GetProperty("nodeOutcomes").EnumerateArray()
                .Select(NormalizeOutcome)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            NormalizeConclusion(view.GetProperty("conclusion")));
    }

    /// <summary>
    /// 条目的「语义指纹」：类型 / 环节 / 轮次 / 选项 / 语义 / 意见 / 办理人 / 目标节点。
    /// <c>actorCallerId</c> 与序号、时间戳刻意不进来 —— 它们随宿主与时钟变化，不是语义。
    /// </summary>
    private static string NormalizeEntry(JsonElement entry) => string.Join(
        '|',
        entry.GetProperty("kind").GetString(),
        entry.GetProperty("activityId").GetString() ?? "-",
        entry.GetProperty("round").ValueKind == JsonValueKind.Null
            ? "-"
            : entry.GetProperty("round").GetInt32().ToString(),
        entry.GetProperty("optionId").GetString() ?? "-",
        entry.GetProperty("semantic").GetString() ?? "-",
        entry.GetProperty("comment").GetString() ?? "-",
        entry.GetProperty("actorId").GetString() ?? "-",
        entry.GetProperty("targetNodeId").GetString() ?? "-");

    private static string NormalizeOutcome(JsonElement outcome) => string.Join(
        '|',
        outcome.GetProperty("activityId").GetString(),
        outcome.GetProperty("aggregation").GetString(),
        outcome.GetProperty("satisfied").GetBoolean(),
        outcome.GetProperty("total").GetInt32(),
        outcome.GetProperty("positive").GetInt32(),
        outcome.GetProperty("negative").GetInt32(),
        outcome.GetProperty("pending").GetInt32(),
        outcome.GetProperty("round").ValueKind == JsonValueKind.Null
            ? "-"
            : outcome.GetProperty("round").GetInt32().ToString());

    private static string NormalizeConclusion(JsonElement conclusion) =>
        conclusion.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? "-"
            : string.Join(
                '|',
                conclusion.GetProperty("semantic").GetString(),
                conclusion.GetProperty("source").GetString(),
                conclusion.GetProperty("optionId").GetString() ?? "-");

    // ─────────────────────────── 基础设施 ───────────────────────────

    /// <summary>
    /// 嵌入式形态：演示宿主（<c>NetBpm.Rest</c>）+ 内存执行树。
    /// 配置口径与 <c>ApprovalTimelineTests</c> 一致 —— 两种形态要能对比，除宿主外的一切都必须相同。
    /// </summary>
    private static WebApplicationFactory<Program> BuildEmbeddedHost() =>
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

    private static async Task DeployAsync(IProcessEngine engine, string processKey)
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                              xmlns:activiti="http://activiti.org/bpmn"
                              targetNamespace="netbpm-dualmode">
              <bpmn:process id="{{processKey}}" name="双形态演示" isExecutable="true">
                <bpmn:startEvent id="start" name="Start" />
                <bpmn:sequenceFlow id="flow1" sourceRef="start" targetRef="task_apply" />
                <bpmn:userTask id="task_apply" name="申请" activiti:assignee="zhangsan">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                    <activiti:formProperty id="comment" name="审批说明" type="string" variable="comment" />
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow1</bpmn:incoming>
                  <bpmn:outgoing>flow2</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow2" sourceRef="task_apply" targetRef="task_approve" />
                <bpmn:userTask id="task_approve" name="审批" activiti:assignee="manager">
                  <bpmn:extensionElements>
                    <activiti:formProperty id="approvalResult" name="审批结果" type="enum" variable="result">
                      <activiti:value id="1" name="通过" />
                      <activiti:value id="2" name="拒绝" />
                    </activiti:formProperty>
                  </bpmn:extensionElements>
                  <bpmn:incoming>flow2</bpmn:incoming>
                  <bpmn:outgoing>flow3</bpmn:outgoing>
                </bpmn:userTask>
                <bpmn:sequenceFlow id="flow3" sourceRef="task_approve" targetRef="end" />
                <bpmn:endEvent id="end" name="End" />
              </bpmn:process>
            </bpmn:definitions>
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await engine.DeployAsync(stream, processKey + ".bpmn20.xml", ct: Ct);
    }

    private static async Task<string> RegisterAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/admin/clients?confirmPrivilegedScopes=true", UriKind.Relative),
            new
            {
                name,
                tenantId = DefaultTenant,
                scopes = new[]
                {
                    PlatformScopes.InstanceStart,
                    PlatformScopes.InstanceRead,
                    PlatformScopes.TaskRead,
                    PlatformScopes.TaskSubmit,
                    PlatformScopes.TaskSubmitOnBehalf,
                    PlatformScopes.HistoryRead,
                },
            },
            Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"注册客户端失败：{response.StatusCode} {body}");

        using var json = JsonDocument.Parse(body);

        return json.RootElement.GetProperty("clientSecret").GetString()!;
    }

    private static async Task<string> StartAsync(HttpClient client, string secret, string processKey)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/workflow/v1/instances",
            secret,
            subject: null,
            new { processKey });

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return body.GetProperty("instanceId").GetString()!;
    }

    private static async Task<string> FindFirstTaskAsync(IProcessEngine engine, string instanceId)
    {
        var tasks = await engine.FindTasksAsync(
            new NetBpm.Runtime.TaskQuery(ProcessInstanceId: instanceId),
            Ct);
        var task = Assert.Single(tasks);

        Assert.Equal("task_apply", task.TaskDefinitionKey);

        return task.Id;
    }

    private static async Task SubmitAsync(HttpClient client, string taskId, string secret)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/workflow/v1/tasks/{taskId}/submit",
            secret,
            subject: "zhangsan",
            new { optionId = "1", comment = "同意" });

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"提交失败：{response.StatusCode} {body}");
    }

    private static async Task<IReadOnlyList<TimelineFingerprint>> GetTimelineAsync(
        HttpClient client,
        string instanceId,
        string secret)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/workflow/v1/instances/{instanceId}/history",
            secret,
            subject: null,
            body: null);

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return body.GetProperty("entries").EnumerateArray()
            .Select(entry => new TimelineFingerprint(
                entry.GetProperty("kind").GetString()!,
                NormalizeEntry(entry)))
            .ToArray();
    }

    private static async Task<JsonElement> GetInstanceAsync(
        HttpClient client,
        string instanceId,
        string secret)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/workflow/v1/instances/{instanceId}",
            secret,
            subject: null,
            body: null);

        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return body.Clone();
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string secret,
        string? subject,
        object? body)
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.False(string.IsNullOrWhiteSpace(body), "响应体为空。");

        return JsonDocument.Parse(body).RootElement;
    }

    private sealed record TimelineFingerprint(string Kind, string Fingerprint)
    {
        public override string ToString() => Fingerprint;
    }

    private sealed record Snapshot(
        IReadOnlyList<TimelineFingerprint> Timeline,
        string Status,
        IReadOnlyList<string> CurrentActivityIds,
        IReadOnlyList<string> Outcomes,
        string Conclusion);
}
