using System.Collections.Concurrent;

namespace NetBpm.Rest.RateLimiting;

/// <summary>
/// 限流配置（键都在 <c>NetBpm:RateLimit</c> 下，管理端可用 <c>NetBpm:Admin:RateLimit</c> 覆盖）。
/// </summary>
/// <remarks>
/// <para>
/// 分档的存在理由与旧配置一致：登录口是<b>口令爆破</b>的入口，凭据探针是<b>密钥爆破</b>的入口，
/// 二者的合理频次远低于正常业务调用，用同一个桶会要么太松要么误伤。
/// </para>
/// <para>
/// <see cref="ClientPerMinute"/> 是<b>兜底</b>：正常情况下一律用客户端注册表里的
/// <c>quota_per_minute</c>（一系统一配额，多系统接入时一家跑飞不该拖垮别家）。
/// </para>
/// </remarks>
public sealed class ClientRateLimitOptions
{
    /// <summary>是否启用限流。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>普通业务请求的默认每分钟上限（JWT 用户 / 无客户端配额的兜底 / 匿名 <c>/api</c>）。</summary>
    public int ApiPerMinute { get; set; } = 600;

    /// <summary>凭据探针（<c>/api/auth/me</c>）每 IP 每分钟上限。</summary>
    public int ProbePerMinute { get; set; } = 30;

    /// <summary>登录（<c>/api/auth/login</c>）每 IP 每分钟上限。</summary>
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>已注册客户端未配置配额时的兜底每分钟上限。</summary>
    public int ClientPerMinute { get; set; } = 600;

    /// <summary>
    /// 分区上限（内存保护）。超过后先清理过期窗口，仍然超过则对新分区<b>放行</b>。
    /// </summary>
    /// <remarks>
    /// 这里刻意 fail-open：分区键来自客户端 id / 租户 / IP，攻击者可以用海量 IP 把字典撑大。
    /// 若在「字典满了」时 fail-closed，等于用一个内存保护把全体正常调用方一起打挂。
    /// 放行会短暂失去限流能力，但服务仍然可用——这是更小的损失。
    /// </remarks>
    public int MaxPartitions { get; set; } = 100_000;
}

/// <summary>一次限流判定结果。</summary>
/// <param name="Allowed">是否放行。</param>
/// <param name="Limit">该分区本窗口的配额。</param>
/// <param name="Remaining">本窗口剩余可用次数（放行后）。</param>
/// <param name="ResetAfter">距离窗口重置的时长。</param>
/// <param name="Counted">
/// 本次是否真的计入配额。为 <see langword="false"/> 只在「分区表已满」时出现（fail-open）。
/// </param>
public readonly record struct RateLimitDecision(
    bool Allowed,
    int Limit,
    int Remaining,
    TimeSpan ResetAfter,
    bool Counted)
{
    /// <summary>放行且不限流（不在限流范围内的路径）。</summary>
    public static RateLimitDecision Unlimited { get; } = new(true, 0, 0, TimeSpan.Zero, false);

    /// <summary><c>Retry-After</c> 的秒数（向上取整，至少 1 秒：返回 0 会让客户端立刻重试）。</summary>
    public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(ResetAfter.TotalSeconds));
}

/// <summary>
/// 按分区键计数的<b>固定窗口</b>限流器（进程内）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用框架自带的 <c>RateLimiter</c>：它只提供 <c>RetryAfter</c> 元数据，
/// 拿不到 <c>Remaining</c>，而「还剩多少次」恰恰是调用方最需要的（避免盲重试把配额打光）。
/// 另外这里的配额是<b>每分区不同</b>的（取客户端注册表里的值），需要一个能按 key 传配额的实现。
/// </para>
/// <para>
/// 固定窗口的取舍：窗口边界处允许 2 倍突发（59 秒用满 + 01 秒再用满）。
/// 对本场景可接受——目的是「防脚本失控 / 防爆破」，不是精确计费；
/// 换成滑动窗口会让实现复杂一个量级，而计费口径本来就不该看这里。
/// </para>
/// <para>
/// ⚠️ 进程内实现：多实例部署时每个实例各算一份，总配额 = 实例数 × 配置值。
/// 需要全局配额时应把本类换成分区在 Redis 上的实现（接口不变）。
/// </para>
/// </remarks>
public sealed class FixedWindowRateLimiter
{
    private static readonly TimeSpan WindowSize = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly int _maxPartitions;

    private long _acquires;

    /// <summary>构造。</summary>
    /// <param name="maxPartitions">分区上限。</param>
    /// <param name="clock">时钟（测试可替换）。</param>
    public FixedWindowRateLimiter(int maxPartitions, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartitions);

        _maxPartitions = maxPartitions;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>当前分区数（诊断用）。</summary>
    public int PartitionCount => _windows.Count;

    /// <summary>
    /// 尝试占用一次配额。
    /// </summary>
    /// <param name="partitionKey">分区键（调用方 id / 用户 / IP）。</param>
    /// <param name="permitLimit">该分区每分钟配额；<c>&lt;= 0</c> 表示不限流。</param>
    public RateLimitDecision Acquire(string partitionKey, int permitLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);

        if (permitLimit <= 0)
        {
            return RateLimitDecision.Unlimited;
        }

        var now = _clock.GetUtcNow();
        var windowStart = now.UtcTicks - (now.UtcTicks % WindowSize.Ticks);
        var resetAfter = TimeSpan.FromTicks(windowStart + WindowSize.Ticks - now.UtcTicks);

        if (!_windows.TryGetValue(partitionKey, out var window))
        {
            if (!TryAddWindow(partitionKey, out window))
            {
                // 分区表已满：放行但计数（见 MaxPartitions 的说明）
                return new RateLimitDecision(true, permitLimit, permitLimit, resetAfter, Counted: false);
            }
        }

        lock (window)
        {
            if (window.StartTicks != windowStart)
            {
                window.StartTicks = windowStart;
                window.Count = 0;
            }

            if (window.Count >= permitLimit)
            {
                return new RateLimitDecision(false, permitLimit, 0, resetAfter, Counted: true);
            }

            window.Count++;

            return new RateLimitDecision(true, permitLimit, permitLimit - window.Count, resetAfter, Counted: true);
        }
    }

    /// <summary>清理已过期的窗口（周期性调用或测试用）。</summary>
    /// <returns>清理掉的条目数。</returns>
    public int Cleanup()
    {
        var now = _clock.GetUtcNow();
        var windowStart = now.UtcTicks - (now.UtcTicks % WindowSize.Ticks);
        var removed = 0;

        foreach (var (key, window) in _windows)
        {
            long start;

            lock (window)
            {
                start = window.StartTicks;
            }

            // 上一窗口（甚至更早）的条目已经没有意义：下一窗口会重置计数
            if (start < windowStart && _windows.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryAddWindow(string partitionKey, out Bucket window)
    {
        // 每 1024 次新分区做一次清理：既不需要后台定时器，也不会让清理本身成为热路径
        if (Interlocked.Increment(ref _acquires) % 1024 == 0)
        {
            Cleanup();
        }

        if (_windows.Count >= _maxPartitions)
        {
            Cleanup();
        }

        if (_windows.Count >= _maxPartitions)
        {
            window = null!;
            return false;
        }

        window = _windows.GetOrAdd(partitionKey, _ => new Bucket());
        return true;
    }

    /// <summary>一个分区的窗口状态（每个 key 一把锁，锁粒度 = 一个分区）。</summary>
    private sealed class Bucket
    {
        public long StartTicks;

        public int Count;
    }
}
