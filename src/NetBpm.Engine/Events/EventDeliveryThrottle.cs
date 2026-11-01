using System.Collections.Concurrent;

namespace NetBpm.Engine.Events;

/// <summary>
/// 单订阅投递节流：订阅方说「我一分钟最多收 N 条」，这里替它数着。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>IRateLimiter</c>（<c>NetBpm.Rest</c> 的 API 配额）<b>不是</b>同一件事，也不该合成一个：
/// 那个是<b>对自己</b>的保护（不让某个调用方打爆我们），这个是<b>对订阅方</b>的礼让
/// （不让我们按 outbox 的节奏把对方打爆）。两者的失败语义也不同——
/// 配额超限是拒绝请求，节流超限是<b>推迟投递</b>。
/// </para>
/// <para>
/// 🔑 <b>被挡下不消耗尝试次数</b>：调用方拿到 <see langword="false"/> 之后要走
/// <see cref="IEventDeliveryStore.MarkDeferredAsync"/>，而不是 <c>MarkFailureAsync</c>。
/// 否则订阅方只是回得慢、被限速几次，就会被推进死信。
/// </para>
/// </remarks>
public interface IEventDeliveryThrottle
{
    /// <summary>
    /// 尝试为本次投递占用一个配额。
    /// </summary>
    /// <param name="subscriptionId">订阅 id（分区键）。</param>
    /// <param name="maxPerMinute">订阅配置的每分钟上限；<c>&lt;= 0</c> 表示不限。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns><see langword="true"/> = 放行；<see langword="false"/> = 本窗口已超限，应推迟。</returns>
    ValueTask<bool> TryAcquireAsync(string subscriptionId, int maxPerMinute, CancellationToken ct = default);
}

/// <summary>
/// 进程内固定窗口计数器（默认实现）。
/// </summary>
/// <remarks>
/// <para>
/// 窗口按 <b>UTC 分钟</b>对齐（<c>unix 秒 / 60</c>），不是滑动窗口：滑动窗口要存每次投递的时间戳，
/// 一个订阅上千条事件就是上千个时间戳常驻内存，而这里要的只是「别一秒钟灌对方一百条」，
/// 固定窗口的边界突刺（跨窗口瞬间放行两倍）完全可接受。
/// </para>
/// <para>
/// ⚠️ <b>多副本时实际总配额 = 副本数 × <c>max_per_minute</c></b>：每个进程各数各的。
/// 这对「礼让订阅方」这个目的仍然有效（每个副本自己不会超频），但它<b>不是</b>精确的全局配额——
/// 想要精确全局配额得让计数器跨进程共享（Redis），而这一层没有引入那个依赖。
/// 真要严格限流的场景请把订阅端的限速当成近似值来用。
/// </para>
/// </remarks>
public sealed class InMemoryEventDeliveryThrottle : IEventDeliveryThrottle
{
    /// <summary>超过这个订阅数就顺手清理过期窗口（防止订阅被反复创建/删除后字典只增不减）。</summary>
    private const int PruneThreshold = 1024;

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>构造。</summary>
    /// <param name="clock">时钟（测试可注入，默认 <see cref="TimeProvider.System"/>）。</param>
    public InMemoryEventDeliveryThrottle(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryAcquireAsync(string subscriptionId, int maxPerMinute, CancellationToken ct = default)
    {
        if (maxPerMinute <= 0)
        {
            return ValueTask.FromResult(true);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var window = CurrentWindow();

        // AddOrUpdate 的更新委托可能被并发调用多次，但 TryUpdate 会因比较值不匹配而重算，
        // 因此计数不会被丢掉；先把计数加上再判断，被挡下的那一批也不会在窗口内反复放行/拒绝抖动。
        var current = _windows.AddOrUpdate(
            subscriptionId,
            _ => new Window(window, 1),
            (_, existing) => existing.WindowId == window
                ? new Window(window, existing.Count + 1)
                : new Window(window, 1));

        if (_windows.Count > PruneThreshold)
        {
            Prune(window);
        }

        return ValueTask.FromResult(current.Count <= maxPerMinute);
    }

    /// <summary>当前窗口号（UTC 分钟）。</summary>
    private long CurrentWindow() => _clock.GetUtcNow().ToUnixTimeSeconds() / 60;

    /// <summary>清掉上一窗口之前的残留（它们下次被用到时本来也会重置，清掉只是不让字典无限长）。</summary>
    private void Prune(long window)
    {
        foreach (var pair in _windows)
        {
            if (pair.Value.WindowId < window)
            {
                _windows.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Window(long WindowId, int Count);
}
