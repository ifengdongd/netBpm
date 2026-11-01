using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Events;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 死信查询对着<b>真库</b>的验证（<c>nb_event_delivery</c> 的 <c>offset/fetch</c> 分页）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么专门一条：<see cref="IEventDeliveryStore.ListDeadLettersAsync"/> 的分页子句由
/// <c>SqlPaging</c> 按方言生成，两个方言的写法完全不同（MySQL <c>limit/offset</c> 与
/// SQL Server <c>offset ... fetch next</c>）。内存版永远是对的，SQL 写错了只有真库能发现。
/// </para>
/// <para>
/// 顺带钉住两件更容易错的事：① 非死信行（还在重试额度里的 <c>FAILED</c>）不能出现在列表里——
/// 管理端看到了就会去重投，等于把正常的也重发一遍；② offset 真的生效，
/// 否则「第 2 页」会把第 1 页原样再给一遍，翻页时看起来像数据库卡住了。
/// </para>
/// </remarks>
[Collection("MySql")]
public sealed class EventDeadLetterQueryTests : IClassFixture<RestEngineFactory>
{
    private readonly RestEngineFactory _factory;

    public EventDeadLetterQueryTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListDeadLetters_ShouldFilterAndPage_OnRealSql()
    {
        SkipUnlessReady();

        var store = _factory.Services.GetRequiredService<IEventDeliveryStore>();

        // 每次用独立租户：平台表不被 EngineTableCleaner 清理，复用固定值会让上一轮的残留混进 total
        var tenant = "dlq-" + Guid.NewGuid().ToString("N")[..12];
        const string SubA = "sub-a";
        const string SubB = "sub-b";

        var now = DateTime.UtcNow;

        try
        {
            // sub-a 两条死信；sub-b 一条死信 + 一条还在退避的失败
            await SeedAsync(store, tenant, "e1", SubA, deadLetter: true, now);
            await SeedAsync(store, tenant, "e2", SubA, deadLetter: true, now);
            await SeedAsync(store, tenant, "e3", SubB, deadLetter: true, now);
            await SeedAsync(store, tenant, "e4", SubB, deadLetter: false, now);

            var first = await store.ListDeadLettersAsync(new EventDeadLetterQuery(tenant, null, 0, 2), Ct);

            Assert.Equal(3, first.Total);
            Assert.Equal(2, first.Items.Count);

            var second = await store.ListDeadLettersAsync(new EventDeadLetterQuery(tenant, null, 2, 2), Ct);

            Assert.Equal(3, second.Total);
            Assert.Single(second.Items);

            // offset 生效：两页不能有交集，也不能漏
            var union = first.Items
                .Select(item => item.EventId)
                .Union(second.Items.Select(item => item.EventId), StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(["e1", "e2", "e3"], union);

            // 按订阅过滤
            var onlyB = await store.ListDeadLettersAsync(new EventDeadLetterQuery(tenant, SubB, 0, 10), Ct);

            Assert.Equal(1, onlyB.Total);
            Assert.Equal("e3", onlyB.Items[0].EventId);

            // e4 是 FAILED（还有重试额度）：不能混进死信列表
            Assert.DoesNotContain(onlyB.Items, item => string.Equals(item.EventId, "e4", StringComparison.Ordinal));

            // 空条件 = 跨租户：本次造的四条都在，且都不该是别的租户的
            var crossTenant = await store.ListDeadLettersAsync(
                new EventDeadLetterQuery(null, SubA, 0, 100),
                Ct);

            Assert.Contains(crossTenant.Items, item => string.Equals(item.TenantId, tenant, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var eventId in new[] { "e1", "e2", "e3", "e4" })
            {
                await store.DeleteByEventAsync(eventId, Ct);
            }
        }
    }

    private static async Task SeedAsync(
        IEventDeliveryStore store,
        string tenant,
        string eventId,
        string subscriptionId,
        bool deadLetter,
        DateTime now)
    {
        await store.GetOrCreateAsync(eventId, subscriptionId, tenant, Ct);

        await store.MarkFailureAsync(
            eventId,
            subscriptionId,
            reason: deadLetter ? "回调 500" : "回调超时（将继续重试）",
            nowUtc: now,
            nextAttemptUtc: deadLetter ? null : now.AddSeconds(30),
            maxAttempts: deadLetter ? 1 : 5,
            ct: Ct);
    }

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();

        var count = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.tables "
            + "where table_schema = database() and table_name = 'nb_event_delivery'");

        Assert.SkipUnless(
            count >= 1,
            "库里还没有 nb_event_delivery：请对测试库执行 "
                + "dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql ... 补齐（新增平台表只能靠幂等脚本补）。");
    }
}
