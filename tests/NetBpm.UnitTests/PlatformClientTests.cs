using System.Net;
using System.Text;
using System.Text.Json;
using NetBpm.Platform.Client;
using NetBpm.Platform.Contracts;

namespace NetBpm.UnitTests;

/// <summary>
/// 官方 SDK 的传输层：URL 拼装、身份头、错误映射、游标分页、幂等键。
/// </summary>
/// <remarks>
/// <para>
/// 用假 handler 而不是起宿主：这一层要验的是「发出去的请求长什么样」与「拿回来的错误怎么抛」，
/// 与服务端行为无关。端到端（SDK 打真实宿主）在 <c>NetBpm.IntegrationTests.PlatformClientEndToEndTests</c>。
/// </para>
/// <para>
/// 分两层测的理由：URL / 错误映射错了，端到端测试会报「400 或空结果」，得靠人肉看日志才能定位；
/// 而这几个用例直接盯着 request 对象，哪一段拼错了一眼就能看到。
/// </para>
/// </remarks>
public sealed class PlatformClientTests
{
    private const string ProblemJson =
        """
        {
          "type": "https://netbpm.dev/problems/parameter.type_invalid",
          "title": "参数不合法",
          "status": 422,
          "detail": "days 必须是 1..365 的整数。",
          "instance": "/workflow/v1/tasks/t1/submit",
          "code": "parameter.type_invalid",
          "traceId": "trace-9",
          "errors": [ { "field": "parameters.days", "code": "parameter.type_invalid", "message": "超出范围" } ]
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_ShouldBuildQueryWithContractVocabulary()
    {
        var handler = new StubHandler(_ => Json("""{"items":[],"nextCursor":null,"hasMore":false}"""));

        using var client = CreateClient(handler);

        await client.Instances.ListAsync(
            new InstanceQuery(
                ProcessKey: "leave_process",
                Status: InstanceStatus.Running,
                StartedFrom: new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(8)),
                Limit: 5),
            Ct);

        var uri = Assert.Single(handler.Requests).RequestUri!;

        Assert.Equal("/workflow/v1/instances", uri.AbsolutePath);

        // 枚举必须是契约里的 SCREAMING_SNAKE 词汇（服务端按它解析；大小写敏感的是 Minimal API 绑定）
        Assert.Contains("status=RUNNING", uri.Query, StringComparison.Ordinal);
        Assert.Contains("direction=DESCENDING", uri.Query, StringComparison.Ordinal);
        Assert.Contains("limit=5", uri.Query, StringComparison.Ordinal);
        Assert.Contains("processKey=leave_process", uri.Query, StringComparison.Ordinal);

        // 时间带偏移量：客户端在 +08:00 也要能表达「这一刻」，服务端按 ISO 8601 解析
        Assert.Contains("startedFrom=2026-11-01T08%3A00%3A00", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_ShouldCarryIdentityHeaders()
    {
        var handler = new StubHandler(_ => Json("""{"items":[],"nextCursor":null,"hasMore":false}"""));

        using var client = new NetBpmPlatformClient(new NetBpmClientOptions
        {
            BaseAddress = new Uri("http://localhost:5080/"),
            ApiKey = "key-abc",
            AccessToken = "jwt-xyz",
            SubjectId = "zhangsan",
            TenantId = "acme",
            HttpMessageHandler = handler,
        });

        await client.Tasks.ListAsync(ct: Ct);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("Bearer jwt-xyz", request.Headers.Authorization?.ToString());
        Assert.Equal("key-abc", Assert.Single(request.Headers.GetValues("X-Api-Key")));
        Assert.Equal("zhangsan", Assert.Single(request.Headers.GetValues("X-Subject-Id")));
        Assert.Equal("acme", Assert.Single(request.Headers.GetValues("X-Tenant-Id")));
    }

    [Fact]
    public async Task TokenProvider_ShouldBeCalledOnEveryRequest()
    {
        var handler = new StubHandler(_ => Json("""{"items":[],"nextCursor":null,"hasMore":false}"""));
        var issued = 0;

        using var client = new NetBpmPlatformClient(new NetBpmClientOptions
        {
            BaseAddress = new Uri("http://localhost:5080/"),
            AccessTokenProvider = _ => ValueTask.FromResult<string?>($"token-{++issued}"),
            HttpMessageHandler = handler,
        });

        await client.Instances.ListAsync(ct: Ct);
        await client.Instances.ListAsync(ct: Ct);

        // 每个请求都取一次：长驻进程里令牌会过期，缓存到客户端就等于永不刷新
        Assert.Equal(2, issued);
        Assert.Equal(
            ["Bearer token-1", "Bearer token-2"],
            handler.Requests.Select(request => request.Headers.Authorization?.ToString()).ToArray());
    }

    [Fact]
    public async Task ProblemResponse_ShouldBecomeNetBpmApiException()
    {
        var handler = new StubHandler(_ => Json(ProblemJson, HttpStatusCode.UnprocessableEntity));

        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<NetBpmApiException>(() =>
            client.Tasks.SubmitAsync(
                "t1",
                new SubmitTaskRequest("approve", Parameters: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["param.days"] = "999",
                }),
                ct: Ct));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
        Assert.Equal(ErrorCodes.ParameterTypeInvalid, exception.Code);
        Assert.Equal("trace-9", exception.TraceId);
        Assert.True(exception.IsInvalidRequest);
        Assert.False(exception.IsNotFound);

        // 字段级明细要带上：接入方靠它把错误标到具体输入框上
        var field = Assert.Single(exception.Errors);

        Assert.Equal("parameters.days", field.Field);
        Assert.Contains("days", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonProblemResponse_ShouldKeepRawBody()
    {
        // 网关 / 反代的 HTML 错误页：不是 RFC 9457，但原文必须留着，否则工单里什么都写不出来
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
        });

        using var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<NetBpmApiException>(() =>
            client.Processes.GetAsync("leave_process", Ct));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Null(exception.Code);
        Assert.True(exception.IsTransient);
        Assert.Contains("502 Bad Gateway", exception.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("RFC 9457", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptySuccessBody_ShouldNotSilentlyReturnDefault()
    {
        // 2xx + 空体是服务端实现 bug；返回 default 会让调用方在很远的地方 NRE
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        });

        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<NetBpmApiException>(() => client.Instances.GetAsync("i-1", Ct));
    }

    [Fact]
    public async Task Submit_ShouldSendIdempotencyKeyAndJsonBody()
    {
        var handler = new StubHandler(_ => Json("""{"instance":null,"nextTasks":[],"timelineDelta":[],"events":[]}"""));

        using var client = CreateClient(handler);

        await client.Instances.StartAsync(
            new StartInstanceRequest(
                "leave_process",
                BusinessKey: "BX-2026-0001",
                Parameters: new Dictionary<string, string?>(StringComparer.Ordinal) { ["param.days"] = "3" }),
            idempotencyKey: "BX-2026-0001:start",
            ct: Ct);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("BX-2026-0001:start", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        Assert.Equal("leave_process", body.GetProperty("processKey").GetString());
        Assert.Equal("3", body.GetProperty("parameters").GetProperty("param.days").GetString());

        // 发起人只来自令牌：请求体里【不能】出现 startedBy
        Assert.False(body.TryGetProperty("startedBy", out _));
    }

    [Fact]
    public async Task Enumerate_ShouldFollowCursorUntilHasMoreFalse()
    {
        var handler = new StubHandler(request =>
        {
            var cursor = request.RequestUri!.Query.Contains("cursor=page2", StringComparison.Ordinal)
                ? "page2"
                : null;

            return cursor is null
                ? Json("""{"items":[{"instanceId":"i-1","processKey":"p","status":"RUNNING"}],"nextCursor":"page2","hasMore":true}""")
                : Json("""{"items":[{"instanceId":"i-2","processKey":"p","status":"COMPLETED"}],"nextCursor":null,"hasMore":false}""");
        });

        using var client = CreateClient(handler);

        var ids = new List<string>();

        await foreach (var item in client.Instances.EnumerateAsync(ct: Ct))
        {
            ids.Add(item.InstanceId);
        }

        Assert.Equal(["i-1", "i-2"], ids);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("cursor=page2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDelete_ShouldReturnFalseOn404()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var client = CreateClient(handler);

        // 「本来就删掉了」与「删成功」对调用方是同一件事，不值得让调用方自己判 404
        Assert.False(await client.AdminProcesses.DeleteAsync("leave_process", Ct));
    }

    [Fact]
    public async Task PathSegments_ShouldBeEscaped()
    {
        var handler = new StubHandler(_ => Json("""{"instanceId":"x","processKey":"p","processDefinitionId":"d","definitionVersion":1,"businessKey":null,"title":null,"status":"RUNNING","startedAt":null,"startedBy":null,"endedAt":null,"currentActivityIds":[],"revision":1,"tasks":[],"parameters":{"instanceId":"x","parameters":[]},"nodeOutcomes":[],"conclusion":null}"""));

        using var client = CreateClient(handler);

        await client.Instances.GetAsync("a b/c", Ct);

        // 实例 id 里带空格 / 斜杠：不转义会被服务端当成两段路径
        Assert.Equal("/workflow/v1/instances/a%20b%2Fc", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
    }

    private static NetBpmPlatformClient CreateClient(HttpMessageHandler handler) =>
        new(new NetBpmClientOptions
        {
            BaseAddress = new Uri("http://localhost:5080/"),
            ApiKey = "test-key",
            HttpMessageHandler = handler,
        });

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>假传输层：记录每个请求与最后一次请求体，响应由委托决定。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        internal List<HttpRequestMessage> Requests { get; } = [];

        internal string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _responder(request);
        }
    }
}
