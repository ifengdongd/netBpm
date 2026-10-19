namespace NetBpm.Engine.Command;

/// <summary>
/// 命令重试策略。默认只对 <see cref="OptimisticLockingException"/> 重试，
/// 与 Activiti 的 <c>RetryInterceptor</c>（默认 3 次）同一位置、同一触发条件。
/// <para>
/// ⚠️ <b>重试意味着监听器 / 服务任务委托可能被调用多次</b>（至少一次语义）：
/// 已被回滚的那次尝试里的外部副作用不会自动撤销。需要严格一次语义的委托
/// 必须自行做幂等（业务主键 + 唯一约束 / 去重表）。
/// </para>
/// <para>
/// 宿主可通过 <see cref="ShouldRetry"/> 收窄或扩大重试范围（例如数据库死锁）；
/// 把 <see cref="MaxAttempts"/> 设为 1 即完全关闭重试。
/// </para>
/// </summary>
public sealed class CommandRetryPolicy
{
    /// <summary>总尝试次数（首次 + 重试），默认 3。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>首次重试前的退避基数；之后按 2 的幂递增（0 表示不等待）。</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>判定某个异常是否值得重试。</summary>
    public Func<Exception, bool> ShouldRetry { get; init; } = static exception => exception is OptimisticLockingException;

    /// <summary>
    /// 每次重试前的观测回调（尝试序号, 触发异常）。
    /// 用于打点 / 测试取证：并发冲突是否真的发生、重试是否收敛。
    /// </summary>
    public Action<int, Exception>? OnRetry { get; init; }

    /// <summary>复制一份策略并替换观测回调（保留原有重试判定）。</summary>
    public CommandRetryPolicy WithOnRetry(Action<int, Exception>? onRetry) =>
        new()
        {
            MaxAttempts = MaxAttempts,
            BaseDelay = BaseDelay,
            ShouldRetry = ShouldRetry,
            OnRetry = onRetry,
        };

    /// <summary>本次重试前的退避等待（最多按 2^5 放大）。</summary>
    internal Task DelayAsync(int attempt, CancellationToken ct)
    {
        if (BaseDelay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        var factor = 1 << Math.Min(attempt - 1, 5);
        return Task.Delay(BaseDelay * factor, ct);
    }
}
