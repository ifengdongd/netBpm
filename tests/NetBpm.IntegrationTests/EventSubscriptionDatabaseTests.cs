using Dapper;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Events;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 事件订阅与投递对着<b>真库</b>的验证（<c>nb_event_subscription</c> / <c>nb_event_delivery</c>）。
/// </summary>
/// <remarks>
/// 内存实现跑通不代表 SQL 跑得通。这里盯的是内存版天然不会出问题的三处：
/// <list type="number">
///   <item><description><b>多值列的往返</b>：事件类型白名单存成逗号分隔串、请求头存成 JSON，
///     写进去再读回来必须一模一样（多一个空格就会匹配不上）；</description></item>
///   <item><description><b>「取（没有就建）」的幂等</b>：<c>insert ignore</c> 撞复合主键要静默忽略，
///     两次调用不能把尝试次数冲掉 —— 否则重试计数永远归零，死信机制等于没有；</description></item>
///   <item><description><b>死信判定在一条 update 里</b>：<c>attempts + 1</c> 与状态切换必须同语句，
///     先读再写会被并发的另一个宿主覆盖。</description></item>
/// </list>
/// </remarks>
[Collection("MySql")]
public sealed class EventSubscriptionDatabaseTests : IClassFixture<RestEngineFactory>
{
    private const string DefaultTenant = "default";

    private readonly RestEngineFactory _factory;

    public EventSubscriptionDatabaseTests(RestEngineFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Subscription_ShouldRoundTrip_ThroughSql()
    {
        SkipUnlessReady();

        var store = _factory.Services.GetRequiredService<IEventSubscriptionStore>();

        var created = await store.CreateAsync(
            new EventSubscription
            {
                TenantId = DefaultTenant,
                Name = "真库往返",
                EventTypes = ["TASK_CREATED", "PROCESS_STARTED"],
                ProcessDefinitionId = "def-1",
                EndpointUrl = "https://erp.test/hook",
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Gateway"] = "gw-1" },
                MaxAttempts = 3,
            },
            Ct);

        try
        {
            Assert.False(string.IsNullOrWhiteSpace(created.Id));
            Assert.False(string.IsNullOrWhiteSpace(created.Secret));

            var read = await store.FindByIdAsync(created.Id, Ct);

            Assert.NotNull(read);
            Assert.Equal("真库往返", read!.Name);
            Assert.Equal(["TASK_CREATED", "PROCESS_STARTED"], read.EventTypes);
            Assert.Equal("def-1", read.ProcessDefinitionId);
            Assert.Equal("gw-1", read.Headers["X-Gateway"]);
            Assert.Equal(3, read.MaxAttempts);
            Assert.True(read.Enabled);

            // 按租户 + 启用状态过滤
            var enabled = await store.FindAsync(new EventSubscriptionQuery(DefaultTenant, true, 50), Ct);

            Assert.Contains(enabled, item => item.Id == created.Id);

            var disabled = await store.FindAsync(new EventSubscriptionQuery(DefaultTenant, false, 50), Ct);

            Assert.DoesNotContain(disabled, item => item.Id == created.Id);

            Assert.True(await store.SetEnabledAsync(created.Id, false, Ct));
            Assert.False((await store.FindByIdAsync(created.Id, Ct))!.Enabled);

            read.Name = "改过名字";
            Assert.True(await store.UpdateAsync(read, Ct));
            Assert.Equal("改过名字", (await store.FindByIdAsync(created.Id, Ct))!.Name);
        }
        finally
        {
            await store.DeleteAsync(created.Id, Ct);
        }

        Assert.Null(await store.FindByIdAsync(created.Id, Ct));
    }

    [Fact]
    public async Task Delivery_GetOrCreate_ShouldBeIdempotent_AndDeadLetterAfterMaxAttempts()
    {
        SkipUnlessReady();

        var subscriptions = _factory.Services.GetRequiredService<IEventSubscriptionStore>();
        var deliveries = _factory.Services.GetRequiredService<IEventDeliveryStore>();

        var subscription = await subscriptions.CreateAsync(
            new EventSubscription
            {
                TenantId = DefaultTenant,
                Name = "投递真库",
                EndpointUrl = "https://erp.test/hook",
            },
            Ct);

        try
        {
            var first = await deliveries.GetOrCreateAsync("evt-db-1", subscription.Id, DefaultTenant, Ct);

            Assert.Equal(EventDeliveryStatus.Pending, first.Status);

            // 失败一次后再次 GetOrCreate：不能把计数冲回 0（insert ignore 必须真的忽略）
            await deliveries.MarkFailureAsync(
                "evt-db-1", subscription.Id, "503", DateTime.UtcNow, null, 5, Ct);

            var second = await deliveries.GetOrCreateAsync("evt-db-1", subscription.Id, DefaultTenant, Ct);

            Assert.Equal(1, second.Attempts);

            // 达到上限进死信
            await deliveries.MarkFailureAsync(
                "evt-db-1", subscription.Id, "503", DateTime.UtcNow, null, 2, Ct);

            var dead = await deliveries.FindAsync("evt-db-1", subscription.Id, Ct);

            Assert.Equal(EventDeliveryStatus.DeadLetter, dead!.Status);
            Assert.Equal(2, dead.Attempts);

            // 非死信状态重投要失败（SQL 里带 status 条件）
            Assert.False(await deliveries.RetryAsync("evt-db-2", subscription.Id, DateTime.UtcNow, Ct));
            Assert.True(await deliveries.RetryAsync("evt-db-1", subscription.Id, DateTime.UtcNow, Ct));

            var retried = await deliveries.FindAsync("evt-db-1", subscription.Id, Ct);

            Assert.Equal(EventDeliveryStatus.Pending, retried!.Status);
            Assert.Equal(0, retried.Attempts);

            // 成功一次后按事件查得到
            await deliveries.MarkDeliveredAsync("evt-db-1", subscription.Id, DateTime.UtcNow, Ct);

            var all = await deliveries.FindByEventAsync("evt-db-1", Ct);

            Assert.Single(all);
            Assert.Equal(EventDeliveryStatus.Delivered, all[0].Status);
            Assert.NotNull(all[0].DeliveredAt);

            Assert.Equal(1, await deliveries.DeleteByEventAsync("evt-db-1", Ct));
        }
        finally
        {
            await subscriptions.DeleteAsync(subscription.Id, Ct);
            await deliveries.DeleteByEventAsync("evt-db-1", Ct);
        }
    }

    private static void SkipUnlessReady()
    {
        Assert.SkipUnless(MySqlTestDatabase.IsAvailable, MySqlTestDatabase.SkipReason);

        using var session = MySqlTestDatabase.CreateSessionFactory().Create();
        var count = session.Connection.ExecuteScalar<long>(
            "select count(*) from information_schema.tables "
            + "where table_schema = database() and table_name = 'nb_event_subscription'");

        Assert.SkipUnless(
            count >= 1,
            "库里还没有 nb_event_subscription：请对测试库执行 "
                + "dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql ... 补齐（新增平台表只能靠幂等脚本补）。");
    }
}
