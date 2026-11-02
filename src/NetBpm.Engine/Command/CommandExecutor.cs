using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NetBpm.Engine.Diagnostics;

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
        var commandName = command.GetType().Name;
        using var activity = EngineDiagnostics.ActivitySource.StartActivity(
            $"netbpm.command {commandName}",
            ActivityKind.Internal);

        activity?.SetTag("netbpm.command", commandName);

        var startedAt = Stopwatch.GetTimestamp();
        var result = "success";
        var context = _contextFactory();
        context.ApplyCommandId(command.CommandId);

        var previous = CommandContext.Swap(context);

        try
        {
            return await InvokeAsync(0, command, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = "error";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("netbpm.error_type", ex.GetType().Name);

            // 命令失败 ⇒ 事务回滚 ⇒ 队列中未执行的操作必须丢弃，
            // 否则残留操作会被后续命令（同一上下文）误执行。
            _logger.LogWarning(ex, "命令 {Command} 执行失败，清空议程队列。", commandName);
            context.Agenda.Clear();

            // 没有事务的宿主（内存执行管理器）没有「回滚」这回事，
            // 只能靠命令级快照把内存状态恢复成命令开始前的样子。
            context.RunAbortedHooks();
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            EngineDiagnostics.CommandsExecuted.Add(1, new KeyValuePair<string, object?>("command", commandName), new KeyValuePair<string, object?>("result", result));
            EngineDiagnostics.CommandDuration.Record(elapsedMs, new KeyValuePair<string, object?>("command", commandName));

            // 命令级资源（DI 作用域 / 数据库连接 / 事务）随命令结束一并释放。
            // 顺序：先作用域后事务 —— 作用域里的服务（仓储 / DbContext）Dispose 时可能还要用会话。
            // 重试拦截器可能已换过事务与作用域，这里释放的是「最后一次尝试」的那一份。
            try
            {
                context.ReleaseServiceScope();
            }
            finally
            {
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
