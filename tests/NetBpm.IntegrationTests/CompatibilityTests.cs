using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 兼容性：<b>上一版契约（= 上一版 SDK 的模型）打当前服务</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能只靠快照一致性</b>：<c>ContractSnapshotTests</c> 回答「声明的契约变了没有」；
/// 它回答不了「声明没变、但服务端真的还按声明返回吗」。一个字段忘了赋值、枚举多了一个没登记的取值、
/// 某个 200 变成 500，快照全都是绿的，而调用方已经挂了。
/// </para>
/// <para>
/// <b>基线从哪来</b>：<c>contracts/baseline/workflow-v1.json</c> 是<b>上一个已发布版本</b>的
/// <c>workflow-v1.json</c> 的冻结副本。发版时把当时的快照复制过去即可（见 <c>contracts/README.md</c>）。
/// 这份文件是「已经承诺给接入方的东西」，因此<b>只允许在发布新版本时追加</b>，不允许为了让测试变绿而修改。
/// </para>
/// <para>
/// <b>两条断言</b>：① 基线声明的每条路径 / 方法仍在（旧客户端的 URL 还能用）；
/// ② 真实响应的结构仍符合基线的 schema（旧客户端的反序列化不会炸）。
/// 两条都过，才谈得上「升级服务端不影响已接入系统」。
/// </para>
/// </remarks>
public sealed class CompatibilityTests
{
    /// <summary>基线文件（上一版契约）。</summary>
    private static readonly string BaselineFile = Path.Combine(
        RepositoryLayout.Root,
        "contracts",
        "baseline",
        "workflow-v1.json");

    private const string ProcessKey = ReadEndpointHarness.ProcessKey;

