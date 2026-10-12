namespace NetBpm.Engine.Command;

/// <summary>拦截器链中的下一环。</summary>
/// <typeparam name="TResult">命令返回值类型。</typeparam>
public delegate ValueTask<TResult> CommandHandler<TResult>(CancellationToken ct);

/// <summary>
/// 命令拦截器。对应 Activiti 的 <c>CommandInterceptor</c>。
/// <para>本方案的链（§5.6）：Log → Transaction → CommandContext。</para>
/// </summary>
public interface ICommandInterceptor
{
    ValueTask<TResult> InterceptAsync<TResult>(
        ICommand<TResult> command,
        ICommandContext context,
        CommandHandler<TResult> nextHandler,
        CancellationToken ct = default);
}

/// <summary>命令执行器。对应 Activiti 的 <c>CommandExecutor</c>。</summary>
public interface ICommandExecutor
{
    ValueTask<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);
}
