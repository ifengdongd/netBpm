using System.Net.Http.Headers;

namespace NetBpm.Runtime.Events;

/// <summary>一次 HTTP 投递的结果。</summary>
/// <param name="Success">是否成功（2xx）。</param>
/// <param name="StatusCode">HTTP 状态码；没拿到响应时为 <see langword="null"/>。</param>
/// <param name="Error">失败原因（超时 / 连接失败 / 非 2xx）。</param>
public sealed record EventDeliveryOutcome(bool Success, int? StatusCode, string? Error);

/// <summary>
/// 投递传输层。抽出来是为了两件事：单测能不启 HTTP 就跑；
/// 宿主想换成「先写消息队列再由 sidecar 发」时不用改 handler。
/// </summary>
public interface IEventDeliveryTransport
{
    /// <summary>
    /// 把字节原样 POST 到目标地址。
    /// </summary>
    /// <remarks>
    /// 🔑 <paramref name="body"/> 必须<b>原样发出</b>：签名是按这些字节算的，
    /// 重新序列化（缩进、字段顺序、转义不同）会让订阅方验签失败。
    /// </remarks>
    ValueTask<EventDeliveryOutcome> PostAsync(
        string endpointUrl,
        byte[] body,
        string contentType,
        IDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken ct = default);
}

/// <summary>
/// HTTP 投递（默认传输实现）。
/// <para>
/// 只认 <b>2xx 为成功</b>：3xx 不跟随（跳转会把带签名的 POST 变成 GET，签名就废了），
/// 4xx/5xx 都算失败交给退避重试 —— 订阅方重启期间的 502 不该直接进死信。
/// </para>
/// </summary>
public sealed class HttpEventDeliveryTransport : IEventDeliveryTransport
{
    private readonly HttpClient _client;

    /// <summary>构造。</summary>
    /// <param name="client">复用的 HttpClient（长连接池；别每次投递新建，会耗尽端口）。</param>
    public HttpEventDeliveryTransport(HttpClient? client = null)
    {
        _client = client ?? new HttpClient
        {
            // 超时由每次调用用 CancellationToken 精确控制；这里设一个兜底上限
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <inheritdoc />
    public async ValueTask<EventDeliveryOutcome> PostAsync(
        string endpointUrl,
        byte[] body,
        string contentType,
        IDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        ArgumentNullException.ThrowIfNull(body);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        foreach (var header in headers)
        {
            // 逐条 TryAdd：Content-Type 之类由上面设定，订阅的自定义头覆盖不掉也无害
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        try
        {
            using var response = await _client
                .PostAsync(endpointUrl, content, timeoutSource.Token)
                .ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new EventDeliveryOutcome(true, statusCode, null)
                : new EventDeliveryOutcome(false, statusCode, $"订阅方返回 HTTP {statusCode}。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EventDeliveryOutcome(false, null, $"投递超时（{timeout.TotalSeconds:F0} 秒）。");
        }
        catch (HttpRequestException exception)
        {
            return new EventDeliveryOutcome(false, null, $"投递请求失败：{exception.Message}");
        }
    }
}
