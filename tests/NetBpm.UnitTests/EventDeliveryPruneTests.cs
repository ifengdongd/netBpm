using NetBpm.Engine.Events;

namespace NetBpm.UnitTests;

/// <summary>
/// 事件投递保留期清理（<c>nb_event_delivery</c> 的内存实现口径）。
/// </summary>
/// <remarks>
/// <para>
/// 这里锁住两条语义：<b>只删终结态</b>（成功按 <c>delivered_at</c>、死信按 <c>updated_at</c>），
/// 以及 <c>PENDING</c> / <c>FAILED</c> <b>无论多旧都不动</b> —— 它们还在重试链路上，
/// 删掉就是把「这条还没投成」伪装成「从来没有这条」。跑 MySQL 的端到端版本在
/// <c>EventSubscriptionDatabaseTests.Delivery_Prune_ShouldRemoveOnlySettledRows</c>。
/// </para>
/// <para>
/// 为什么这条边界值得单独守：保留期默认关闭（<c>NetBpm:Events:DeliveryRetentionDays = 0</c>），
/// 一旦有人打开，删错状态就是静默丢数据，日志里只有一句「已清理 N 条」。
/// </para>
/// </remarks>
public sealed class EventDeliveryPruneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>过期的终结态删掉，未过期的留着，返回值是删除条数。</summary>
    [Fact]
    public async Task Prune_ShouldRemoveOnlySettledRows_OlderThanCutoff()
    {
        var store = new InMemoryEventDeliveryStore();
        var now = DateTime.UtcNow;
        var old = now.AddDays(-30);
        var fresh = now.AddDays(-1);

        await Delivered(store, "evt-old", old);
        await Delivered(store, "evt-new", fresh);
        await DeadLettered(store, "evt-dead-old", old);
        await DeadLettered(store, "evt-dead-new", fresh);

        var removed = await store.PruneAsync(now.AddDays(-7), Ct);

        Assert.Equal(2, removed);
        Assert.Null(await store.FindAsync("evt-old", "sub-1", Ct));
        Assert.Null(await store.FindAsync("evt-dead-old", "sub-1", Ct));
        Assert.NotNull(await store.FindAsync("evt-new", "sub-1", Ct));
        Assert.NotNull(await store.FindAsync("evt-dead-new", "sub-1", Ct));
    }

    /// <summary>还在重试链路上的两种状态：时间再久也不删。</summary>
    [Fact]
    public async Task Prune_ShouldNeverRemoveLiveRows()
    {
        var store = new InMemoryEventDeliveryStore();
        var now = DateTime.UtcNow;
        var old = now.AddDays(-30);

        // 从没试过的一条
        var pending = await store.GetOrCreateAsync("evt-pending", "sub-1", "default", Ct);
        pending.CreatedAt = old;
        pending.UpdatedAt = old;

        // 失败但还在重试额度内
        await store.GetOrCreateAsync("evt-failed", "sub-1", "default", Ct);
        await store.MarkFailureAsync("evt-failed", "sub-1", "超时", old, old.AddSeconds(5), 5, Ct);

        Assert.Equal(0, await store.PruneAsync(now.AddDays(-7), Ct));
        Assert.Equal(EventDeliveryStatus.Pending, (await store.FindAsync("evt-pending", "sub-1", Ct))!.Status);
        Assert.Equal(EventDeliveryStatus.Failed, (await store.FindAsync("evt-failed", "sub-1", Ct))!.Status);
    }

    /// <summary>正好落在切点上的留着（「早于」而非「早于等于」）；第二次跑返回 0。</summary>
    [Fact]
    public async Task Prune_ShouldKeepRecordAtCutoff_AndBeRepeatable()
    {
        var store = new InMemoryEventDeliveryStore();
        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-7).Date;

        await Delivered(store, "evt-edge", cutoff);
        await Delivered(store, "evt-old", now.AddDays(-30));

        Assert.Equal(1, await store.PruneAsync(cutoff, Ct));
        Assert.Equal(0, await store.PruneAsync(cutoff, Ct));
        Assert.NotNull(await store.FindAsync("evt-edge", "sub-1", Ct));
    }

    private static async Task Delivered(InMemoryEventDeliveryStore store, string eventId, DateTime deliveredAtUtc)
    {
        await store.GetOrCreateAsync(eventId, "sub-1", "default", Ct);
        await store.MarkDeliveredAsync(eventId, "sub-1", deliveredAtUtc, Ct);
    }

    private static async Task DeadLettered(InMemoryEventDeliveryStore store, string eventId, DateTime failedAtUtc)
    {
        await store.GetOrCreateAsync(eventId, "sub-1", "default", Ct);

        // 上限 1：一次失败即进死信，updated_at 就是这里给的时间
        await store.MarkFailureAsync(eventId, "sub-1", "500", failedAtUtc, null, 1, Ct);
    }
}
