using Microsoft.AspNetCore.Diagnostics;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Rest.Security;

namespace NetBpm.Rest;

/// <summary>
/// 全局异常处理器：把异常按<b>语义</b>映射成稳定状态码，并统一返回 JSON 错误体。
/// <para>映射约定（REST 契约的一部分）：</para>
/// <list type="table">
///   <item><description>400：请求本身有问题（校验失败 / 参数非法 / 引擎业务规则拒绝）；</description></item>
///   <item><description>403：已认证，但身份或租户与令牌不一致；</description></item>
///   <item><description>404：目标对象不存在（含跨租户访问，避免存在性泄露）；</description></item>
///   <item><description>409：状态冲突（已认领 / 仍有运行实例 / 挂起 / 乐观锁失败）；</description></item>
///   <item><description>501：明确未实现的能力；</description></item>
///   <item><description>500：未预期异常，<b>不回传异常原文</b>，只给 traceId。</description></item>
/// </list>
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, message, errors, code) = Map(exception);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "未处理异常：{Method} {Path} ({TraceId})",
                httpContext.Request.Method, httpContext.Request.Path, httpContext.TraceIdentifier);
        }
        else
        {
            logger.LogWarning(exception, "请求被拒绝（{StatusCode}）：{Method} {Path} ({TraceId})",
                statusCode, httpContext.Request.Method, httpContext.Request.Path, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(message, errors, httpContext.TraceIdentifier, code),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static (int StatusCode, string Message, IReadOnlyList<ValidationError>? Errors, string? Code) Map(
        Exception exception) =>
        exception switch
        {
            // 部署校验：带明细，前端可逐条定位
            ProcessValidationException ex => (StatusCodes.Status400BadRequest, ex.Message, ex.Errors, "validation_error"),

            BpmnXmlParseException ex => (StatusCodes.Status400BadRequest, ex.Message, null, "bpmn_xml_parse_error"),

            ForbiddenException ex => (StatusCodes.Status403Forbidden, ex.Message, null, "forbidden"),

            EngineObjectNotFoundException ex => (StatusCodes.Status404NotFound, ex.Message, null, "not_found"),

            OptimisticLockingException ex => (StatusCodes.Status409Conflict, ex.Message, null, "optimistic_lock_conflict"),

            EngineConflictException ex => (StatusCodes.Status409Conflict, ex.Message, null, "conflict"),

            EngineNotImplementedException ex => (StatusCodes.Status501NotImplemented, ex.Message, null, "not_implemented"),

            // 其余引擎业务错误 = 请求不被接受
            EngineException ex => (StatusCodes.Status400BadRequest, ex.Message, null, "engine_rule_violation"),

            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "请求已取消。", null, "request_cancelled"),

            // 兜底：不泄露 SQL / 连接串 / 堆栈；细节只进日志
            _ => (
                StatusCodes.Status500InternalServerError,
                "服务器内部错误，请稍后重试；如持续出现请提供 traceId 给运维。",
                null,
                "internal_error"),
        };
}
