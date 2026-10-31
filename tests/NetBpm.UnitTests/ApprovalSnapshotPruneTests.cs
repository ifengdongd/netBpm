using NetBpm.Persistence.Platform;

namespace NetBpm.UnitTests;

/// <summary>
/// 表单快照保留期清理（<c>nb_approval_snapshot</c> 的内存实现口径）。
/// </summary>
/// <remarks>
/// <para>
/// 这里只用内存实现锁住「<b>按定稿时间切</b>」这个语义：早于切点的删、晚于的留、返回值是删除条数。
/// SQL 版是同一条 <c>where captured_at &lt; @before</c>，跑 MySQL 的端到端版本在
/// <c>ApprovalSnapshotTests.Snapshot_ShouldBePrunedByCapturedAt</c>。
/// </para>
/// <para>
/// 为什么这条边界值得单独守：保留期<b>与历史归档共用同一个切点</b>，
/// 一旦哪天有人改成「按租户 / 按实例」删，历史还在而快照先没了，
/// 历史页会退化成「有结论、没有内容」——那比没有历史页更误导人。
/// </para>
/// </remarks>
public sealed class ApprovalSnapshotPruneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>过期的删、未过期的留，返回删除条数。</summary>
    [Fact]
    public async Task Prune_ShouldRemoveOnlyExpired()
    {
        var store = new InMemoryApprovalSnapshotStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(Record("old-1", now.AddDays(-400)), Ct);
        await store.AppendAsync(Record("old-2", now.AddDays(-30)), Ct);
        await store.AppendAsync(Record("new-1", now.AddDays(-1)), Ct);

        var removed = await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct);

        Assert.Equal(2, removed);
        Assert.Null(await store.FindAsync("old-1", Ct));
        Assert.Null(await store.FindAsync("old-2", Ct));
        Assert.NotNull(await store.FindAsync("new-1", Ct));
    }

    /// <summary>没有过期记录时返回 0：清理任务据此决定要不要打日志，别把 0 当成失败。</summary>
    [Fact]
    public async Task Prune_ShouldReturnZero_WhenNothingExpired()
    {
        var store = new InMemoryApprovalSnapshotStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(Record("new-1", now.AddDays(-1)), Ct);

        Assert.Equal(0, await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct));
        Assert.NotNull(await store.FindAsync("new-1", Ct));
    }

    /// <summary>切点取「早于」而不是「早于等于」：正好落在切点上的那份要留着。</summary>
    [Fact]
    public async Task Prune_ShouldKeepRecord_ExactlyAtCutoff()
    {
        var store = new InMemoryApprovalSnapshotStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(Record("edge", now.AddDays(-7)), Ct);

        Assert.Equal(0, await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct));
        Assert.NotNull(await store.FindAsync("edge", Ct));
    }

    /// <summary>清理是可重复的：第二次跑不报错，也不再删出东西（后台任务每 6h 都会再跑一次）。</summary>
    [Fact]
    public async Task Prune_ShouldBeIdempotent()
    {
        var store = new InMemoryApprovalSnapshotStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(Record("old-1", now.AddDays(-30)), Ct);

        Assert.Equal(1, await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct));
        Assert.Equal(0, await store.PruneAsync(now.AddDays(-7).UtcDateTime, Ct));
    }

    private static ApprovalSnapshotRecord Record(string taskId, DateTimeOffset capturedAt) =>
        new(
            TaskId: taskId,
            InstanceId: "instance-1",
            TenantId: "default",
            ProcessKey: "leave_process",
            ProcessDefinitionId: null,
            DefinitionVersion: 1,
            ActivityId: "task_owner",
            ActivityName: "主管审批",
            ActorId: "manager",
            ActorCallerId: null,
            OptionId: "1",
            Fields: [],
            Values: new Dictionary<string, string?>(StringComparer.Ordinal),
            Attachments: null,
            CapturedAt: capturedAt);
}
