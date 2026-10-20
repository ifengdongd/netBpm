using NetBpm.Persistence.Admin;

namespace NetBpm.UnitTests;

/// <summary>
/// 审计保留策略的内存实现：按时间 / 租户裁剪，且不误删边界内的记录。
/// <para>数据库实现的同类语义由集成测试（<c>AuditPurge_*</c>）覆盖。</para>
/// </summary>
public sealed class AdminAuditStoreTests
{
    [Fact]
    public async Task Purge_ShouldRemoveEntriesStrictlyOlderThanCutoff()
    {
        var store = new InMemoryAdminAuditStore();
        var now = DateTime.UtcNow;

        await RecordAsync(store, "old", now.AddDays(-10));
        await RecordAsync(store, "edge", now.AddDays(-7));
        await RecordAsync(store, "fresh", now.AddDays(-1));

        var removed = await store.PurgeAsync(null, now.AddDays(-7), Ct);

        Assert.Equal(1, removed);

        var page = await store.QueryAsync(new AdminAuditQuery(MaxResults: 10), Ct);

        Assert.Equal(2, page.Total);
        Assert.DoesNotContain(page.Items, item => item.Actor == "old");
    }

    [Fact]
    public async Task Purge_ShouldRespectTenant()
    {
        var store = new InMemoryAdminAuditStore();
        var now = DateTime.UtcNow;

        await RecordAsync(store, "a-old", now.AddDays(-3), tenantId: "tenant-a");
        await RecordAsync(store, "b-old", now.AddDays(-3), tenantId: "tenant-b");

        var removed = await store.PurgeAsync("tenant-a", now, Ct);

        Assert.Equal(1, removed);

        var page = await store.QueryAsync(new AdminAuditQuery(MaxResults: 10), Ct);
        var remaining = Assert.Single(page.Items);

        Assert.Equal("tenant-b", remaining.TenantId);
    }

    [Fact]
    public async Task Purge_ShouldKeepCleanupTrail_WhenCutoffIsNow()
    {
        var store = new InMemoryAdminAuditStore();
        var now = DateTime.UtcNow;

        await RecordAsync(store, "before", now.AddMilliseconds(-2));

        var removed = await store.PurgeAsync(null, now, Ct);

        Assert.Equal(1, removed);

        // 清理动作自身的记录晚于 cutoff：不会被同一次清理删掉
        await RecordAsync(store, "purger", DateTime.UtcNow, action: AdminAuditActions.AuditPurge);

        var page = await store.QueryAsync(new AdminAuditQuery(MaxResults: 10), Ct);
        var trail = Assert.Single(page.Items);

        Assert.Equal(AdminAuditActions.AuditPurge, trail.Action);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask RecordAsync(
        InMemoryAdminAuditStore store,
        string actor,
        DateTime createdAt,
        string? tenantId = null,
        string action = "task.reassign") =>
        store.RecordAsync(
            new AdminAuditEntry(
                Guid.NewGuid().ToString("N"),
                tenantId,
                actor,
                "jwt",
                action,
                "task",
                "target",
                null,
                null,
                true,
                null,
                createdAt),
            Ct);
}
