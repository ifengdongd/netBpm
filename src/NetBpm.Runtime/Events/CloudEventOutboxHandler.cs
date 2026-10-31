using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Events;
using NetBpm.Runtime.Diagnostics;

namespace NetBpm.Runtime.Events;

/// <summary>
/// 事件投递选项（<c>NetBpm:Events</c>）。
/// </summary>
public sealed class EventDeliveryOptions
{
    /// <summary>单次投递超时（默认 10 秒）。订阅方慢会拖住整条 outbox 轮询，别设太大。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>CloudEvents <c>source</c> 前缀（默认 <c>/netbpm</c>）。</summary>
    public string SourcePrefix { get; set; } = "/netbpm";

    /// <summary>订阅未单独指定时的最大尝试次数（默认 5）。</summary>
    public int DefaultMaxAttempts { get; set; } = 5;

    /// <summary>一轮最多给多少个订阅投递（防止一个实例配了上百个订阅把轮询拖死）。</summary>
    public int MaxSubscriptionsPerEvent { get; set; } = 50;
}

/// <summary>
/// 把 outbox 事件按订阅投递出去（CloudEvents + HMAC 签名）。
/// <para>
/// 它是 <see cref="IOutboxEventHandler"/>，由 <c>OutboxPublisherService</c> 驱动：
/// <b>成功返回 = 打标已发布；抛异常 = 由 outbox 退避重投</b>。
/// 因此这里刻意<b>不吞异常</b> —— 吞掉就等于告诉引擎「发出去了」，事件会被打标，
/// 订阅方却什么都没收到，那是最难排查的一类故障。
/// </para>
/// <para>
/// 🔑 <b>幂等靠订阅方</b>：投递是「至少一次」。同一条事件重投时 <c>id</c> 不变，
/// 订阅方必须按 <see cref="CloudEvent.Id"/> 去重。投递记录
/// （<see cref="IEventDeliveryStore"/>）只是减少无谓的重发，不是幂等的替代品。
/// </para>
/// <para>
/// 一个订阅挂了<b>不会</b>拖住别家：已成功的投递记着状态，重投时直接跳过。
/// 只有当「至少还有一个订阅没投成」时才抛异常让事件重来。
/// </para>
/// </summary>
public sealed class CloudEventOutboxHandler : IOutboxEventHandler
{
    private readonly IEventSubscriptionStore _subscriptions;
    private readonly IEventDeliveryStore _deliveries;
    private readonly IEventDeliveryTransport _transport;
    private readonly IEnumerable<IAlertSink> _sinks;
    private readonly EventDeliveryOptions _options;
    private readonly ILogger<CloudEventOutboxHandler> _logger;

