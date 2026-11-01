using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetBpm.Persistence.Idempotency;
using NetBpm.Platform.Contracts;

namespace NetBpm.Rest.Security;

/// <summary>
/// HTTP 幂等：客户端带 <c>Idempotency-Key</c> 时，同键重试直接回放首次响应。
/// <para>
/// 只对改变状态的请求生效（POST / PUT / PATCH / DELETE），且仅在显式提供幂等键时介入，
/// 因此对既有调用方零影响。存储由 <see cref="IIdempotencyStore"/> 决定：
/// 单进程用内存，数据库宿主用 <c>ACT_IDEMPOTENCY</c>（跨实例）。
/// </para>
/// <para>
/// 覆盖范围是<b>所有写入口</b>：<c>/api/**</c>（引擎原语面）、<c>/workflow/v1</c>（业务面）、
/// <c>/admin/v1</c>（管理面）。业务面的发起 / 提交尤其依赖它——
/// 「网络超时了，到底开没开单」这个问题只能靠同键重试回答。
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

    /// <summary>
    /// 存储列的宽度（<c>ACT_IDEMPOTENCY.IDEMPOTENCY_KEY_ varchar(120)</c>）。
    /// 加上命名空间前缀后不能越过它，否则 MySQL 严格模式直接报错。
    /// </summary>
    private const int MaxStorageKeyLength = 120;

    private const int MaxCachedResponseBytes = 1024 * 1024;

    /// <summary>问题体的序列化口径（camelCase，与端点里 <c>Results.Json</c> 一致）。</summary>
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);
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
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                $"Idempotency-Key 最长 {MaxKeyLength} 个字符。",
                ErrorCodes.RequestMalformed)
                .ConfigureAwait(false);

            return;
        }

        // 身份取自鉴权解析结果（JWT 声明优先），而不是可伪造的请求头：
        // 否则不同用户用同一个 Idempotency-Key 会互相回放对方的响应。
        var identity = context.RequestServices.GetRequiredService<RequestContext>();

        // 幂等键必须按「谁在调用」分命名空间：键是全局唯一主键，两个接入方各自生成
        // 幂等键时迟早撞车（业务系统爱用「业务单号+步骤」这类自造键，不像 UUID 那样随机）。
        // 撞上之后后到的一方会拿到 409「该键已用于另一个请求」，而它明明是第一次调用 ——
        // 用同样的键永远重试不了，只能等 24 小时过期。
        var storageKey = ToStorageKey(identity, key);

        var requestHash = await ComputeRequestHashAsync(context.Request, identity, context.RequestAborted)
            .ConfigureAwait(false);

        var existing = await store.FindAsync(storageKey, context.RequestAborted).ConfigureAwait(false);

        if (existing is not null)
        {
            await HandleExistingAsync(context, existing, requestHash).ConfigureAwait(false);
            return;
        }

        var now = DateTime.UtcNow;
        var record = new IdempotencyRecord(
            storageKey,
            requestHash,
            StatusCode: null,
            ContentType: null,
            ResponseBody: null,
            CreatedAt: now,
            ExpiresAt: now.Add(Retention));

        if (!await store.TryBeginAsync(record, context.RequestAborted).ConfigureAwait(false))
        {
            // 并发：两个请求同时占位，后到的那个重新读一次
            var raced = await store.FindAsync(storageKey, context.RequestAborted).ConfigureAwait(false);

            if (raced is not null)
            {
                await HandleExistingAsync(context, raced, requestHash).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "幂等键正在处理中，请稍后重试。",
                ErrorCodes.IdempotencyInFlight)
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
                await store.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
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
                await store.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var body = buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                await store
                    .CompleteAsync(
                        storageKey,
                        statusCode,
                        context.Response.ContentType,
                        body,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // 幂等存储故障不得影响已经成功的业务响应：记日志并放弃缓存
            _logger.LogError(exception, "写入幂等记录失败：{Key}", key);

            try
            {
                await store.DeleteAsync(storageKey, CancellationToken.None).ConfigureAwait(false);
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
                "该 Idempotency-Key 已用于另一个请求（方法 / 路径 / 请求体不同）。",
                ErrorCodes.IdempotencyKeyReused);

            return;
        }

        if (existing.StatusCode is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                "相同 Idempotency-Key 的请求正在处理中，请稍后重试。",
                ErrorCodes.IdempotencyInFlight);

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

    /// <summary>
    /// 把调用方给的幂等键落到某个命名空间下，避免互不相关的调用方互相抢键。
    /// <para>
    /// 命名空间优先用 <c>caller</c>（接入系统），其次是真人 <c>subject</c> 与租户，都没有才落
    /// <c>anon</c>。同一个 subject 换 key、或同一个 key 换 subject，落到的行都不同 ——
    /// 这正是要的效果：幂等的边界应当是「一个调用主体的一次意图」，而不是全局字符串。
    /// </para>
    /// <para>
    /// 存储形态刻意保持可读（<c>cl_order:order-1001-submit</c> 而不是一坨哈希）：这张表是
    /// 24 小时缓存，排障时最常做的就是按业务单号 <c>like</c> 一把。
    /// </para>
    /// </summary>
    internal static string ToStorageKey(RequestContext identity, string rawKey)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var ns = NormalizeNamespace(ResolveNamespace(identity));
        var plain = $"{ns}\u001f{rawKey}";

        // 兜底：命名空间 + 原始键塞不进列宽时整体换成哈希。宁可不可读，也不能让 insert 报错。
        return plain.Length <= MaxStorageKeyLength
            ? plain
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));
    }

    private static string ResolveNamespace(RequestContext identity) =>
        !string.IsNullOrWhiteSpace(identity.CallerId) ? $"c\u001f{identity.CallerId.Trim()}"
        : !string.IsNullOrWhiteSpace(identity.SubjectId) ? $"s\u001f{identity.SubjectId.Trim()}"
        : !string.IsNullOrWhiteSpace(identity.TenantId) ? $"t\u001f{identity.TenantId.Trim()}"
        : "anon";

    /// <summary>
    /// 命名空间里可能带分隔符以外的杂乱字符，统一压成安全的短记号；
    /// 过长的（比如把 JWT 整个塞进来）截断后再补哈希，避免截断引起碰撞。
    /// </summary>
    private static string NormalizeNamespace(string ns)
    {
        const int MaxNamespaceLength = 40;

        if (ns.Length <= MaxNamespaceLength)
        {
            return ns;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns)))[..8];

        return $"{ns[..MaxNamespaceLength]}~{digest}";
    }

    /// <summary>
    /// 错误体形状按<b>路径</b>选：平台面必须回 RFC 9457，否则 SDK 解析不出错误码，
    /// 调用方只能看到「409 加一段 JSON」。
    /// </summary>
    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        string platformCode)
    {
        context.Response.StatusCode = statusCode;

        if (IsPlatformSurface(context.Request.Path))
        {
            context.Response.ContentType = "application/problem+json";

            var problem = PlatformProblem.Create(
                platformCode,
                statusCode == StatusCodes.Status409Conflict ? "幂等键冲突" : "请求不合法",
                statusCode,
                message) with
            {
                Instance = context.Request.Path.Value,
                TraceId = context.TraceIdentifier,
            };

            await context.Response
                .WriteAsJsonAsync(problem, ProblemJsonOptions, context.RequestAborted)
                .ConfigureAwait(false);

            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response
            .WriteAsJsonAsync(new ErrorResponse(
                message,
                Code: statusCode == StatusCodes.Status409Conflict ? "idempotency_conflict" : "bad_request"))
            .ConfigureAwait(false);
    }

    private static bool ShouldHandle(HttpRequest request) =>
        IsPlatformSurface(request.Path)
        && !string.IsNullOrWhiteSpace(request.Headers[HeaderName].ToString())
        && (HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method));

    /// <summary>是否属于「写入口」：引擎原语面 + 平台业务面 + 平台管理面。</summary>
    private static bool IsPlatformSurface(PathString path) =>
        path.StartsWithSegments("/api")
        || path.StartsWithSegments("/workflow/v1")
        || path.StartsWithSegments("/admin/v1");

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
