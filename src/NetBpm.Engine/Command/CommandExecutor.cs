using Microsoft.Extensions.Logging;

namespace NetBpm.Engine.Command;

/// <summary>
/// 默认命令执行器：按注册顺序串起拦截器链，并在链外维护
/// <see cref="CommandContext.Current"/>（<see cref="AsyncLocal{T}"/>）。
/// <para>
/// 两条生命周期规则：
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>最外层命令</b>：创建上下文（含事务与议程）→ 跑拦截器链 → 释放事务 → 恢复外层上下文。
///   </description></item>
///   <item><description>
///   <b>嵌套命令</b>（命令/监听器/服务任务里再次调用引擎）：复用当前上下文，
///   <b>不新建事务、不排空议程、不释放资源</b> —— 生命周期由最外层命令负责。
///   这正是 <see cref="Interceptors.TransactionInterceptor"/>「嵌套命令加入外层事务」的前提。
///   </description></item>
/// </list>
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

        // ── 嵌套命令：加入外层上下文 ────────────────────────────────
        if (CommandContext.Current is { } parent)
        {
            parent.EnterNested();

            try
            {
                return await InvokeAsync(0, command, parent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 内层命令失败 ⇒ 它规划过但尚未执行的操作不能留给外层继续排空
                // （否则会执行一条已经失败的命令留下的半成品操作）。
                _logger.LogWarning(
                    ex,
                    "嵌套命令 {Command} 执行失败，清空当前命令的议程队列。",
                    command.GetType().Name);

                parent.Agenda.Clear();
                throw;
            }
            finally
            {
                parent.ExitNested();
            }
        }

        // ── 最外层命令：新建上下文 ──────────────────────────────────
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
            context.Agenda.Clear();
            throw;
        }
        finally
        {
            // 命令级资源（数据库连接 / 事务）随命令结束一并释放。
            // 重试拦截器可能已换过事务，这里释放的是「最后一次尝试」的那一个。
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
