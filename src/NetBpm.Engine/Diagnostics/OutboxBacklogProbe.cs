namespace NetBpm.Engine.Diagnostics;

/// <summary>
/// outbox 积压的「最近一次采样值」——指标回调与数据库之间的那层缓存。
/// <para>
/// 积压是<b>状态量</b>（此刻还有多少条没发出去），只能用 Gauge 表达；
/// 而 OTel 的观测式 Gauge 是<b>同步回调</b>：Prometheus 每 15~30 秒来抓一次，
/// 回调里若直接查库，等于把查询频率交给抓取方，还会在抓取线程上同步阻塞。
/// 所以后台采样器按自己的节奏查库、把结果写到这里，回调只读内存快照。
/// </para>
/// <para>
/// ⚠️ 缓存天然带一个副作用：<b>进程死了、数字还留在那儿</b>。看板上「积压 0 条」
/// 可能只是「没人再更新它了」，而这个恰恰是「一切正常」的样子 ——
/// 所以这里同时记采样时刻，由 <see cref="IsStale"/> 和
/// <c>netbpm.outbox.backlog_stale</c> 明确区分「真的没有积压」与「不知道有没有」。
/// </para>
/// </summary>
public static class OutboxBacklogProbe
{
    /// <summary>
    /// 判定过期的最小窗口：采样间隔就算配到 1 秒，也至少容忍这么久。
    /// <para>采样本身要查两次库，偶发慢一两轮很正常，别把抖动当成故障。</para>
    /// </summary>
    public static readonly TimeSpan MinimumStaleThreshold = TimeSpan.FromSeconds(30);

    private static int _pending;
    private static int _deadLetter;
    private static double _oldestPendingAgeSeconds;
    private static long _intervalTicks;
    private static long _sampledAtTicks;

    /// <summary>最近一次采样的待发布条数（未发布且未死信）。</summary>
    public static int Pending => Volatile.Read(ref _pending);

    /// <summary>最近一次采样的死信条数（未发布且已判死）。</summary>
    public static int DeadLetter => Volatile.Read(ref _deadLetter);

    /// <summary>
    /// 最近一次采样时「最老一条待发布事件」的年龄（秒）；没有积压时为 0。
    /// <para>
    /// 比 <see cref="Pending"/> 更值得盯：积压 200 条可能是刚灌进来的瞬时洪峰，
    /// 积压 3 条但最老的躺了 40 分钟，说明发布链路已经断了。
    /// </para>
    /// </summary>
    public static double OldestPendingAgeSeconds => Volatile.Read(ref _oldestPendingAgeSeconds);

    /// <summary>采样间隔（由采样器写入；未采样时为 <see cref="TimeSpan.Zero"/>）。</summary>
    public static TimeSpan SampleInterval => TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));

    /// <summary>最近一次成功采样的时刻（UTC）；<see langword="null"/> = 从未采样过。</summary>
    public static DateTime? LastSampledAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _sampledAtTicks);

            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// 采样值是否已不可信（从未采样，或距上次成功采样已超过容忍窗口）。
    /// </summary>
    public static bool IsStale
    {
        get
        {
            var sampledAt = LastSampledAtUtc;

            if (sampledAt is null)
            {
                return true;
            }

            return DateTime.UtcNow - sampledAt.Value > StaleThreshold;
        }
    }

    /// <summary>
    /// 容忍窗口 = 采样间隔 × 3，但不小于 <see cref="MinimumStaleThreshold"/>。
    /// </summary>
    public static TimeSpan StaleThreshold
    {
        get
        {
            var threshold = TimeSpan.FromTicks(SampleInterval.Ticks * 3);

            return threshold < MinimumStaleThreshold ? MinimumStaleThreshold : threshold;
        }
    }

    /// <summary>写入一次采样结果。</summary>
    /// <param name="pending">待发布条数。</param>
    /// <param name="deadLetter">死信条数。</param>
    /// <param name="oldestPendingAgeSeconds">最老一条待发布事件的年龄（秒）。</param>
    /// <param name="interval">本次使用的采样间隔（决定过期窗口）。</param>
    public static void Record(int pending, int deadLetter, double oldestPendingAgeSeconds, TimeSpan interval)
    {
        Volatile.Write(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
        Volatile.Write(ref _deadLetter, deadLetter);
        Volatile.Write(ref _pending, pending);
        Interlocked.Exchange(ref _intervalTicks, interval <= TimeSpan.Zero ? TimeSpan.Zero.Ticks : interval.Ticks);
        Interlocked.Exchange(ref _sampledAtTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// 标记「采样已停止，旧数字别再信」。
    /// <para>
    /// 采样器停机时调用：此时数值留在最后一次的结果上，看板会一直显示那个数字，
    /// 而 <see cref="IsStale"/> 会把这个状态显式暴露出来。
    /// </para>
    /// </summary>
    public static void MarkStale() => Interlocked.Exchange(ref _sampledAtTicks, 0);

    /// <summary>清零（测试用）。</summary>
    public static void Reset()
    {
        MarkStale();
        Interlocked.Exchange(ref _intervalTicks, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _deadLetter, 0);
        Volatile.Write(ref _oldestPendingAgeSeconds, 0);
    }
}
