using System.Data.Common;
using Microsoft.AspNetCore.Diagnostics;
using NetBpm.Bpmn.Converter;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine;
using NetBpm.Persistence;
using NetBpm.Persistence.Platform;
using NetBpm.Rest.Security;

namespace NetBpm.Rest;

/// <summary>
/// 全局异常处理器：把异常按<b>语义</b>映射成稳定状态码，并统一返回 JSON 错误体。
/// <para>映射约定（REST 契约的一部分）：</para>
/// <list type="table">
///   <item><description>400：请求本身有问题（校验失败 / 参数非法 / 引擎业务规则拒绝）；</description></item>
///   <item><description>403：已认证，但身份或租户与令牌不一致；</description></item>
///   <item><description>404：目标对象不存在（含跨租户访问，避免存在性泄露）；</description></item>
///   <item><description>409：状态冲突（已认领 / 仍有运行实例 / 挂起 / 乐观锁失败 / 唯一约束冲突）；</description></item>
///   <item><description>501：明确未实现的能力；</description></item>
///   <item><description>503：数据库暂时不可用（可重试，不回传连接串与 SQL）；</description></item>
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
            ProcessValidationException ex => (
                StatusCodes.Status400BadRequest,
                ex.Message,
                ex.Errors,
                EngineErrorCode.ValidationError),

            BpmnXmlParseException ex => (
                StatusCodes.Status400BadRequest,
                ex.Message,
                null,
                EngineErrorCode.BpmnXmlParseError),

            // 查询参数 / 路由参数绑定失败（例如枚举取值不匹配、数字格式错）。
            // 不接这一条的话 Minimal API 抛的上游异常会落到兜底的 500：
            // 调用方写错一个参数就得到「服务器内部错误」，排查方向完全被带偏。
            BadHttpRequestException ex => (
                StatusCodes.Status400BadRequest,
                ex.Message,
                null,
                EngineErrorCode.ValidationError),

            ForbiddenException ex => (StatusCodes.Status403Forbidden, ex.Message, null, EngineErrorCode.Forbidden),

            // 接入管理：scope 未定义 / 高危未确认 / 租户不存在等，错误码随异常走（客户端按码分支）
            ClientRegistryException ex => (
                StatusCodes.Status400BadRequest,
                ex.Message,
                null,
                ex.Code),

            // 带业务码的 404（例如「实例不在运行中」→ 调用方应改查历史）
            ResourceNotFoundException ex => (StatusCodes.Status404NotFound, ex.Message, null, ex.Code),

            EngineObjectNotFoundException ex => (
                StatusCodes.Status404NotFound,
                ex.Message,
                null,
                EngineErrorCode.NotFound),

            OptimisticLockingException ex => (
                StatusCodes.Status409Conflict,
                ex.Message,
                null,
                EngineErrorCode.OptimisticLockConflict),

            EngineConflictException ex => (StatusCodes.Status409Conflict, ex.Message, null, EngineErrorCode.Conflict),

            EngineNotImplementedException ex => (
                StatusCodes.Status501NotImplemented,
                ex.Message,
                null,
                EngineErrorCode.NotImplemented),

            // 其余引擎业务错误 = 请求不被接受
            EngineException ex => (
                StatusCodes.Status400BadRequest,
                ex.Message,
                null,
                EngineErrorCode.EngineRuleViolation),

            // 数据库层：唯一约束冲突 = 与并发写入撞车（重读后重试即可，不该是 500）；
            // 其余数据库异常不泄露 SQL / 连接串 / 账号，按「暂时不可用」让客户端重试。
            DbException ex when DatabaseErrors.IsUniqueViolation(ex) => (
                StatusCodes.Status409Conflict,
                "记录已存在或已被并发修改，请刷新后重试。",
                null,
                EngineErrorCode.UniqueViolation),

            DbException => (
                StatusCodes.Status503ServiceUnavailable,
                "数据库暂时不可用，请稍后重试；如持续出现请提供 traceId 给运维。",
                null,
                EngineErrorCode.DatabaseUnavailable),

            OperationCanceledException => (
                StatusCodes.Status499ClientClosedRequest,
                "请求已取消。",
                null,
                EngineErrorCode.RequestCancelled),

            // 兜底：不泄露 SQL / 连接串 / 堆栈；细节只进日志
            _ => (
                StatusCodes.Status500InternalServerError,
                "服务器内部错误，请稍后重试；如持续出现请提供 traceId 给运维。",
                null,
                EngineErrorCode.InternalError),
        };
}
