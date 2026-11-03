using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.El;
using NetBpm.Engine;
using NetBpm.Platform.Contracts;
using NetBpm.Rest;
using NetBpm.Rest.Security;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 异常 → 状态码映射是 REST 契约的一部分：这里锁住 4xx 语义，以及 5xx 不回传异常原文。
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(ExpectedMappings))]
    public async Task DomainException_ShouldMapToExpectedStatus(Exception exception, int expectedStatus)
    {
        var (status, body) = await HandleAsync(exception);

        Assert.Equal(expectedStatus, status);

        using var json = JsonDocument.Parse(body);

        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
    }

    public static TheoryData<Exception, int> ExpectedMappings => new()
    {
        { new EngineException("业务规则拒绝"), StatusCodes.Status400BadRequest },
        { new EngineObjectNotFoundException("任务 '1' 不存在"), StatusCodes.Status404NotFound },
        { new EngineConflictException("任务已被认领"), StatusCodes.Status409Conflict },
        { new OptimisticLockingException("revision 冲突"), StatusCodes.Status409Conflict },
        {
            new EngineNotImplementedException("子流程未实现", "subProcess", "P10"),
            StatusCodes.Status501NotImplemented
        },
        { new ForbiddenException("租户与令牌不一致"), StatusCodes.Status403Forbidden },
        // BPMN 表达式求值失败是 422 而不是 500：消息可行动，且不是服务端故障
        { new ElException("表达式里用到了未知属性：${applyUser}（无法解析标识符 'applyUser'。）"), StatusCodes.Status422UnprocessableEntity },
    };

    /// <summary>
    /// EL 求值失败必须把异常原文透出（点名表达式与标识符），并带稳定的错误码。
    /// </summary>
    /// <remarks>
    /// 反例就是 2026-11-03 那次：<c>assignee="${applyUser}"</c> 但发起没给变量，
    /// 控制台只显示「服务器内部错误请提供 traceId」——线索全丢，只能翻服务端日志。
    /// </remarks>
    [Fact]
    public async Task ExpressionError_ShouldSurfaceActionableMessageAndCode()
    {
        const string message = "表达式里用到了未知属性：${applyUser}（无法解析标识符 'applyUser'。）";

        var (status, body) = await HandleAsync(new ElException(message));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);

        using var json = JsonDocument.Parse(body);

        Assert.Equal(message, json.RootElement.GetProperty("message").GetString());
        Assert.Equal(ErrorCodes.EngineExpressionError, json.RootElement.GetProperty("code").GetString());
        Assert.Contains("applyUser", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledException_ShouldNotLeakInternalMessage()
    {
        var (status, body) = await HandleAsync(
            new InvalidOperationException("SELECT * FROM ACT_RU_TASK; Server=db-prod;Password=secret"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.DoesNotContain("ACT_RU_TASK", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(body);
        Assert.Contains("traceId", body, StringComparison.Ordinal);
        Assert.Equal(
            "服务器内部错误，请稍后重试；如持续出现请提供 traceId 给运维。",
            json.RootElement.GetProperty("message").GetString());
    }

    private static async Task<(int Status, string Body)> HandleAsync(Exception exception)
    {
        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        var buffer = new MemoryStream();
        context.Response.Body = buffer;

        var handled = await handler.TryHandleAsync(context, exception, Ct);

        Assert.True(handled);

        buffer.Position = 0;
        var body = await new StreamReader(buffer).ReadToEndAsync(Ct);

        return (context.Response.StatusCode, body);
    }
}
