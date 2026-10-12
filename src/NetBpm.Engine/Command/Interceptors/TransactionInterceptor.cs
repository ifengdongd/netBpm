namespace NetBpm.Engine.Command.Interceptors;

/// <summary>
/// 事务拦截器：<b>一个命令 = 一个事务</b>（开发计划 §5.3 / §5.6）。
/// <para>
/// 链上位置为「Log 之内、CommandContext 之外」—— 这样议程排空（
/// <see cref="CommandContextInterceptor"/>）就落在事务内，满足风险 R4 的要求。
/// </para>
/// <para>
/// 内存宿主下 <see cref="ICommandContext.Transaction"/> 为 <see langword="null"/>，本拦截器直接透传。
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

        var transaction = context.Transaction;

        if (transaction is null)
        {
            return await nextHandler(ct).ConfigureAwait(false);
        }

        // 嵌套命令加入外层事务：不新开、不提交（Activiti 同样由最外层命令定事务边界）
        if (transaction.IsActive)
        {
            return await nextHandler(ct).ConfigureAwait(false);
        }

        transaction.Begin();

        TResult result;
        try
        {
            result = await nextHandler(ct).ConfigureAwait(false);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // CommitAsync 内部必须先 flush 一级缓存（DbSession.CommitAsync 的契约）
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }
}
