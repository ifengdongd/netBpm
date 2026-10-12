using Microsoft.Extensions.Logging;

namespace NetBpm.Engine.Command;

/// <summary>
/// 默认命令执行器：按注册顺序串起拦截器链，并在链外维护
/// <see cref="CommandContext.Current"/>（<see cref="AsyncLocal{T}"/>）。
/// </summary>
public sealed class CommandExecutor : ICommandExecutor
{
    private readonly IReadOnlyList<ICommandInterceptor> _interceptors;
    private readonly Func<CommandContext> _contextFactory;
    private readonly ILogger<CommandExecutor> _logger;

    public CommandExecutor(
        IEnumerable<ICommandInterceptor> interceptors,
        Func<CommandContext> contextFactory,
        ILogger<CommandExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(contextFactory);

        _interceptors = interceptors as IReadOnlyList<ICommandInterceptor> ?? interceptors.ToList();
        _contextFactory = contextFactory;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CommandExecutor>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var context = _contextFactory();
        var previous = CommandContext.Swap(context);

        try
        {
            return await InvokeAsync(0, command, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 命令失败 ⇒ 事务回滚 ⇒ 队列中未执行的操作必须丢弃，
            // 否则残留操作会被后续命令（同一上下文）误执行。
            _logger.LogWarning(ex, "命令 {Command} 执行失败，清空议程队列。", command.GetType().Name);
            context.Services.Agenda.Clear();
            throw;
        }
        finally
        {
            // 命令级资源（数据库连接 / 事务）随命令结束一并释放
            try
            {
                context.Transaction?.Dispose();
            }
            finally
            {
                CommandContext.Swap(previous);
            }
        }
    }

    private ValueTask<TResult> InvokeAsync<TResult>(
        int index,
        ICommand<TResult> command,
        ICommandContext context,
        CancellationToken ct)
    {
        if (index >= _interceptors.Count)
        {
            return command.ExecuteAsync(context, ct);
        }

        var interceptor = _interceptors[index];
        return interceptor.InterceptAsync(
            command,
            context,
            token => InvokeAsync(index + 1, command, context, token),
            ct);
    }
}
