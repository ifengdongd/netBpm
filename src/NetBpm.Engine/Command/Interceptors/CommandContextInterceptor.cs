namespace NetBpm.Engine.Command.Interceptors;

/// <summary>
/// 命令上下文拦截器：命令主体执行完毕后，<b>在同一个命令（= 事务）内</b>把议程队列排空。
/// <para>
/// 🔑 这是 §5.3 / 风险 R4 的硬约束落点：<b>agenda 的排空不能放到事务外</b>，
/// 否则并行 Join 的中间状态会泄漏到事务外，且「单命令单事务」语义被破坏。
/// </para>
/// </summary>
public sealed class CommandContextInterceptor : ICommandInterceptor
{
    /// <inheritdoc />
    public async ValueTask<TResult> InterceptAsync<TResult>(
        ICommand<TResult> command,
        ICommandContext context,
        CommandHandler<TResult> nextHandler,
        CancellationToken ct = default)
    {
        var result = await nextHandler(ct).ConfigureAwait(false);

        // 排空议程：把命令期间"规划"的操作全部执行掉（仍在同一事务内）。
        // 用上下文自己的议程 —— 并发命令各排各的队列，互不串扰。
        await context.Agenda.DrainAsync(ct).ConfigureAwait(false);

        return result;
    }
}
