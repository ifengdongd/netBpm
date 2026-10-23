namespace NetBpm.Engine;

/// <summary>
/// 可替换的时钟。引擎内的「现在」必须走这里，而不是直接读 <see cref="DateTime.UtcNow"/>。
/// </summary>
/// <remarks>
/// <para>
/// 直接读静态属性会让三类场景无法测试：定时器到期、作业退避、任务领取时间。
/// 注入之后，测试可以给出一个「停在某一刻」的时钟，把时间推进变成确定性的断言。
/// </para>
/// <para>
/// ⚠️ 只用于<b>引擎决策</b>（到期判断、超时、创建时间）。持久化层写库时的时间戳仍由
/// 各仓储自己取时间 —— 那是数据契约，不属于可替换的业务时钟。
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>当前 UTC 时间。</summary>
    DateTime UtcNow { get; }
}

/// <summary>默认时钟：直接返回系统 UTC 时间。</summary>
public sealed class SystemClock : IClock
{
    /// <summary>进程级单例（无状态）。</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
