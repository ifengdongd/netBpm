using Microsoft.AspNetCore.Http;
using NetBpm.Platform.Contracts;
using NetBpm.Rest.Security;

namespace NetBpm.Rest.Platform;

/// <summary>
/// 平台面的错误响应构造（RFC 9457 <see cref="PlatformProblem"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 新平台面（<c>/workflow/v1</c> 起）一律用 <see cref="PlatformProblem"/>，
/// 不再用旧宿主里的 <c>ErrorResponse</c>：后者是国内常见的 <c>{ code, message }</c> 信封的变体，
/// 与 HTTP 状态码语义重复，且网关与各语言客户端的通用错误处理都要额外适配。
/// </para>
/// <para>
/// 过渡期两个错误体并存是<b>有意</b>的：旧端点（<c>/api/**</c>）改错误体属于破坏性变更，
/// 要跟着 SDK 一起发版；新端点从第一天就用对。
/// </para>
/// </remarks>
public static class PlatformProblems
{
    /// <summary>构造错误响应。</summary>
    /// <param name="http">当前请求（取路径作为 <c>instance</c>）。</param>
    /// <param name="context">身份上下文（取请求 id 作为 <c>traceId</c>）。</param>
    /// <param name="code">稳定错误码（见 <see cref="ErrorCodes"/>）。</param>
    /// <param name="title">短标题。</param>
    /// <param name="status">HTTP 状态码。</param>
    /// <param name="detail">具体说明。</param>
    /// <param name="errors">字段级明细。</param>
    public static IResult Create(
        HttpContext http,
        RequestContext context,
        string code,
        string title,
        int status,
        string? detail = null,
        IReadOnlyList<PlatformFieldError>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(context);

        var problem = PlatformProblem.Create(code, title, status, detail) with
        {
            Instance = http.Request.Path.Value,
            TraceId = string.IsNullOrEmpty(context.RequestId) ? http.TraceIdentifier : context.RequestId,
            Errors = errors,
        };

        return Results.Json(problem, statusCode: status, contentType: "application/problem+json");
    }

    /// <summary>403：缺少 scope。</summary>
    /// <param name="http">当前请求。</param>
    /// <param name="context">身份上下文。</param>
    /// <param name="scope">缺少的 scope。</param>
    public static IResult InsufficientScope(HttpContext http, RequestContext context, string scope) =>
        Create(
            http,
            context,
            ErrorCodes.AuthInsufficientScope,
            "缺少授权范围",
            StatusCodes.Status403Forbidden,
            $"当前凭据未获授予 scope '{scope}'。接入方需要在客户端注册时申请该 scope。");

    /// <summary>404：流程不存在或对当前调用方不可见。</summary>
    /// <param name="http">当前请求。</param>
    /// <param name="context">身份上下文。</param>
    /// <param name="processKey">流程 key。</param>
    /// <remarks>
    /// 未授权与不存在<b>共用</b>这一个响应：区分它们等于告诉调用方「这条流程存在，只是你没被授权」，
    /// 多系统共用一套引擎时这就是一份流程清单泄露。
    /// </remarks>
    public static IResult ProcessNotFound(HttpContext http, RequestContext context, string processKey) =>
        Create(
            http,
            context,
            ErrorCodes.ProcessNotFound,
            "流程不存在",
            StatusCodes.Status404NotFound,
            $"流程 '{processKey}' 不存在，或当前调用方未被授权使用它。");

    /// <summary>404：待办不存在、已完成，或对当前调用方不可见。</summary>
    /// <param name="http">当前请求。</param>
    /// <param name="context">身份上下文。</param>
    /// <param name="taskId">任务 id。</param>
    /// <remarks>与 <see cref="ProcessNotFound"/> 同口径：未授权与不存在共用 404，不泄露待办归属。</remarks>
    public static IResult TaskNotFound(HttpContext http, RequestContext context, string taskId) =>
        Create(
            http,
            context,
            ErrorCodes.TaskNotFound,
            "待办不存在",
            StatusCodes.Status404NotFound,
            $"待办 '{taskId}' 不存在、已完成，或当前调用方未被授权查看它。");

    /// <summary>404：实例不存在，或对当前调用方不可见。</summary>
    /// <param name="http">当前请求。</param>
    /// <param name="context">身份上下文。</param>
    /// <param name="instanceId">实例 id。</param>
    /// <remarks>与 <see cref="ProcessNotFound"/> 同口径：未授权与不存在共用 404，不泄露实例归属。</remarks>
    public static IResult InstanceNotFound(HttpContext http, RequestContext context, string instanceId) =>
        Create(
            http,
            context,
            ErrorCodes.InstanceNotFound,
            "实例不存在",
            StatusCodes.Status404NotFound,
            $"实例 '{instanceId}' 不存在，或当前调用方未被授权查看它。");
}
