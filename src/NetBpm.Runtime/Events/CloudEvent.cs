using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetBpm.Engine.Events;

namespace NetBpm.Runtime.Events;

/// <summary>
/// CloudEvents 1.0 常量与 HTTP 绑定用到的头名。
/// <para>
/// 为什么用 CloudEvents 而不是自己拍一套 JSON：订阅方常常不止一个系统，
/// 有现成的标准（且各家网关 / Knative / EventBridge 都认）就不要发明方言。
/// 引擎专有信息一律走<b>扩展属性</b>（<c>netbpm*</c> 小写前缀），不污染标准字段。
/// </para>
/// </summary>
public static class CloudEvents
{
    /// <summary>规格版本（CloudEvents 1.0）。</summary>
    public const string SpecVersion = "1.0";

    /// <summary>结构化模式的 Content-Type。</summary>
    public const string JsonContentType = "application/cloudevents+json";

    /// <summary>签名头：<c>sha256=&lt;hex&gt;</c>。</summary>
    public const string SignatureHeader = "X-NetBpm-Signature";

    /// <summary>签名时间戳头（Unix 秒）。订阅方用它挡重放。</summary>
    public const string TimestampHeader = "X-NetBpm-Timestamp";

    /// <summary>投递 id 头（与 CloudEvents 的 <c>id</c> 同值，方便网关层做幂等）。</summary>
    public const string EventIdHeader = "X-NetBpm-Event-Id";

    /// <summary>引擎事件类型 → CloudEvents <c>type</c>。认不出的按约定降级，不抛错。</summary>
    public static string TypeOf(string engineEventType)
    {
        if (string.IsNullOrWhiteSpace(engineEventType))
        {
            return "netbpm.unknown";
        }

        // PROCESS_STARTED → netbpm.process.started；未知类型保持原样小写化，
        // 认不出就直译，订阅方按字符串匹配即可，不需要引擎再维护一张表
        var parts = engineEventType.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length >= 2
            ? $"netbpm.{parts[0].ToLowerInvariant()}.{string.Join('.', parts.Skip(1)).ToLowerInvariant()}"
            : $"netbpm.{engineEventType.ToLowerInvariant()}";
    }
}

/// <summary>
/// 一条 CloudEvents 事件（结构化模式的 JSON 形态）。
/// <para>
/// <b>幂等键就是 <see cref="Id"/></b>：它随事件一起落库（<c>ACT_EVT_OUTBOX.ID_</c>），
/// 重投时不变。订阅方必须按它去重 —— 投递是「至少一次」，网络超时与进程崩溃都会导致重发。
/// </para>
/// </summary>
public sealed class CloudEvent
{
    /// <summary>规格版本。</summary>
    [JsonPropertyName("specversion")]
    public string SpecVersion { get; set; } = CloudEvents.SpecVersion;

    /// <summary>事件 id（幂等键，全局唯一且重投不变）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>事件源（<c>/netbpm/{tenant}</c>）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "/netbpm/default";

    /// <summary>事件类型（见 <see cref="CloudEvents.TypeOf"/>）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>发生时间（UTC）。</summary>
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    /// <summary>数据内容类型。</summary>
    [JsonPropertyName("datacontenttype")]
    public string DataContentType { get; set; } = "application/json";

