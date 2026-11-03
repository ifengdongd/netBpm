namespace NetBpm.ServerTaskWorker;

/// <summary>
/// 业务口径的失败 —— 「同一份数据再试多少次都一样」。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Retryable"/> 直接决定回执里 <c>retries</c> 怎么填：
/// <c>false</c> 时 worker 会以 <c>retries: 0</c> 回执，让任务直接进死信等人看，
/// 而不是白耗三次重试额度。
/// </para>
/// <para>
/// 与「异常」的区别：普通异常（网络抖动、反序列化失败）默认可重试；
/// 本异常表示业务规则拒绝，重试没有意义。
/// </para>
/// </remarks>
public sealed class BusinessRuleException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="message">失败摘要。</param>
    /// <param name="retryable">是否值得重试。</param>
    /// <param name="details">结构化详情（会进 <c>errorDetails</c>）。</param>
    public BusinessRuleException(string message, bool retryable = false, object? details = null)
        : base(message)
    {
        Retryable = retryable;
        Details = details;
    }

    /// <summary>是否值得重试。</summary>
    public bool Retryable { get; }

    /// <summary>结构化详情。</summary>
    public object? Details { get; }
}