    private static string[] Scopes =>
    [
        PlatformScopes.ProcessRead,
        PlatformScopes.InstanceRead,
        PlatformScopes.InstanceStart,
        PlatformScopes.InstanceTerminate,
        PlatformScopes.TaskRead,
        PlatformScopes.TaskSubmit,
        PlatformScopes.TaskSubmitOnBehalf,
        PlatformScopes.HistoryRead,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>基线里声明的每一条路径 / 方法，当前服务必须仍然提供。</summary>
    [Fact]
    public async Task Baseline_ShouldStillBeServed()
    {
        var baseline = LoadBaseline();
        var live = await FetchLiveDocumentAsync();

        var missing = new List<string>();

        foreach (var (path, operations) in Assert.IsType<JsonObject>(baseline["paths"]))
        {
            var livePath = Assert.IsType<JsonObject>(live["paths"]).TryGetPropertyValue(path, out var node)
                ? node as JsonObject
                : null;

            foreach (var method in (operations as JsonObject ?? []).Select(pair => pair.Key))
            {
                if (livePath is null || !livePath.ContainsKey(method))
                {
                    missing.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "这轮把已承诺的接口弄丢了（对已接入系统来说是破坏性变更）：\n  "
            + string.Join("\n  ", missing)
            + "\n如果是有意的，必须先走 v2 路径 + 公告，然后再考虑更新基线。");
    }

    /// <summary>主流程各响应仍符合基线 schema。</summary>
    [Fact]
    public async Task Responses_ShouldStillMatchBaselineSchema()
    {
        var baseline = LoadBaseline();

        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        using var client = host.CreateClient();
        var secret = await ReadEndpointHarness.RegisterAsync(client, "compat-suite", Scopes);

        var probes = new List<Probe>();

        // ── 目录 ──
        probes.Add(await GetAsync(client, secret, "workflow/v1/processes", "/workflow/v1/processes"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/processes/{ProcessKey}", "/workflow/v1/processes/{processKey}"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/processes/{ProcessKey}/definition", "/workflow/v1/processes/{processKey}/definition"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/processes/{ProcessKey}/versions", "/workflow/v1/processes/{processKey}/versions"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/processes/{ProcessKey}/parameters/schema", "/workflow/v1/processes/{processKey}/parameters/schema"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/processes/{ProcessKey}/form/schema", "/workflow/v1/processes/{processKey}/form/schema"));

        // ── 发起 ──
        var start = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "workflow/v1/instances",
            secret,
            JsonContent.Create(new
            {
                processKey = ProcessKey,
                businessKey = "BX-COMPAT-001",
                parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["param.days"] = "2" },
            }),
            // 发起人 = lisi：后面的终止要求「发起人或管理视角」，没人发起就撤不了单
            subject: "lisi"), Ct);

        Assert.Equal(HttpStatusCode.Created, start.StatusCode);

        probes.Add(Probe.Of("/workflow/v1/instances", "post", 201, await start.Content.ReadAsStringAsync(Ct)));

        var startedBody = JsonNode.Parse(await start.Content.ReadAsStringAsync(Ct))!;
        var instanceId = startedBody["instanceId"]!.GetValue<string>();

        // ── 实例读形态 ──
        probes.Add(await GetAsync(client, secret, $"workflow/v1/instances/{instanceId}", "/workflow/v1/instances/{instanceId}"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/instances/{instanceId}/history", "/workflow/v1/instances/{instanceId}/history"));
        probes.Add(await GetAsync(client, secret, $"workflow/v1/instances/{instanceId}/parameters", "/workflow/v1/instances/{instanceId}/parameters"));
        probes.Add(await GetAsync(client, secret, "workflow/v1/instances", "/workflow/v1/instances"));

        // ── 待办（MINE 要真人主体：不带 X-Subject-Id 的服务令牌没有「我的待办」）──
        var mine = await GetAsync(client, secret, "workflow/v1/tasks?scope=MINE", "/workflow/v1/tasks", subject: "lisi");

        probes.Add(mine);

        var taskId = mine.Body!["items"]![0]!["taskId"]!.GetValue<string>();

        probes.Add(await GetAsync(
            client, secret, $"workflow/v1/tasks/{taskId}", "/workflow/v1/tasks/{taskId}", subject: "lisi"));

        // ── 提交（推进到审批环节）──
        var submit = await client.SendAsync(Authorized(
            HttpMethod.Post,
            $"workflow/v1/tasks/{taskId}/submit",
            secret,
            new StringContent(
                """{"optionId":"1","comment":"兼容性用例"}""",
                Encoding.UTF8,
                "application/json"),
            subject: "lisi"), Ct);

        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        probes.Add(Probe.Of("/workflow/v1/tasks/{taskId}/submit", "post", 200, await submit.Content.ReadAsStringAsync(Ct)));

        // 提交之后才有表单快照与附件回放
        probes.Add(await GetAsync(
            client, secret, $"workflow/v1/tasks/{taskId}/form", "/workflow/v1/tasks/{taskId}/form", subject: "lisi"));

        probes.Add(await GetAsync(
            client, secret, $"workflow/v1/tasks/{taskId}/attachments", "/workflow/v1/tasks/{taskId}/attachments", subject: "lisi"));

        // ── 参数修订历史（v1.23 新增；基线里已有就必须继续符合）──
        probes.Add(await GetAsync(client, secret, $"workflow/v1/instances/{instanceId}/parameters/history", "/workflow/v1/instances/{instanceId}/parameters/history"));

        // ── 终止 ──
        var terminate = await client.SendAsync(Authorized(
            HttpMethod.Post,
            $"workflow/v1/instances/{instanceId}/terminate",
            secret,
            new StringContent(
                """{"reason":"兼容性用例"}""",
                Encoding.UTF8,
                "application/json"),
            subject: "lisi"), Ct);

        Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);

        probes.Add(Probe.Of("/workflow/v1/instances/{instanceId}/terminate", "post", 200, await terminate.Content.ReadAsStringAsync(Ct)));

        // ── 逐条比对 ──
        var failures = new List<string>();

        foreach (var probe in probes)
        {
            var schema = ResolveResponseSchema(baseline, probe);

            if (schema is null)
            {
                // 基线没声明这个状态码的 JSON schema（例如只有 204）：跳过而不是失败
                continue;
            }

            var violations = JsonSchemaValidator.Validate(baseline, schema, probe.Body, "$");

            if (violations.Count > 0)
            {
                failures.Add(
                    $"{probe.Method.ToUpperInvariant()} {probe.Template} → {probe.Status}\n    "
                    + string.Join("\n    ", violations));
            }
        }

        Assert.True(
            failures.Count == 0,
            "以下响应不再符合上一版契约（已接入系统会解析失败）：\n  "
            + string.Join("\n  ", failures));
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    /// <summary>一次探测：基线里的模板 + 实际请求得到的响应。</summary>
    private sealed record Probe(string Template, string Method, int Status, JsonNode? Body)
    {
        internal static Probe Of(string template, string method, int status, string body) =>
            new(template, method, status, string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body));
    }

    private static JsonObject LoadBaseline()
    {
        Assert.True(
            File.Exists(BaselineFile),
            $"缺少兼容性基线：{BaselineFile}。发版时把当时的 contracts/openapi/workflow-v1.json 复制过去。");

        return Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(BaselineFile)));
    }

    private static async Task<JsonObject> FetchLiveDocumentAsync()
    {
        using var host = ReadEndpointHarness.BuildHost();
        using var client = host.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return Assert.IsType<JsonObject>(JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct)));
    }

    private static async Task<Probe> GetAsync(
        HttpClient client,
        string secret,
        string url,
        string template,
        string? subject = null)
    {
        using var response = await client.SendAsync(Authorized(HttpMethod.Get, url, secret, subject: subject), Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {url} 返回 {(int)response.StatusCode}：{body}");

        return Probe.Of(template, "get", 200, body);
    }

    private static HttpRequestMessage Authorized(
        HttpMethod method,
        string url,
        string secret,
        HttpContent? content = null,
        string? subject = null)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = content };

        request.Headers.Add("X-Api-Key", secret);

        if (subject is not null)
        {
            request.Headers.Add("X-Subject-Id", subject);
        }

        return request;
    }

    private static JsonNode? ResolveResponseSchema(JsonObject baseline, Probe probe)
    {
        var paths = baseline["paths"] as JsonObject;
        var operation = paths?[probe.Template]?[probe.Method] as JsonObject;

        return operation?["responses"]?[probe.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)]
            ?["content"]?["application/json"]?["schema"];
    }
}
