namespace NetBpm.Engine.Command.Interceptors;

/// <summary>
/// 事务拦截器：<b>一个命令 = 一个事务</b>（开发计划 §5.3 / §5.6）。
/// <para>
/// 链上位置为「Log 之内、CommandContext 之外」—— 这样议程排空（
/// <see cref="CommandContextInterceptor"/>）就落在事务内，满足风险 R4 的要求。
/// </para>
/// <para>
/// 内存宿主下 <see cref="ICommandContext.Transaction"/> 为 <see langword="null"/>，
/// 本拦截器直接透传，并在命令返回后触发提交后钩子。
/// </para>
/// </summary>
public sealed class TransactionInterceptor : ICommandInterceptor
{
    /// <inheritdoc />
    public async ValueTask<TResult> InterceptAsync<TResult>(
        ICommand<TResult> command,
        ICommandContext context,
        CommandHandler<TResult> nextHandler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var typed = context as CommandContext
            ?? throw new ArgumentException(
                $"上下文类型 '{context.GetType().Name}' 不是 {nameof(CommandContext)}，" +
                "无法触发提交后钩子。",
                nameof(context));

        var transaction = typed.Transaction;

        if (transaction is null)
        {
            // 内存宿主：没有事务边界，命令返回即视为「已提交」
            var memoryResult = await nextHandler(ct).ConfigureAwait(false);

            await typed.RunCommittedHooksAsync(typed.Services.Logger, ct).ConfigureAwait(false);

            return memoryResult;
        }

        // 嵌套命令与重试共用同一上下文：已有活动事务时不新开、不提交
        // （Activiti 同样由最外层命令定事务边界）。
        // 钩子也交给最外层统一触发，避免嵌套命令提前清空队列提示。
        if (transaction.IsActive)
        {
            return await nextHandler(ct).ConfigureAwait(false);
        }

        TResult result;

        transaction.Begin();

        try
        {
            result = await nextHandler(ct).ConfigureAwait(false);

            // CommitAsync 内部必须先 flush 一级缓存（DbSession.CommitAsync 的契约）。
            // flush 时同样可能撞乐观锁，因此提交也必须在 try 内 —— 失败要立刻回滚，
            // 不能让半提交状态带着未释放的事务传给重试拦截器。
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // 提交成功之后才触发：队列提示 / 通知等副作用不应与业务事务同生共死，
        // 放在事务外还能避免它们拖长数据库事务持锁时间。
        await typed.RunCommittedHooksAsync(typed.Services.Logger, ct).ConfigureAwait(false);

        return result;
    }
}
