using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace NetBpm.Rest.Security;

/// <summary>
/// 外部 IdP 的公钥集（<c>NetBpm:Security:Jwt:JwksUri</c>）缓存。
/// </summary>
/// <remarks>
/// <para>
/// 「外部 IdP 直连」的含义：令牌不由本服务签发，本服务只需要<b>公钥</b>就能验签。
/// 于是接入企业统一身份时不必让平台持有任何私钥，也不需要把 IdP 的用户同步过来。
/// </para>
/// <para>
/// 缓存策略：成功拉取后 10 分钟内直接复用；<c>kid</c> 命中不了时允许重新拉取，
/// 但两个拉取之间至少隔 30 秒——否则一个带着随机 <c>kid</c> 的令牌就能把我们变成
/// 对 IdP 的压测工具（缓存击穿 + 外部依赖放大）。
/// </para>
/// <para>
/// 拉取失败不抛错：保留上一次的密钥集继续验签，只记警告。IdP 抖一下不该让全站 401。
/// </para>
/// <para>
/// 🔑 校验路径上的解析器是同步签名（<c>IssuerSigningKeyResolver</c>），
/// 所以这里用同步 HTTP。它只在缓存过期或 <c>kid</c> 未知时发生，不在每个请求的热路径上。
/// </para>
/// </remarks>
public sealed class RemoteJwksProvider
{
    /// <summary>配置键：外部 IdP 的 JWKS 地址。</summary>
    public const string ConfigKey = "NetBpm:Security:Jwt:JwksUri";

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ILogger<RemoteJwksProvider> _logger;
    private readonly string? _jwksUri;
    private readonly TimeSpan _minRefreshInterval;
    private readonly object _gate = new();

    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>构造。</summary>
    /// <param name="configuration">配置。</param>
    /// <param name="http">HTTP 客户端（由容器提供，避免每次 new 造成 socket 耗尽）。</param>
    /// <param name="logger">日志。</param>
    public RemoteJwksProvider(
        IConfiguration configuration,
        HttpClient http,
        ILogger<RemoteJwksProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _logger = logger;

        // 两次拉取之间的最小间隔：防止「带随机 kid 的令牌」把我们变成 IdP 的压测工具。
        // 默认 30 秒；测试与已知会频繁轮换密钥的环境可以调小。
        var seconds = configuration.GetValue("NetBpm:Security:Jwt:JwksMinRefreshSeconds", 30);
        _minRefreshInterval = TimeSpan.FromSeconds(Math.Max(0, seconds));

        var uri = configuration[ConfigKey]?.Trim();

        if (!string.IsNullOrEmpty(uri))
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    $"{ConfigKey} 必须是绝对的 http(s) 地址，实际为 '{uri}'。");
            }

            _jwksUri = parsed.ToString();
        }
    }

    /// <summary>是否配置了外部 JWKS。</summary>
    public bool IsConfigured => _jwksUri is not null;

    /// <summary>
    /// 取验签公钥：优先按 <paramref name="keyId"/> 过滤（<c>kid</c> 对不上就换密钥了）。
    /// </summary>
    /// <param name="keyId">令牌头里的 <c>kid</c>。</param>
    public IReadOnlyList<SecurityKey> Resolve(string? keyId)
    {
        if (_jwksUri is null)
        {
            return [];
        }

        lock (_gate)
        {
            var expired = DateTimeOffset.UtcNow - _fetchedAt > KeyLifetime;
            var unknownKey = keyId is { Length: > 0 } && !ContainsKey(_keys, keyId);

            if (expired || (unknownKey && CanRefresh()))
            {
                Refresh();
            }

            return keyId is not { Length: > 0 }
                ? _keys
                : [.. _keys.Where(key => string.Equals(key.KeyId, keyId, StringComparison.Ordinal))];
        }
    }

    private bool CanRefresh() => DateTimeOffset.UtcNow - _fetchedAt > _minRefreshInterval;

    private static bool ContainsKey(IReadOnlyList<SecurityKey> keys, string keyId)
    {
        foreach (var key in keys)
        {
            if (string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void Refresh()
    {
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);

            var document = _http.GetStringAsync(_jwksUri!, cts.Token).GetAwaiter().GetResult();
            var keys = new JsonWebKeySet(document).GetSigningKeys().ToArray();

            if (keys.Length == 0)
            {
                // 空密钥集是「对方配置错了」而不是「没有密钥」，保留旧集合并告警
                _logger.LogWarning("{Uri} 返回的 JWKS 不含任何可用签名密钥，继续沿用上一次的密钥集。", _jwksUri);
                _fetchedAt = DateTimeOffset.UtcNow;

                return;
            }

            _keys = keys;
            _fetchedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("已刷新外部 IdP 公钥集：{Uri}（{Count} 把）", _jwksUri, keys.Length);
        }
        catch (Exception exception)
        {
            // 拉取失败不影响已有令牌的校验：沿用上一次的密钥集。
            // 超时（TaskCanceledException）也走这里——它是「对方慢」而不是「本请求被取消」。
            _fetchedAt = DateTimeOffset.UtcNow;

            _logger.LogWarning(exception, "拉取外部 IdP 公钥集失败：{Uri}（沿用上一次的密钥集）", _jwksUri);
        }
    }
}
