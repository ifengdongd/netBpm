using Microsoft.AspNetCore.Diagnostics;
using NetBpm.Engine;

namespace NetBpm.Rest;

/// <summary>
/// 全局异常处理器：把未处理异常（数据库不可达、SQL 失败、空引用等）
/// 记录到日志并返回统一 JSON 错误体，而不是 ASP.NET Core 兜底的纯文本
/// "Internal Server Error"——前端部署列表因此能拿到可读的错误说明，
/// 而不是「加载失败：Internal Server Error」。
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "未处理异常：{Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        var (statusCode, message) = exception switch
        {
            EngineException ex => (StatusCodes.Status400BadRequest, ex.Message),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "请求已取消。"),
            _ => (
                StatusCodes.Status500InternalServerError,
                $"服务器内部错误：{exception.Message}（详情见服务端日志）"),
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(
            new ErrorResponse(message),
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}
