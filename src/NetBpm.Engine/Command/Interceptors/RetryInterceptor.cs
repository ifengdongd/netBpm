using Microsoft.Extensions.Logging;
using NetBpm.Engine.Diagnostics;

namespace NetBpm.Engine.Command.Interceptors;

/// <summary>
/// 重试拦截器：乐观锁冲突时丢弃当前事务（含一级缓存）与议程，重新执行整条命令。
/// 对应 Activiti 的 <c>RetryInterceptor</c>。
/// <para>
/// 链上位置：<b>Log 之内、Transaction 之外</b>。必须在事务之外，因为一次重试 =
/// 一个全新的 <c>DbSession</c>：旧会话里被回滚的脏实体与已自增的 <c>REV_</c>
/// 绝不能带入下一次尝试（否则必然二次冲突或写坏行）。
/// </para>
/// <para>
/// 嵌套命令<b>不重试</b>：它加入的是外层事务，重试内层命令会把外层已完成的写入重放一遍。
/// 是否嵌套由 <see cref="CommandContext.IsNested"/> 判定。
/// </para>
/// </summary>
public sealed class RetryInterceptor : ICommandInterceptor
{
    private readonly CommandRetryPolicy _policy;
    private readonly ILogger<RetryInterceptor>? _logger;

    public RetryInterceptor(CommandRetryPolicy? policy = null, ILogger<RetryInterceptor>? logger = null)
    {
        _policy = policy ?? new CommandRetryPolicy();
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<TResult> InterceptAsync<TResult>(
        ICommand<TResult> command,
        ICommandContext context,
        CommandHandler<TResult> nextHandler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextHandler);

        // 自定义上下文（非默认实现）无法重建事务，退回单次执行。
        if (_policy.MaxAttempts <= 1
            || context is not CommandContext commandContext
            || commandContext.IsNested)
        {
            return await nextHandler(ct).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await nextHandler(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < _policy.MaxAttempts
                                              && _policy.ShouldRetry(exception)
                                              && !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(
                    exception,
                    "命令 {Command} 第 {Attempt}/{MaxAttempts} 次尝试失败（{ExceptionType}），重置上下文后重试。",
                    command.GetType().Name,
                    attempt,
                    _policy.MaxAttempts,
                    exception.GetType().Name);

                EngineDiagnostics.OptimisticLockRetries.Add(
                    1,
                    new KeyValuePair<string, object?>("command", command.GetType().Name));

                _policy.OnRetry?.Invoke(attempt, exception);
                commandContext.ResetForRetry();

                await _policy.DelayAsync(attempt, ct).ConfigureAwait(false);
            }
        }
    }
}
