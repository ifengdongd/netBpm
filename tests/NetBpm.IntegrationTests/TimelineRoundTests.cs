using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence;
using NetBpm.Platform.Contracts;
using NetBpm.Persistence.Platform;
using NetBpm.Rest.Platform;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 时间线「轮次」（<c>round_no</c>）与环节聚合的精确分组。
/// </summary>
/// <remarks>
/// <para>
/// 轮次解决的是一个<b>只看连续性必然算错</b>的场景：管理干预把单据跳回当前节点后，
/// 时间线上是「A 完成 → JUMPED → A 完成」，两条完成记录是<b>紧挨着</b>的。
/// 按连续段切分会把两轮并成一轮 —— 于是「第 1 轮 1 人同意 + 第 2 轮 2 人同意、还差 1 人」
/// 被算成「3 人里 3 票同意、已通过」，结论直接反了。
/// </para>
/// <para>
/// 所以轮次必须<b>写时定稿</b>：只有写入端知道「这条是重新进入」。用例同时钉住
/// 内存版与真库版两套实现的同一口径，以及老数据（轮次 0）退回连续段近似的兼容路径。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class TimelineRoundTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";

    private readonly RestEngineFactory _factory;

    public TimelineRoundTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MemoryStore_ShouldBumpRound_OnReentryOnly()
    {
        var store = new InMemoryApprovalTimelineStore();
        var instanceId = $"round-memory-{Guid.NewGuid():N}";

        // 第 1 轮：会签 2 人先后完成 —— 同一轮，不能各算一轮
        var first = await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");
        var second = await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");

        Assert.Equal(1, first.Round);
        Assert.Equal(1, second.Round);

        // 走到别的节点：顺便把该节点的第 1 轮记上
        var other = await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_review");

        Assert.Equal(1, other.Round);

        // 退回后再办一次同一个节点 → 第 2 轮
        var again = await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");

        Assert.Equal(2, again.Round);

        // 管理干预「跳回当前节点」：跳转条目自己打开了下一轮，紧随其后的完成条目沿用该轮
        var jumped = await AppendAsync(
            store,
            instanceId,
            TimelineEntryKind.InstanceJumped,
            activityId: null,
            targetNodeId: "task_apply");

        Assert.Equal(3, jumped.Round);

        var afterJump = await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");

        Assert.Equal(3, afterJump.Round);

        // 与环节无关的条目没有轮次
        var finished = await AppendAsync(store, instanceId, TimelineEntryKind.InstanceCompleted, activityId: null);

        Assert.Equal(0, finished.Round);
    }

    [Fact]
    public async Task DatabaseStore_ShouldRoundTripRounds()
    {
        SkipUnlessReady();

        var store = _factory.Services.GetRequiredService<IApprovalTimelineStore>();
        var instanceId = $"round-db-{Guid.NewGuid():N}";

        try
        {
            await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");
            await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");
            await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_review");
            await AppendAsync(store, instanceId, TimelineEntryKind.TaskCompleted, "task_apply");

            var entries = await store.ListAsync(instanceId, 0, 20, Ct);

            // 序号仍由存储层分配，且与轮次相互独立
            Assert.Equal([1L, 2L, 3L, 4L], entries.Select(entry => entry.Sequence));

            // 真库这一趟验的是「两条标量子查询 + 一条插入」拼出来的口径与内存版一致
            Assert.Equal([1, 1, 1, 2], entries.Select(entry => entry.Round));
        }
        finally
        {
            await DeleteAsync(instanceId);
        }
    }

    [Fact]
    public async Task Aggregator_ShouldSplitTwoRounds_OfTheSameNode()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        var aggregator = host.Services.GetRequiredService<INodeOutcomeAggregator>();

        // 第 1 轮：1 人同意；第 2 轮：2 人同意。两轮人数不能相加
        var entries = new[]
        {
            Completed(1, "task_apply", round: 1, taskId: "t1"),
            Completed(2, "task_apply", round: 2, taskId: "t2"),
            Completed(3, "task_apply", round: 2, taskId: "t3"),
        };

        // 定义 id 置空：用例只验分组口径，不依赖 BPMN 里的多实例配置
        var outcomes = await aggregator.ComputeAsync("inst-round", null, entries, [], Ct);

        Assert.Equal(2, outcomes.Count);

        Assert.Equal(1, outcomes[0].Round);
        Assert.Equal(1, outcomes[0].Positive);
        Assert.Equal(1, outcomes[0].Total);

        Assert.Equal(2, outcomes[1].Round);
        Assert.Equal(2, outcomes[1].Positive);
        Assert.Equal(2, outcomes[1].Total);
    }

    [Fact]
    public async Task Aggregator_ShouldFallBackToContiguousSegments_ForLegacyRows()
    {
        using var host = ReadEndpointHarness.BuildHost();
        await ReadEndpointHarness.DeployAsync(host);

        var aggregator = host.Services.GetRequiredService<INodeOutcomeAggregator>();

        // 老数据没有轮次（全 0）：退回连续段近似 —— A、A 连着就是一轮，中间隔了 B 才算两轮
        var entries = new[]
        {
            Completed(1, "task_apply", round: 0, taskId: "t1"),
            Completed(2, "task_apply", round: 0, taskId: "t2"),
            Completed(3, "task_review", round: 0, taskId: "t3"),
            Completed(4, "task_apply", round: 0, taskId: "t4"),
        };

        var outcomes = await aggregator.ComputeAsync("inst-legacy", null, entries, [], Ct);

        Assert.Equal(3, outcomes.Count);
        Assert.Equal(2, outcomes[0].Total);   // 第 1 段：2 人
        Assert.Equal(1, outcomes[1].Total);   // 中间节点
        Assert.Equal(1, outcomes[2].Total);   // 第 2 段：1 人
        Assert.All(outcomes, outcome => Assert.Equal(0, outcome.Round));
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static TimelineEntry Completed(long seq, string activityId, int round, string taskId) =>
        new(
            Sequence: seq,
            InstanceId: "inst-round",
            Kind: TimelineEntryKind.TaskCompleted,
            ActivityId: activityId,
            ActivityName: activityId,
            TaskId: taskId,
            ActorId: "lisi",
            ActorCallerId: null,
            OptionId: "1",
            Semantic: ApprovalSemantic.Approve,
            Comment: null,
            TargetNodeId: null,
            FormSnapshotTaskId: null,
            OccurredAt: new DateTimeOffset(2026, 11, 1, 12, 0, (int)seq, TimeSpan.Zero),
            Round: round);

    private static async ValueTask<TimelineEntry> AppendAsync(
        IApprovalTimelineStore store,
        string instanceId,
        TimelineEntryKind kind,
        string? activityId,
        string? targetNodeId = null) =>
        await store.AppendAsync(
            DefaultTenant,
            new TimelineEntry(
                Sequence: 0,
                InstanceId: instanceId,
                Kind: kind,
                ActivityId: activityId,
                ActivityName: activityId,
                TaskId: null,
                ActorId: "lisi",
                ActorCallerId: null,
                OptionId: null,
                Semantic: null,
                Comment: null,
                TargetNodeId: targetNodeId,
                FormSnapshotTaskId: null,
                OccurredAt: DateTimeOffset.UtcNow),
            Ct);

    private async Task DeleteAsync(string instanceId)
    {
        var connections = _factory.Services.GetRequiredService<IDbConnectionFactory>();

        await using var connection = connections.CreateConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from nb_instance_timeline where process_instance_id = @instanceId",
            new { instanceId },
            cancellationToken: Ct));
    }

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        var count = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.columns "
            + "where table_schema = database() and table_name = 'nb_instance_timeline' "
            + "and column_name = 'round_no'");

        Assert.SkipUnless(
            count >= 1,
            "库里还没有 nb_instance_timeline.round_no：请对测试库执行迁移补齐（新增列只能靠幂等脚本补）。");
    }
}
