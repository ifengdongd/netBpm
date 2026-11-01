using System.Net;
using NetBpm.Platform.Contracts;

namespace NetBpm.Platform.Client;

/// <summary>
/// 平台返回的<b>业务失败</b>（非 2xx），带上 RFC 9457 的问题体与稳定错误码。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <see cref="HttpRequestException"/> 直接抛：调用方需要按 <see cref="Code"/> 分支
/// （<c>task.already_claimed</c> 要提示「已被别人认领」，<c>concurrency.revision_mismatch</c> 要刷新后重试），
/// 而错误码藏在响应体里。把解析放在 SDK 里只做一次，「从 HTML 错误页里猜原因」这种事故也就少一次。
/// </para>
/// <para>
/// <see cref="Problem"/> 可能为 <see langword="null"/>：网关 502 / 反向代理返回的 HTML 不是 RFC 9457。
/// 此时 <see cref="ResponseBody"/> 保留原文（截断），至少能贴进工单。
/// </para>
/// </remarks>
public sealed class NetBpmApiException : Exception
{
    /// <summary>响应体保留上限（超过只在异常里保留前缀，避免把几十 MB 的 HTML 挂进内存）。</summary>
    private const int MaxBodyLength = 4096;

    /// <summary>构造。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="problem">解析出来的问题体；解析失败为 <see langword="null"/>。</param>
    /// <param name="responseBody">原始响应体（已截断）。</param>
    /// <param name="requestPath">请求路径（拼进异常消息，便于定位）。</param>
    public NetBpmApiException(
        HttpStatusCode statusCode,
        PlatformProblem? problem,
        string? responseBody,
        string requestPath)
        : base(BuildMessage(statusCode, problem, responseBody, requestPath))
    {
        StatusCode = statusCode;
        Problem = problem;
        ResponseBody = responseBody;
        RequestPath = requestPath;
    }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>解析出来的问题体（可能为 <see langword="null"/>）。</summary>
    public PlatformProblem? Problem { get; }

    /// <summary>稳定错误码（见 <see cref="ErrorCodes"/>）；问题体缺失时为 <see langword="null"/>。</summary>
    public string? Code => Problem?.Code;

    /// <summary>服务端追踪 id（与响应头 <c>X-Request-Id</c> 相同），排查时给服务端看这个。</summary>
    public string? TraceId => Problem?.TraceId;

    /// <summary>字段级明细（400 / 422 才有）。</summary>
    public IReadOnlyList<PlatformFieldError> Errors => Problem?.Errors ?? [];

    /// <summary>原始响应体（截断到 4 KB）。</summary>
    public string? ResponseBody { get; }

    /// <summary>请求路径。</summary>
    public string RequestPath { get; }

    /// <summary>是否「不存在」（404）。平台面把「未授权」也归到这里，不泄露存在性。</summary>
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    /// <summary>是否并发冲突（409 / 412）。重试前通常需要刷新本地状态。</summary>
    public bool IsConflict => StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed;

    /// <summary>是否输入不合法（400 / 422）。原样重试没有意义。</summary>
    public bool IsInvalidRequest =>
        StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;

    /// <summary>是否凭据问题（401 / 403）。换令牌或补 scope，而不是重试。</summary>
    public bool IsAuthorizationFailure =>
        StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>是否服务端临时故障（429 / 5xx）。可以带退避重试。</summary>
    public bool IsTransient => StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode >= 500;

    private static string BuildMessage(
        HttpStatusCode statusCode,
        PlatformProblem? problem,
        string? responseBody,
        string requestPath)
    {
        if (problem is not null)
        {
            var detail = string.IsNullOrWhiteSpace(problem.Detail) ? string.Empty : "：" + problem.Detail;

            return $"{(int)statusCode} {problem.Code} {problem.Title}{detail}（{requestPath}）";
        }

        var body = string.IsNullOrWhiteSpace(responseBody)
            ? "（响应体为空）"
            : "（响应体不是 RFC 9457：" + responseBody.Trim() + "）";

        return $"{(int)statusCode} 平台请求失败{body}（{requestPath}）";
    }

    /// <summary>截断响应体（保留前缀，够定位即可）。</summary>
    internal static string? Truncate(string? body) =>
        body is null || body.Length <= MaxBodyLength ? body : body[..MaxBodyLength] + "…";
}
