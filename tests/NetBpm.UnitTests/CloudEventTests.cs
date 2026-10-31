using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NetBpm.Engine.Events;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Diagnostics;
using NetBpm.Runtime.Events;

namespace NetBpm.UnitTests;

/// <summary>
/// CloudEvents 信封、HMAC 签名与事件投递（M13）。
/// </summary>
/// <remarks>
/// 锁住四件事：
/// <list type="number">
///   <item><description><b>信封合规</b>：标准字段齐、扩展属性平铺、<c>data</c> 是对象不是字符串；</description></item>
///   <item><description><b>签名能验</b>：签出来的订阅方验得过；改 body / 过期时间戳验不过；</description></item>
///   <item><description><b>按订阅投递</b>：不匹配的订阅收不到；没人订阅时事件算发布成功；</description></item>
///   <item><description><b>失败要重投、成功不重发</b>：一个订阅挂了整条事件重来，
///     但已成功的订阅不会被重复打扰；超过额度进死信。</description></item>
/// </list>
/// </remarks>
public sealed class CloudEventTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TypeOf_ShouldMapEngineEventTypeToCloudEventsType()
    {
        Assert.Equal("netbpm.process.started", CloudEvents.TypeOf("PROCESS_STARTED"));
        Assert.Equal("netbpm.task.created", CloudEvents.TypeOf("TASK_CREATED"));
        Assert.Equal("netbpm.activity.completed", CloudEvents.TypeOf("ACTIVITY_COMPLETED"));
    }

    [Fact]
    public void Serialize_ShouldProduceCloudEventsEnvelope()
    {
        var cloudEvent = CloudEventSerializer.Create(Event("PROCESS_STARTED", """{"days":"3"}"""));

        Assert.Equal("1.0", cloudEvent.SpecVersion);
        Assert.Equal("evt-1", cloudEvent.Id);
        Assert.Equal("/netbpm/acme", cloudEvent.Source);
        Assert.Equal("netbpm.process.started", cloudEvent.Type);
        Assert.Equal("inst-1", cloudEvent.Subject);

        using var json = JsonDocument.Parse(CloudEventSerializer.Serialize(cloudEvent));
        var root = json.RootElement;

        // 扩展属性平铺在顶层（CloudEvents 的要求），不是塞进 data 里
        Assert.Equal("acme", root.GetProperty("netbmptenantid").GetString());
        Assert.Equal("inst-1", root.GetProperty("netbpmprocessinstanceid").GetString());
        Assert.Equal("act-1", root.GetProperty("netbpmactivityid").GetString());

        // data 必须是对象：订阅方按 JSON 取值，拿到字符串会全线解析失败
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.Equal("3", root.GetProperty("data").GetProperty("days").GetString());
    }

    [Fact]
    public void Serialize_ShouldKeepPayloadAsString_WhenPayloadIsNotJson()
    {
        var cloudEvent = CloudEventSerializer.Create(Event("TASK_CREATED", "不是 JSON"));

        var body = Encoding.UTF8.GetString(CloudEventSerializer.Serialize(cloudEvent));

        Assert.Contains("不是 JSON", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ShouldAcceptValidSignature()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt-1\"}");
        var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

        var signature = EventSignature.Sign("s3cr3t", timestamp, body);

        Assert.StartsWith("sha256=", signature, StringComparison.Ordinal);
        Assert.True(EventSignature.Verify("s3cr3t", timestamp, body, signature));
    }

    [Fact]
    public void Verify_ShouldRejectTamperedBody()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt-1\"}");
        var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var signature = EventSignature.Sign("s3cr3t", timestamp, body);

        Assert.False(EventSignature.Verify("s3cr3t", timestamp, Encoding.UTF8.GetBytes("{\"id\":\"evt-2\"}"), signature));
        Assert.False(EventSignature.Verify("other", timestamp, body, signature));
    }

    [Fact]
    public void Verify_ShouldRejectReplayedTimestamp()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt-1\"}");
        var stale = new DateTimeOffset(DateTime.UtcNow.AddHours(-2)).ToUnixTimeSeconds();
        var signature = EventSignature.Sign("s3cr3t", stale, body);

        // 只签 body 不签时间的方案挡不住重放：抓到一个合法请求就能一直发
        Assert.False(EventSignature.Verify("s3cr3t", stale, body, signature, TimeSpan.FromMinutes(5)));
        Assert.True(EventSignature.Verify("s3cr3t", stale, body, signature, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void Matches_ShouldFilterByTenantEventTypeAndDefinition()
    {
        var subscription = new EventSubscription
        {
            TenantId = "acme",
            EventTypes = ["TASK_CREATED"],
        };

        Assert.True(subscription.Matches(Event("TASK_CREATED", null, tenantId: "acme")));
        Assert.False(subscription.Matches(Event("TASK_COMPLETED", null, tenantId: "acme")));
        Assert.False(subscription.Matches(Event("TASK_CREATED", null, tenantId: "other")));

        // 白名单为空 = 全部
        Assert.True(new EventSubscription { TenantId = "acme" }.Matches(Event("TASK_CREATED", null, tenantId: "acme")));

        // 限定流程定义 id
        var narrowed = new EventSubscription { TenantId = "acme", ProcessDefinitionId = "def-9" };

        Assert.True(narrowed.Matches(Event("TASK_CREATED", null, tenantId: "acme", definitionId: "def-9")));
        Assert.False(narrowed.Matches(Event("TASK_CREATED", null, tenantId: "acme", definitionId: "def-1")));
    }

    [Fact]
    public async Task HandleAsync_ShouldDeliverToMatchingSubscriptionOnly()
    {
        var transport = new FakeTransport();
        var handler = BuildHandler(transport, out var subscriptions, out _);

        await subscriptions.CreateAsync(
            new EventSubscription
            {
                TenantId = "acme",
                Name = "命中",
                EventTypes = ["TASK_CREATED"],
                EndpointUrl = "https://hit.test/hook",
            }, Ct);
        await subscriptions.CreateAsync(
            new EventSubscription
            {
                TenantId = "acme",
                Name = "不命中",
                EventTypes = ["PROCESS_STARTED"],
                EndpointUrl = "https://miss.test/hook",
            }, Ct);

        await handler.HandleAsync(Event("TASK_CREATED", """{"days":"3"}"""), Ct);

        var sent = Assert.Single(transport.Requests);

        Assert.Contains("hit.test", sent.Url, StringComparison.Ordinal);

        // 只有一个订阅被投递，且带上了签名头
        Assert.True(sent.Headers.ContainsKey(CloudEvents.SignatureHeader));
        Assert.True(sent.Headers.ContainsKey(CloudEvents.TimestampHeader));
        Assert.Equal("evt-1", sent.Headers[CloudEvents.EventIdHeader]);
    }

    [Fact]
    public async Task HandleAsync_ShouldThrow_WhenAnySubscriptionFails()
    {
        var transport = new FakeTransport { Fail = true };
        var handler = BuildHandler(transport, out var subscriptions, out var deliveries);

        var subscription = await subscriptions.CreateAsync(
            new EventSubscription { TenantId = "acme", Name = "挂掉的" }, Ct);

        // 抛异常 = 告诉 outbox「还没发完」，让它退避重投；吞掉就等于谎报成功
        await Assert.ThrowsAsync<EventDeliveryException>(
            () => handler.HandleAsync(Event("TASK_CREATED", null), Ct).AsTask());

        var delivery = await deliveries.FindAsync("evt-1", subscription.Id, Ct);

        Assert.NotNull(delivery);
        Assert.Equal(EventDeliveryStatus.Failed, delivery!.Status);
        Assert.Equal(1, delivery.Attempts);
    }

    [Fact]
    public async Task HandleAsync_ShouldNotReDeliverToAlreadyDeliveredSubscription()
    {
        var transport = new FakeTransport { Fail = true };
        var handler = BuildHandler(transport, out var subscriptions, out var deliveries);

        await subscriptions.CreateAsync(
            new EventSubscription { TenantId = "acme", Name = "A", EndpointUrl = "https://a.test/hook" }, Ct);
        await subscriptions.CreateAsync(
            new EventSubscription { TenantId = "acme", Name = "B", EndpointUrl = "https://b.test/hook" }, Ct);

        // 第一次：两家都挂
        await Assert.ThrowsAsync<EventDeliveryException>(
            () => handler.HandleAsync(Event("TASK_CREATED", null), Ct).AsTask());

        // 第二次：两家都恢复 → 都投成功
        transport.Fail = false;

        await handler.HandleAsync(Event("TASK_CREATED", null), Ct);

        Assert.Equal(4, transport.Requests.Count);

        // 第三次：即使订阅方此刻全挂，已成功的订阅也不该被再打扰一次
        transport.Fail = true;

        await handler.HandleAsync(Event("TASK_CREATED", null), Ct);

        Assert.Equal(4, transport.Requests.Count);

        var delivered = await deliveries.FindByEventAsync("evt-1", Ct);

        Assert.All(delivered, item => Assert.Equal(EventDeliveryStatus.Delivered, item.Status));
    }

    [Fact]
    public async Task HandleAsync_ShouldDeadLetterSubscription_AfterMaxAttempts()
    {
        var transport = new FakeTransport { Fail = true };
        var handler = BuildHandler(transport, out var subscriptions, out var deliveries, out var alerts);

        var subscription = await subscriptions.CreateAsync(
            new EventSubscription { TenantId = "acme", Name = "一直挂", MaxAttempts = 2 }, Ct);

        await Assert.ThrowsAsync<EventDeliveryException>(
            () => handler.HandleAsync(Event("TASK_CREATED", null), Ct).AsTask());
        await Assert.ThrowsAsync<EventDeliveryException>(
            () => handler.HandleAsync(Event("TASK_CREATED", null), Ct).AsTask());

        var delivery = await deliveries.FindAsync("evt-1", subscription.Id, Ct);

        Assert.Equal(EventDeliveryStatus.DeadLetter, delivery!.Status);
        Assert.Single(alerts.Alerts);
        Assert.Equal(AlertCodes.EventDeliveryDeadLetter, alerts.Alerts[0].Code);
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenNobodySubscribed()
    {
        var transport = new FakeTransport();
        var handler = BuildHandler(transport, out _, out _);

        // 没人订阅不是失败：事件留在库里反复重试毫无意义，后加的订阅只对新事件生效
        await handler.HandleAsync(Event("TASK_CREATED", null), Ct);

        Assert.Empty(transport.Requests);
    }

    private static CloudEventOutboxHandler BuildHandler(
        FakeTransport transport,
        out IEventSubscriptionStore subscriptions,
        out IEventDeliveryStore deliveries)
    {
        return BuildHandler(transport, out subscriptions, out deliveries, out _);
    }

    private static CloudEventOutboxHandler BuildHandler(
        FakeTransport transport,
        out IEventSubscriptionStore subscriptions,
        out IEventDeliveryStore deliveries,
        out InMemoryAlertSink alerts)
    {
        subscriptions = new InMemoryEventSubscriptionStore();
        deliveries = new InMemoryEventDeliveryStore();
        alerts = new InMemoryAlertSink();

        return new CloudEventOutboxHandler(
            subscriptions,
            deliveries,
            transport,
            Microsoft.Extensions.Options.Options.Create(new EventDeliveryOptions()),
            NullLogger<CloudEventOutboxHandler>.Instance,
            [alerts]);
    }

    private static OutboxEvent Event(
        string eventType,
        string? payload,
        string tenantId = "acme",
        string? definitionId = "def-1") =>
        new()
        {
            Id = "evt-1",
            EventType = eventType,
            EntityId = "inst-1",
            ProcessInstanceId = "inst-1",
            ProcessDefinitionId = definitionId,
            ExecutionId = "exec-1",
            ActivityId = "act-1",
            TenantId = tenantId,
            Payload = payload,
            CreateTime = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc),
        };

    private sealed class FakeTransport : IEventDeliveryTransport
    {
        public List<(string Url, byte[] Body, IDictionary<string, string> Headers)> Requests { get; } = [];

        public bool Fail { get; set; }

        public string? FailForUrlsContaining { get; set; }

        public ValueTask<EventDeliveryOutcome> PostAsync(
            string endpointUrl,
            byte[] body,
            string contentType,
            IDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            Requests.Add((endpointUrl, body, headers));

            var shouldFail = Fail || FailForUrlsContaining is { Length: > 0 } marker
                && endpointUrl.Contains(marker, StringComparison.Ordinal);

            return ValueTask.FromResult(shouldFail
                ? new EventDeliveryOutcome(false, 503, "订阅方返回 HTTP 503。")
                : new EventDeliveryOutcome(true, 200, null));
        }
    }
}
