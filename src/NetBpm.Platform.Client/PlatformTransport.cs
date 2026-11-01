using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// HTTP 传输层：拼 URL / 挂身份头 / 反序列化 / 把非 2xx 转成 <see cref="NetBpmApiException"/>。
/// </summary>
/// <remarks>
/// <para>
/// 所有子客户端共用这一层，因此「错误怎么抛」「头怎么挂」只有一份实现。
/// 子客户端只负责「哪个路径、哪些查询参数、什么返回类型」，不碰 transport 细节。
/// </para>
/// <para>
/// 路径一律写成<b>相对</b>形式（<c>workflow/v1/processes</c>，不带前导斜杠），
/// 这样 <see cref="NetBpmClientOptions.BaseAddress"/> 可以带虚拟目录（<c>https://host/netbpm/</c>），
/// 前导斜杠会把虚拟目录吃掉。
/// </para>
/// </remarks>
internal sealed class PlatformTransport : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly NetBpmClientOptions _options;

    /// <summary>用配置自建 <see cref="HttpClient"/>（本层负责释放）。</summary>
    internal PlatformTransport(NetBpmClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _http = options.HttpMessageHandler is { } handler
            ? new HttpClient(handler, disposeHandler: true)
            : new HttpClient();

        _http.BaseAddress = NormalizeBaseAddress(options.BaseAddress);
        _http.Timeout = options.Timeout;

        OwnsClient = true;
    }

    /// <summary>接管调用方的 <see cref="HttpClient"/>（不负责释放，配合 <c>IHttpClientFactory</c> 用）。</summary>
    internal PlatformTransport(HttpClient httpClient, NetBpmClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        // 调用方已经把 HttpClient 配好了（BaseAddress / handler / 默认头），这里只补身份头
        _options = options ?? new NetBpmClientOptions();
        _http = httpClient;
        OwnsClient = false;
    }

    /// <summary>是否由本层负责释放 <see cref="HttpClient"/>。</summary>
    internal bool OwnsClient { get; }

    internal async Task<T> GetJsonAsync<T>(
        string path,
        IReadOnlyList<KeyValuePair<string, string?>>? query,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path, query));

        return await SendForJsonAsync<T>(request, path, ct).ConfigureAwait(false);
    }

    internal async Task<T> PostJsonAsync<T>(
        string path,
        object? body,
        string? idempotencyKey,
        CancellationToken ct)
    {
        using var request = CreateJsonRequest(HttpMethod.Post, path, body, idempotencyKey);

        return await SendForJsonAsync<T>(request, path, ct).ConfigureAwait(false);
    }

    internal async Task<T> PutJsonAsync<T>(string path, object? body, CancellationToken ct)
    {
        using var request = CreateJsonRequest(HttpMethod.Put, path, body, idempotencyKey: null);

        return await SendForJsonAsync<T>(request, path, ct).ConfigureAwait(false);
    }

    /// <summary>发一个不关心响应体的请求（DELETE / 204）。</summary>
    internal async Task SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        await ApplyIdentityAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildExceptionAsync(response, path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>读纯文本响应（流程图 SVG）。</summary>
    internal async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        await ApplyIdentityAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildExceptionAsync(response, path, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (OwnsClient)
        {
            _http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>把查询参数里的时间格式化成 ISO 8601（服务端按不变区域性解析）。</summary>
    internal static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>把枚举转成契约里的 <c>SCREAMING_SNAKE_CASE</c> 词汇（与 JSON 序列化一致）。</summary>
    internal static string? EnumText<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value is { } actual ? JsonNamingPolicy.SnakeCaseUpper.ConvertName(actual.ToString()) : null;

    // ─────────────────────────── 内部实现 ───────────────────────────

    private async Task<T> SendForJsonAsync<T>(HttpRequestMessage request, string path, CancellationToken ct)
    {
        await ApplyIdentityAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await BuildExceptionAsync(response, path, ct).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var result = await DeserializeAsync<T>(stream, response.StatusCode, path, ct).ConfigureAwait(false);

        // 2xx 但响应体为空（或 "null"）只可能是服务端实现 bug：按「缺响应体」报出来。
        // 静默返回 default 会让调用方在很远的地方才吃到 NullReferenceException，排查成本天差地别。
        return result ?? throw new NetBpmApiException(
            response.StatusCode,
            problem: null,
            responseBody: "（2xx 但响应体为空）",
            requestPath: path);
    }

    /// <summary>
    /// 反序列化，并把「响应体不是合法 JSON」也转成 <see cref="NetBpmApiException"/>。
    /// </summary>
    /// <remarks>
    /// 让 <see cref="JsonException"/> 直接冒出去会把 SDK 的实现细节泄漏给调用方：
    /// 它既没有错误码也没有 traceId，还没法区分「网关返回了 HTML」与「服务端 JSON 坏了」。
    /// 这里统一包一层，把原文带上——排查时要的正是那段原文。
    /// </remarks>
    private static async Task<T?> DeserializeAsync<T>(
        Stream stream,
        HttpStatusCode statusCode,
        string path,
        CancellationToken ct)
    {
        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new NetBpmApiException(
                statusCode,
                problem: null,
                responseBody: "（响应体不是合法 JSON：" + exception.Message + "）",
                requestPath: path);
        }
    }

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string path,
        object? body,
        string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private async ValueTask ApplyIdentityAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = _options.AccessTokenProvider is { } provider
            ? await provider(ct).ConfigureAwait(false)
            : _options.AccessToken;

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _options.ApiKey);
        }

        // 代客调用：审批结论会记在这个人身上，读接口的可见性也会收窄到他的视角
        if (!string.IsNullOrWhiteSpace(_options.SubjectId))
        {
            request.Headers.TryAddWithoutValidation("X-Subject-Id", _options.SubjectId);
        }

        if (!string.IsNullOrWhiteSpace(_options.TenantId))
        {
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", _options.TenantId);
        }
    }

    private static async Task<NetBpmApiException> BuildExceptionAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        PlatformProblem? problem = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                problem = JsonSerializer.Deserialize<PlatformProblem>(body, JsonOptions);
            }
            catch (JsonException)
            {
                // 网关 502 的 HTML、反向代理的纯文本都走这里：问题体解析不出就保留原文
            }
        }

        return new NetBpmApiException(
            response.StatusCode,
            problem,
            NetBpmApiException.Truncate(body),
            path);
    }

    private static Uri BuildUri(string path, IReadOnlyList<KeyValuePair<string, string?>>? query)
    {
        // 相对 URI 不能直接 new Uri(base + path)：带查询串的字符串会被判成「错误的 URI 格式」，
        // 因此查询串自己拼，且只对值做转义（键是代码里写死的常量）
        var builder = new StringBuilder(path);
        var separator = '?';

        foreach (var (key, value) in query ?? [])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            builder.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return new Uri(builder.ToString(), UriKind.Relative);
    }

    private static Uri NormalizeBaseAddress(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("BaseAddress 必须是绝对地址（含 scheme 与 host）。", nameof(baseAddress));
        }

        return baseAddress.AbsoluteUri.EndsWith('/')
            ? baseAddress
            : new Uri(baseAddress.AbsoluteUri + "/");
    }
}
