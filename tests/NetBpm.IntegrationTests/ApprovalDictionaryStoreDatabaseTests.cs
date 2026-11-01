using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Persistence.Platform;
using NetBpm.Platform.Contracts;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 审批语义字典的写路径对着<b>真库</b>的验证（<c>nb_approval_dictionary</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一条：这里的存储编码有两处只有真库能发现。
/// ① 「流程级默认」在库里是空串而不是 NULL——主键含 <c>node_id</c>，而两个方言的主键列都不允许 NULL，
/// 写 NULL 在 MySQL 上直接报 1048，内存版却一切正常；
/// ② 覆盖写走的是「先读 created_at，再 update / insert」，写错成 update-first 就会把
/// <c>created_at</c> 重置成当前时间，「这行是什么时候配的」从此查不出来。
/// </para>
/// <para>
/// 平台表不被 <c>EngineTableCleaner</c> 清理，所以每次用独立租户，并在 <c>finally</c> 里删干净。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class ApprovalDictionaryStoreDatabaseTests : IClassFixture<RestEngineFactory>
{
    private const string ProcessKey = "dictionary_store_demo";

    private readonly RestEngineFactory _factory;

    public ApprovalDictionaryStoreDatabaseTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upsert_ShouldRoundTripProcessLevelRow_AndPreserveCreatedAt()
    {
        SkipUnlessReady();

        var store = _factory.Services.GetRequiredService<IApprovalDictionaryStore>();
        var tenant = NewTenant();

        try
        {
            // 流程级（nodeId = null）：库里落空串，读回来还是 null
            var created = await store.UpsertAsync(
                Entry(tenant, nodeId: null, "1", ApprovalSemantic.Approve, "同意"),
                Ct);

            var rows = await store.ListAsync(tenant, ProcessKey, Ct);

            var row = Assert.Single(rows);

            Assert.Null(row.NodeId);
            Assert.Equal("1", row.OptionId);
            Assert.Equal(ApprovalSemantic.Approve, row.Semantic);
            Assert.Equal(ApprovalOutcomeClass.Positive, row.OutcomeClass);
            Assert.Equal("同意", row.Label);

            // 覆盖同一行：不新增、不动 created_at
            var updated = await store.UpsertAsync(
                Entry(tenant, nodeId: null, "1", ApprovalSemantic.Custom, "转财务复核"),
                Ct);

            rows = await store.ListAsync(tenant, ProcessKey, Ct);

            Assert.Single(rows);
            Assert.Equal(ApprovalSemantic.Custom, rows[0].Semantic);
            Assert.Equal(ApprovalOutcomeClass.Neutral, rows[0].OutcomeClass);
            Assert.Equal(created.CreatedAt, updated.CreatedAt);
            Assert.True(
                updated.UpdatedAt >= created.UpdatedAt,
                "覆盖写的 updated_at 不能早于首次写入。");

            // 节点级与流程级是两行（同一个 optionId 可以有两个答案，按节点覆盖）
            await store.UpsertAsync(
                Entry(tenant, nodeId: "task_approve", "1", ApprovalSemantic.Return, "退回"),
                Ct);

            rows = await store.ListAsync(tenant, ProcessKey, Ct);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, item => item.NodeId is null);
            Assert.Contains(rows, item => string.Equals(item.NodeId, "task_approve", StringComparison.Ordinal));

            // 删除节点级那行，流程级仍在
            Assert.True(await store.DeleteAsync(tenant, ProcessKey, "task_approve", "1", Ct));
            Assert.False(await store.DeleteAsync(tenant, ProcessKey, "task_approve", "1", Ct));

            rows = await store.ListAsync(tenant, ProcessKey, Ct);

            Assert.Single(rows);
            Assert.Null(rows[0].NodeId);

            Assert.True(await store.DeleteAsync(tenant, ProcessKey, null, "1", Ct));
            Assert.Empty(await store.ListAsync(tenant, ProcessKey, Ct));
        }
        finally
        {
            await store.DeleteAsync(tenant, ProcessKey, null, "1", Ct);
            await store.DeleteAsync(tenant, ProcessKey, "task_approve", "1", Ct);
        }
    }

    private static ApprovalDictionaryEntry Entry(
        string tenant,
        string? nodeId,
        string optionId,
        ApprovalSemantic semantic,
        string label)
    {
        var now = DateTimeOffset.UtcNow;

        return new ApprovalDictionaryEntry(
            tenant,
            ProcessKey,
            nodeId,
            optionId,
            semantic,
            ApprovalSemantics.OutcomeClassOf(semantic),
            label,
            RequiresComment: false,
            RequiresTargetNode: false,
            AllowedTargetNodes: null,
            Attributes: null,
            now,
            now);
    }

    private static string NewTenant() => "dict-" + Guid.NewGuid().ToString("N")[..12];

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();

        var count = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.tables "
            + "where table_schema = database() and table_name = 'nb_approval_dictionary'");

        Assert.SkipUnless(
            count >= 1,
            "库里还没有 nb_approval_dictionary：请对测试库执行 "
                + "dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql ... 补齐。");
    }
}
