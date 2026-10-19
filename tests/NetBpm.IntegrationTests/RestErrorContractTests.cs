using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine;
using NetBpm.Rest;

namespace NetBpm.IntegrationTests;

/// <summary>
/// P0-3 的对外契约：重试耗尽的乐观锁冲突必须是 <b>409 Conflict</b>（而不是 400 或 500），
/// 其它引擎业务错误仍是 400。前端据此提示"状态已变化，请刷新"。
/// </summary>
public sealed class RestErrorContractTests
{
    [Fact]
    public async Task OptimisticLockingConflict_ShouldMapTo409()
    {
        var (statusCode, body) = await HandleAsync(new OptimisticLockingException("流程实例已被其他请求修改。"));

        Assert.Equal(StatusCodes.Status409Conflict, statusCode);
        Assert.Contains("流程实例已被其他请求修改", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineBusinessError_ShouldStillMapTo400()
    {
        var (statusCode, body) = await HandleAsync(new EngineException("任务不存在。"));

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("任务不存在", body, StringComparison.Ordinal);
    }

    private static async Task<(int StatusCode, string Body)> HandleAsync(Exception exception)
    {
        var httpContext = new DefaultHttpContext();
        using var body = new MemoryStream();
        httpContext.Response.Body = body;

        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        var handled = await handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);

        Assert.True(handled);

        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8);

        return (httpContext.Response.StatusCode, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
