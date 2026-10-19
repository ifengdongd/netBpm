using System.Security.Cryptography;
using System.Text;
using NetBpm.Persistence.Idempotency;

namespace NetBpm.Rest.Security;

/// <summary>
/// HTTP 幂等：客户端带 <c>Idempotency-Key</c> 时，同键重试直接回放首次响应。
/// <para>
/// 只对改变状态的请求生效（POST / PUT / PATCH / DELETE），且仅在显式提供幂等键时介入，
/// 因此对既有调用方零影响。存储由 <see cref="IIdempotencyStore"/> 决定：
/// 单进程用内存，数据库宿主用 <c>ACT_IDEMPOTENCY</c>（跨实例）。
/// </para>
/// <para>
/// 语义：
/// <list type="bullet">
///   <item><description>同键 + 同请求 → 回放原状态码与响应体，附 <c>Idempotency-Replayed: true</c>；</description></item>
///   <item><description>同键 + 不同请求 → 409（防止误复用键）；</description></item>
///   <item><description>同键 + 首次仍在执行 → 409（不阻塞、不重复执行）；</description></item>
///   <item><description>下游 5xx / 抛异常 → 删除占位，允许客户端用同键重试。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    /// <summary>幂等键请求头。</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>回放标记响应头。</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    private const int MaxKeyLength = 120;
    private const int MaxCachedResponseBytes = 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        ILogger<IdempotencyMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _logger = logger;
    }

    /// <summary>幂等中间件入口。</summary>
    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);

        if (!ShouldHandle(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString().Trim();

        if (key.Length > MaxKeyLength)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, $"Idempotency-Key 最长 {MaxKeyLength} 个字符。")
                .ConfigureAwait(false);

            return;
        }

        // 身份取自鉴权解析结果（JWT 声明优先），而不是可伪造的请求头：
        // 否则不同用户用同一个 Idempotency-Key 会互相回放对方的响应。
        var identity = context.RequestServices.GetRequiredService<RequestContext>();

        var requestHash = await ComputeRequestHashAsync(context.Request, identity, context.RequestAborted)
            .ConfigureAwait(false);

        var existing = await store.FindAsync(key, context.RequestAborted).ConfigureAwait(false);

        if (existing is not null)
        {
            await HandleExistingAsync(context, existing, requestHash).ConfigureAwait(false);
            return;
        }

        var now = DateTime.UtcNow;
        var record = new IdempotencyRecord(
            key,
            requestHash,
            StatusCode: null,
            ContentType: null,
            ResponseBody: null,
            CreatedAt: now,
            ExpiresAt: now.Add(Retention));

        if (!await store.TryBeginAsync(record, context.RequestAborted).ConfigureAwait(false))
        {
            // 并发：两个请求同时占位，后到的那个重新读一次
            var raced = await store.FindAsync(key, context.RequestAborted).ConfigureAwait(false);

            if (raced is not null)
            {
                await HandleExistingAsync(context, raced, requestHash).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(context, StatusCodes.Status409Conflict, "幂等键正在处理中，请稍后重试。")
                .ConfigureAwait(false);

            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();

        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch
        {
            // 下游异常：撤销占位，让客户端可以用同键安全重试
            context.Response.Body = originalBody;

            try
            {
                await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "清理幂等占位失败：{Key}", key);
            }

            throw;
        }

        context.Response.Body = originalBody;

        var statusCode = context.Response.StatusCode;

        try
        {
            if (statusCode >= StatusCodes.Status500InternalServerError || buffer.Length > MaxCachedResponseBytes)
            {
                await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var body = buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                await store
                    .CompleteAsync(key, statusCode, context.Response.ContentType, body, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // 幂等存储故障不得影响已经成功的业务响应：记日志并放弃缓存
            _logger.LogError(exception, "写入幂等记录失败：{Key}", key);

            try
            {
                await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "清理幂等占位失败：{Key}", key);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleExistingAsync(
        HttpContext context,
        IdempotencyRecord existing,
        string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "该 Idempotency-Key 已用于另一个请求（方法 / 路径 / 请求体不同）。");

            return;
        }

        if (existing.StatusCode is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "相同 Idempotency-Key 的请求正在处理中，请稍后重试。");

            return;
        }

        _logger.LogInformation("回放幂等响应：{Key} → {StatusCode}", existing.Key, existing.StatusCode);

        context.Response.StatusCode = existing.StatusCode.Value;
        context.Response.Headers[ReplayedHeaderName] = "true";

        if (!string.IsNullOrEmpty(existing.ContentType))
        {
            context.Response.ContentType = existing.ContentType;
        }

        if (!string.IsNullOrEmpty(existing.ResponseBody))
        {
            await context.Response.WriteAsync(existing.ResponseBody, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response
            .WriteAsJsonAsync(new ErrorResponse(
                message,
                Code: statusCode == StatusCodes.Status409Conflict ? "idempotency_conflict" : "bad_request"))
            .ConfigureAwait(false);
    }

    private static bool ShouldHandle(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && !string.IsNullOrWhiteSpace(request.Headers[HeaderName].ToString())
        && (HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method));

    private static async Task<string> ComputeRequestHashAsync(
        HttpRequest request,
        RequestContext identity,
        CancellationToken ct)
    {
        // 请求体可能不是 seekable：先整体读入缓冲区，再复位供下游读取
        request.EnableBuffering();

        using var bodyBuffer = new MemoryStream();
        await request.Body.CopyToAsync(bodyBuffer, ct).ConfigureAwait(false);
        request.Body.Position = 0;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.UTF8.GetBytes(request.Method));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(request.Path.Value ?? string.Empty));
        hash.AppendData(Encoding.UTF8.GetBytes(request.QueryString.Value ?? string.Empty));
        hash.AppendData([0]);

        // 身份参与哈希：同一个 key 不能被另一个用户 / 租户复用
        hash.AppendData(Encoding.UTF8.GetBytes(identity.UserId ?? string.Empty));
        hash.AppendData([0]);
        hash.AppendData(Encoding.UTF8.GetBytes(identity.TenantId ?? string.Empty));
        hash.AppendData([0]);
        hash.AppendData(bodyBuffer.ToArray());

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