    public CloudEventOutboxHandler(
        IEventSubscriptionStore subscriptions,
        IEventDeliveryStore deliveries,
        IEventDeliveryTransport transport,
        IOptions<EventDeliveryOptions> options,
        ILogger<CloudEventOutboxHandler> logger,
        IEnumerable<IAlertSink>? sinks = null)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptions = subscriptions;
        _deliveries = deliveries;
        _transport = transport;
        _options = options.Value;
        _logger = logger;
        _sinks = sinks ?? [];
    }

    /// <inheritdoc />
    public async ValueTask HandleAsync(OutboxEvent outboxEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);

        var tenantId = string.IsNullOrWhiteSpace(outboxEvent.TenantId) ? "default" : outboxEvent.TenantId;

        var matched = (await _subscriptions
                .FindAsync(new EventSubscriptionQuery(tenantId, true, _options.MaxSubscriptionsPerEvent), ct)
                .ConfigureAwait(false))
            .Where(subscription => subscription.Matches(outboxEvent))
            .ToArray();

        if (matched.Length == 0)
        {
            // 没人订阅也算「发布成功」：事件留在库里反复重试没有意义，
            // 而订阅是后加的 —— 加了之后新事件自然会送过去（历史事件不补投）
            return;
        }

        var body = CloudEventSerializer.Serialize(CloudEventSerializer.Create(outboxEvent, _options.SourcePrefix));
        var failures = new List<string>();

        foreach (var subscription in matched)
        {
            var failure = await DeliverOneAsync(outboxEvent, subscription, tenantId, body, ct).ConfigureAwait(false);

            if (failure is not null)
            {
                failures.Add(failure);
            }
        }

        if (failures.Count > 0)
        {
            // 抛出去让 outbox 退避重投；已成功的订阅靠投递记录跳过，不会重复打扰
            throw new EventDeliveryException(outboxEvent.Id, failures);
        }
    }

    private async ValueTask<string?> DeliverOneAsync(
        OutboxEvent outboxEvent,
        EventSubscription subscription,
        string tenantId,
        byte[] body,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var delivery = await _deliveries
            .GetOrCreateAsync(outboxEvent.Id, subscription.Id, tenantId, ct)
            .ConfigureAwait(false);

        if (string.Equals(delivery.Status, EventDeliveryStatus.Delivered, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(delivery.Status, EventDeliveryStatus.DeadLetter, StringComparison.Ordinal))
        {
            // 已经判死：不再尝试，但必须让事件留在未发布状态（否则事件被静默丢弃，
            // 运维根本不知道有一条事件没送出去）—— 因此算作失败返回
            return $"订阅 '{subscription.Name}' 的投递已进入死信：{delivery.LastError}";
        }

        // 🔑 这里刻意<b>不</b>做订阅级退避：退避节奏由 outbox 统一管（事件级 NEXT_ATTEMPT_）。
        // 两级退避叠加会出现「事件说可以投了、订阅说自己还在退避」的死结 —— 事件一次次重试，
        // 投递一次次拒绝，计数永远涨不上去，最后双双进死信却一次都没真发过。
        // NextAttemptAt 仍然记录（运维看得到预计下次时间），但不参与是否尝试的判定。
        var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds();
        var signature = EventSignature.Sign(subscription.Secret, timestamp, body);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CloudEvents.SignatureHeader] = signature,
            [CloudEvents.TimestampHeader] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [CloudEvents.EventIdHeader] = outboxEvent.Id,
        };

        foreach (var header in subscription.Headers)
        {
            headers[header.Key] = header.Value;
        }

        var outcome = await _transport
            .PostAsync(subscription.EndpointUrl, body, CloudEvents.JsonContentType, headers, _options.Timeout, ct)
            .ConfigureAwait(false);

        var maxAttempts = subscription.MaxAttempts > 0 ? subscription.MaxAttempts : _options.DefaultMaxAttempts;

        if (outcome.Success)
        {
            await _deliveries
                .MarkDeliveredAsync(outboxEvent.Id, subscription.Id, now, ct)
                .ConfigureAwait(false);

            EngineDiagnostics.EventDeliveries.Add(
                1,
                new KeyValuePair<string, object?>("subscription", subscription.Id),
                new KeyValuePair<string, object?>("result", "delivered"));

            _logger.LogDebug(
                "事件 {EventId}（{EventType}）已投递到订阅 '{SubscriptionName}'。",
                outboxEvent.Id,
                outboxEvent.EventType,
                subscription.Name);

            return null;
        }

        var attempts = delivery.Attempts + 1;
        var isDead = attempts >= maxAttempts;

        await _deliveries
            .MarkFailureAsync(
                outboxEvent.Id,
                subscription.Id,
                outcome.Error ?? "未知失败。",
                now,
                isDead ? null : now + OutboxRetry.Backoff(attempts),
                maxAttempts,
                ct)
            .ConfigureAwait(false);

        EngineDiagnostics.EventDeliveries.Add(
            1,
            new KeyValuePair<string, object?>("subscription", subscription.Id),
            new KeyValuePair<string, object?>("result", isDead ? "dead_letter" : "failed"));

        if (isDead)
        {
            await RaiseDeadLetterAlertAsync(outboxEvent, subscription, outcome, ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "事件 {EventId} 投递到订阅 '{SubscriptionName}'（{EndpointUrl}）失败：{Error}（第 {Attempts}/{MaxAttempts} 次）。",
            outboxEvent.Id,
            subscription.Name,
            subscription.EndpointUrl,
            outcome.Error,
            attempts,
            maxAttempts);

        return $"订阅 '{subscription.Name}' 投递失败：{outcome.Error}";
    }

    private async ValueTask RaiseDeadLetterAlertAsync(
        OutboxEvent outboxEvent,
        EventSubscription subscription,
        EventDeliveryOutcome outcome,
        CancellationToken ct)
    {
        var alert = PlatformAlert.Raise(
            AlertCodes.EventDeliveryDeadLetter,
            AlertSeverity.Critical,
            $"事件 '{outboxEvent.Id}'（{outboxEvent.EventType}）投递到订阅 '{subscription.Name}'"
                + $" 已超过 {subscription.MaxAttempts} 次仍失败：{outcome.Error}",
            subscription.TenantId,
            outboxEvent.Id,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["subscriptionId"] = subscription.Id,
                ["endpointUrl"] = subscription.EndpointUrl,
                ["eventType"] = outboxEvent.EventType,
                ["processInstanceId"] = outboxEvent.ProcessInstanceId,
            });

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.RaiseAsync(alert, ct).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "告警出口 {SinkType} 处理投递死信告警时失败。", sink.GetType().Name);
            }
        }
    }
}

/// <summary>
/// 事件投递未完成（至少一个订阅没投成功）。抛给 outbox 让它安排重投。
/// </summary>
public sealed class EventDeliveryException(string eventId, IReadOnlyList<string> failures)
    : Exception($"事件 '{eventId}' 的投递尚未完成：{string.Join("；", failures)}")
{
    /// <summary>事件 id。</summary>
    public string EventId { get; } = eventId;

    /// <summary>各订阅的失败原因。</summary>
    public IReadOnlyList<string> Failures { get; } = failures;
}
