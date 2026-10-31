using System.Globalization;
using Microsoft.AspNetCore.Http;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.RateLimiting;

/// <summary>
/// 限流中间件：按<b>调用方</b>分区（客户端 id → 用户 → IP），而不是按 IP。
/// </summary>
/// <remarks>
/// <para>
/// 按 IP 分区在两种真实部署下都不成立：
/// <list type="bullet">
///   <item><description>多个系统共用一个出口 IP（NAT / 网关）：一家跑飞，别家全部 429；</description></item>
///   <item><description>反代后面所有人共用一个 <c>RemoteIpAddress</c>：整站共用一个桶。</description></item>
/// </list>
/// 按 <c>clientId</c> 分区才能做到「一系统一配额」，这也是客户端注册表里
/// <c>quota_per_minute</c> 存在的意义——那个字段入库存了很久，直到这里才真正生效。
/// </para>
/// <para>
/// 顺序：本中间件必须排在<b>鉴权之后</b>。放在鉴权之前就只能拿到 IP（<see cref="RequestContext"/>
/// 还没填充）。代价是未通过的请求不做限流——它们本来就过不去，且密钥比较是定长 SHA-256，不算放大面。
/// </para>
/// <para>
/// 不在限流范围的路径：<c>/health</c>（编排系统探活，限流会让它误判实例不健康）与静态资源。
/// </para>
/// </remarks>
public sealed class ClientRateLimitMiddleware
{
    /// <summary>本窗口配额。</summary>
    public const string LimitHeader = "RateLimit-Limit";

    /// <summary>本窗口剩余次数。</summary>
    public const string RemainingHeader = "RateLimit-Remaining";

    /// <summary>距离窗口重置的秒数。</summary>
    public const string ResetHeader = "RateLimit-Reset";

    /// <summary>需要限流的路径前缀（引擎面与平台面都在内，新增对外面时别忘了加）。</summary>
    private static readonly string[] LimitedPrefixes = ["/api", "/workflow", "/engine", "/admin"];

    private readonly RequestDelegate _next;
    private readonly IRateLimiter _limiter;
    private readonly ClientRateLimitOptions _options;

    /// <summary>构造。</summary>
    /// <param name="next">下一个中间件。</param>
    /// <param name="limiter">计数器（进程内 / Redis，由 <c>NetBpm:RateLimit:Store</c> 决定）。</param>
    /// <param name="options">配置。</param>
    public ClientRateLimitMiddleware(
        RequestDelegate next,
        IRateLimiter limiter,
        ClientRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _limiter = limiter;
        _options = options;
    }

    /// <summary>中间件入口。</summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="identity">已由鉴权中间件填充的身份。</param>
    public async Task InvokeAsync(HttpContext context, RequestContext identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);

        if (!IsLimitedPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var partition = Resolve(context, identity);
        var decision = await _limiter
            .AcquireAsync(partition.Key, partition.Limit, context.RequestAborted)
            .ConfigureAwait(false);

        if (decision.Counted)
        {
            context.Response.Headers[LimitHeader] = decision.Limit.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers[RemainingHeader] = decision.Remaining.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers[ResetHeader] = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            await WriteTooManyRequestsAsync(context, identity, partition, decision).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsLimitedPath(PathString path)
    {
        foreach (var prefix in LimitedPrefixes)
        {
            if (path.StartsWithSegments(prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>一次限流的分区。</summary>
    /// <param name="Key">分区键（含种类前缀，便于诊断不同种类之间不会串号）。</param>
    /// <param name="Limit">配额。</param>
    /// <param name="Kind">分区种类（进错误详情，方便调用方判断该找谁调配额）。</param>
    private readonly record struct Partition(string Key, int Limit, string Kind);

    private Partition Resolve(HttpContext context, RequestContext identity)
    {
        // 1) 注册客户端：一系统一配额。即使它代客调用（带 subject），配额也记在系统头上——
        //    配额是「这个接入方能用多少」，不是「这个人能用多少」。
        if (identity.IsClientPrincipal && !string.IsNullOrEmpty(identity.CallerId))
        {
            return new Partition(
                "client:" + identity.CallerId,
                identity.QuotaPerMinute ?? _options.ClientPerMinute,
                "client");
        }

        // 2) JWT 用户：按（租户, 操作人）分区。比按 IP 公平——一个办公室几十号人不会互相挤占
        if (identity.AuthMode == RequestAuthMode.Jwt && !string.IsNullOrEmpty(identity.SubjectId))
        {
            return new Partition(
                $"user:{identity.EffectiveTenantId}:{identity.SubjectId}",
                _options.ApiPerMinute,
                "user");
        }

        // 3) 无身份：只能按 IP，且按用途分档（登录口是口令爆破点，额度必须最低）
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var path = context.Request.Path;

        if (path.StartsWithSegments("/api/auth/login"))
        {
            return new Partition("ip:login:" + ip, _options.LoginPerMinute, "ip");
        }

        if (path.StartsWithSegments("/api/auth/me"))
        {
            return new Partition("ip:probe:" + ip, _options.ProbePerMinute, "ip");
        }

        return new Partition("ip:api:" + ip, _options.ApiPerMinute, "ip");
    }

    private static async Task WriteTooManyRequestsAsync(
        HttpContext context,
        RequestContext identity,
        Partition partition,
        RateLimitDecision decision)
    {
        var problem = PlatformProblem.Create(
            ErrorCodes.RateLimited,
            "请求过于频繁",
            StatusCodes.Status429TooManyRequests,
            $"本分钟配额已用尽（分区 {partition.Key}，上限 {decision.Limit} 次/分钟）；"
            + $"请 {decision.RetryAfterSeconds} 秒后重试，或联系平台管理员调整配额。") with
        {
            Instance = context.Request.Path.Value,
            TraceId = string.IsNullOrEmpty(identity.RequestId) ? context.TraceIdentifier : identity.RequestId,
        };

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/problem+json";

        await context.Response
            .WriteAsJsonAsync(
                problem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
    }
}