    /// <summary>主体：任务 id / 活动 id / 实例 id（<c>ACT_EVT_OUTBOX.ENTITY_ID_</c>）。</summary>
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    /// <summary>引擎专有扩展属性（全部小写，直接平铺到 JSON 顶层）。</summary>
    [JsonExtensionData]
    public IDictionary<string, object?> Extensions { get; set; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>业务数据（<c>data</c>）。</summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>设置扩展属性（值为 <see langword="null"/> 时移除，避免塞一堆 null 进信封）。</summary>
    public CloudEvent With(string name, object? value)
    {
        if (value is null)
        {
            Extensions.Remove(name);
        }
        else
        {
            Extensions[name] = value;
        }

        return this;
    }
}

/// <summary>
/// CloudEvents 的构造与序列化。
/// </summary>
public static class CloudEventSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把 <c>ACT_EVT_OUTBOX</c> 的一行翻译成 CloudEvents。
    /// </summary>
    /// <param name="outboxEvent">outbox 事件。</param>
    /// <param name="sourcePrefix">source 前缀（默认 <c>/netbpm</c>）。</param>
    public static CloudEvent Create(OutboxEvent outboxEvent, string sourcePrefix = "/netbpm")
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);

        var tenant = string.IsNullOrWhiteSpace(outboxEvent.TenantId) ? "default" : outboxEvent.TenantId;

        var cloudEvent = new CloudEvent
        {
            Id = outboxEvent.Id,
            Source = $"{sourcePrefix}/{tenant}",
            Type = CloudEvents.TypeOf(outboxEvent.EventType),
            Time = outboxEvent.CreateTime,
            Subject = outboxEvent.EntityId,
            Data = ParseData(outboxEvent.Payload),
        };

        // 扩展属性：让订阅方不必回查就能路由（租户 / 实例 / 定义 / 节点）
        cloudEvent
            .With("netbmpeventtype", outboxEvent.EventType)
            .With("netbmptenantid", tenant)
            .With("netbpmprocessinstanceid", outboxEvent.ProcessInstanceId)
            .With("netbpmprocessdefinitionid", outboxEvent.ProcessDefinitionId)
            .With("netbpmexecutionid", outboxEvent.ExecutionId)
            .With("netbpmactivityid", outboxEvent.ActivityId);

        return cloudEvent;
    }

    /// <summary>序列化为 UTF-8 JSON（没有 BOM —— 签名要按字节算，BOM 会让订阅方校验失败）。</summary>
    public static byte[] Serialize(CloudEvent cloudEvent)
    {
        ArgumentNullException.ThrowIfNull(cloudEvent);

        return JsonSerializer.SerializeToUtf8Bytes(cloudEvent, Options);
    }

    private static object? ParseData(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 载荷不是合法 JSON（历史数据 / 手工写入）：原样当字符串发，别因为解析失败把整条事件卡死
            return payload;
        }
    }
}

/// <summary>
/// 事件签名：HMAC-SHA256 over <c>{timestamp}.{body}</c>。
/// <para>
/// 签名里<b>带上时间戳</b>是必须的：只签 body 的话，抓到一个合法请求就能无限重放。
/// 订阅方应当校验时间戳在自己的容差窗口内（默认 5 分钟）再验签。
/// </para>
/// <para>
/// 校验用<b>常量时间比较</b>（<see cref="CryptographicOperations.FixedTimeEquals"/>）：
/// 逐字符比对的写法会通过响应时间泄漏前缀匹配长度。
/// </para>
/// </summary>
public static class EventSignature
{
    /// <summary>默认时间容差（5 分钟）。</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>签名前缀。</summary>
    public const string Prefix = "sha256=";

    /// <summary>计算签名头的值（含 <c>sha256=</c> 前缀）。</summary>
    /// <param name="secret">订阅的共享密钥。</param>
    /// <param name="timestampUnixSeconds">Unix 秒。</param>
    /// <param name="body">消息体字节（<b>就是实际发送的那些字节</b>，不能重新序列化）。</param>
    public static string Sign(string secret, long timestampUnixSeconds, byte[] body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);

        var message = Encoding.UTF8.GetBytes($"{timestampUnixSeconds}.");
        var buffer = new byte[message.Length + body.Length];

        message.CopyTo(buffer, 0);
        body.CopyTo(buffer, message.Length);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), buffer);

        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 校验签名（订阅方 / 测试用）。
    /// </summary>
    /// <param name="secret">共享密钥。</param>
    /// <param name="timestampUnixSeconds">签名头里的 Unix 秒。</param>
    /// <param name="body">收到的原始字节。</param>
    /// <param name="signature"><see cref="CloudEvents.SignatureHeader"/> 的值。</param>
    /// <param name="tolerance">时间容差；<see langword="null"/> 用 <see cref="DefaultTolerance"/>。</param>
    /// <param name="nowUtc">当前 UTC 时间（测试可注入）。</param>
    public static bool Verify(
        string secret,
        long timestampUnixSeconds,
        byte[] body,
        string? signature,
        TimeSpan? tolerance = null,
        DateTime? nowUtc = null)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature) || body is null)
        {
            return false;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestampUnixSeconds).UtcDateTime;
        var window = tolerance ?? DefaultTolerance;

        if (Math.Abs((now - signedAt).TotalSeconds) > window.TotalSeconds)
        {
            return false;
        }

        if (!signature.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Sign(secret, timestampUnixSeconds, body);

        // 长度不同直接否（FixedTimeEquals 要求等长，否则抛异常）
        return expected.Length == signature.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(signature));
    }
}
