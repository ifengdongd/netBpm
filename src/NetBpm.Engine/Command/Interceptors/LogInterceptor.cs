using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetBpm.Engine.Command.Interceptors;

/// <summary>
/// 最外层拦截器：记录命令名称与耗时。对应 Activiti 的 <c>LogInterceptor</c>。
/// </summary>
public sealed class LogInterceptor : ICommandInterceptor
{
    private readonly ILogger<LogInterceptor> _logger;

    public LogInterceptor(ILogger<LogInterceptor>? logger = null)
    {
        _logger = logger ?? NullLogger<LogInterceptor>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<TResult> InterceptAsync<TResult>(
        ICommand<TResult> command,
        ICommandContext context,
        CommandHandler<TResult> nextHandler,
        CancellationToken ct = default)
    {
        var commandName = command.GetType().Name;

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return await nextHandler(ct).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await nextHandler(ct).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogDebug("命令 {Command} 完成，耗时 {ElapsedMs} ms", commandName, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "命令 {Command} 失败，耗时 {ElapsedMs} ms", commandName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
