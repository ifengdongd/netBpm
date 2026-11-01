using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Events;
using NetBpm.Runtime;
using NetBpm.Runtime.Hosting;

namespace NetBpm.UnitTests;

/// <summary>
/// outbox 积压指标（M13 补漏：此前积压只有端点数据，Prometheus 抓不到）。
/// </summary>
/// <remarks>
/// 锁住四件事：
/// <list type="number">
///   <item><description><b>采样算得对</b>：待发布条数、死信条数、最老一条的年龄；</description></item>
///   <item><description><b>年龄比条数更敏感</b>：没有积压时年龄是 0，有积压时按最老那条算；</description></item>
///   <item><description><b>采样失败不拖垮宿主</b>：一轮失败只是保留旧值并判为 stale；</description></item>
///   <item><description><b>指标名对得上运维件</b>：告警规则与看板里写的名字，必须与 Meter 里的一致。</description></item>
/// </list>
/// </remarks>
public sealed class OutboxBacklogMetricsTests
{
    private static OutboxBacklogMetricsService CreateService(
        IEventOutboxStore store,
        OutboxBacklogMetricsOptions? options = null) =>
        new(store,
            Options.Create(options ?? new OutboxBacklogMetricsOptions()),
            NullLogger<OutboxBacklogMetricsService>.Instance);

    private static OutboxEvent Event(string id, DateTime createdAt) => new()
    {
        Id = id,
        EventType = "task.created",
        CreateTime = createdAt,
    };

    [Fact]
    public void 未采样时积压指标应当判定为不可信()
    {
        OutboxBacklogProbe.Reset();

        Assert.True(OutboxBacklogProbe.IsStale);
        Assert.Null(OutboxBacklogProbe.LastSampledAtUtc);
        Assert.Equal(0, OutboxBacklogProbe.Pending);
    }

    [Fact]
    public async Task 采样应当区分待发布条数_死信条数与最老一条的年龄()
    {
        OutboxBacklogProbe.Reset();

        var store = new InMemoryEventOutbox();
        var now = DateTime.UtcNow;

        await store.EnqueueAsync(Event("e-old", now.AddMinutes(-42)));
        await store.EnqueueAsync(Event("e-mid", now.AddMinutes(-3)));
        await store.EnqueueAsync(Event("e-new", now.AddSeconds(-5)));

        var dead = Event("e-dead", now.AddMinutes(-20));
        await store.EnqueueAsync(dead);
        await store.MarkFailureAsync("e-dead", "boom", now, now);
        await store.MarkFailureAsync("e-dead", "boom", now, now);
        await store.MarkFailureAsync("e-dead", "boom", now, now);
        await store.MarkFailureAsync("e-dead", "boom", now, now);
        await store.MarkFailureAsync("e-dead", "boom", now, now);

        await CreateService(store).SampleOnceAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(3, OutboxBacklogProbe.Pending);
        Assert.Equal(1, OutboxBacklogProbe.DeadLetter);

        // 年龄取最老的一条（42 分钟），不是最近那条
        Assert.InRange(OutboxBacklogProbe.OldestPendingAgeSeconds, 42 * 60, 42 * 60 + 30);
        Assert.False(OutboxBacklogProbe.IsStale);
    }

    [Fact]
    public async Task 没有待发布事件时年龄应当归零而不是残留旧值()
    {
        OutboxBacklogProbe.Reset();

        var store = new InMemoryEventOutbox();

        await store.EnqueueAsync(Event("e-1", DateTime.UtcNow.AddMinutes(-30)));
        await CreateService(store).SampleOnceAsync(TimeSpan.FromSeconds(15));

        Assert.True(OutboxBacklogProbe.OldestPendingAgeSeconds > 0);

        await store.MarkPublishedAsync("e-1", DateTime.UtcNow);
        await CreateService(store).SampleOnceAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, OutboxBacklogProbe.Pending);
        Assert.Equal(0d, OutboxBacklogProbe.OldestPendingAgeSeconds);
    }

    [Fact]
    public async Task 采样失败应当只记日志不抛出_并让旧值保持可判定为过期()
    {
        OutboxBacklogProbe.Reset();

        var interval = TimeSpan.FromSeconds(1);
        OutboxBacklogProbe.Record(7, 2, 123d, interval);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService(new ThrowingOutboxStore()).SampleOnceAsync(interval).AsTask());

        // 失败没有污染上一次的结果，也没有把进程拖垮：值还在、但已经过了容忍窗口
        Assert.Equal(7, OutboxBacklogProbe.Pending);
    }

    [Fact]
    public async Task 采样器把积压暴露成与运维件同名的指标()
    {
        OutboxBacklogProbe.Reset();

        var store = new InMemoryEventOutbox();
        await store.EnqueueAsync(Event("e-1", DateTime.UtcNow.AddMinutes(-10)));
        await CreateService(store).SampleOnceAsync(TimeSpan.FromSeconds(15));

        var measured = new Dictionary<string, double>(StringComparer.Ordinal);
        var published = new List<string>();

        // 先摸一下静态字段：Meter 与 instrument 在类型初始化时创建，
        // 必须在 listener.Start() 之前就存在 —— Start 只补发「当时已发布」的 instrument
        Assert.Equal("netbpm.outbox.pending", EngineDiagnostics.OutboxPending.Name);
        Assert.Equal("netbpm.outbox.dead_letter", EngineDiagnostics.OutboxDeadLetter.Name);
        Assert.Equal("netbpm.outbox.oldest_pending_age", EngineDiagnostics.OutboxOldestPendingAge.Name);
        Assert.Equal("netbpm.outbox.backlog_stale", EngineDiagnostics.OutboxBacklogStale.Name);

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, _) =>
        {
            published.Add(instrument.Meter.Name + "/" + instrument.Name);

            if (instrument.Meter.Name == EngineDiagnostics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>(
            (instrument, value, _, _) => measured[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, _, _) => measured[instrument.Name] = value);
        listener.MeasurementsCompleted = (_, _) => { };

        listener.Start();

        // 观测式指标要显式触发一次回调才会产生 measurement（没有 exporter 时没人来抓）
        listener.RecordObservableInstruments();

        // 名字直接对应 deploy/observability 里的告警与看板：写错了那边会静默不告警
        Assert.Contains("NetBpm.Engine/netbpm.outbox.pending", published);
        Assert.Equal(1, measured["netbpm.outbox.pending"]);
        Assert.Equal(0, measured["netbpm.outbox.dead_letter"]);
        Assert.Equal(0, measured["netbpm.outbox.backlog_stale"]);
        Assert.True(measured["netbpm.outbox.oldest_pending_age"] >= 10 * 60);
    }

    [Fact]
    public void 开outbox时宿主应自动挂上积压采样_并按配置节读参数()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["NetBpm:Outbox:Metrics:IntervalSeconds"] = "42",
            })
            .Build();

        var services = new ServiceCollection();
        // 必须按 IConfiguration 注册：宿主里是框架注册的，这里手工补上
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddNetBpm(options => options.EnableOutbox = true);

        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IHostedService>().OfType<OutboxBacklogMetricsService>());
        Assert.Equal(
            TimeSpan.FromSeconds(42),
            provider.GetRequiredService<IOptions<OutboxBacklogMetricsOptions>>().Value.Interval);
    }

    [Fact]
    public void 停机后应当把积压指标标记为不可信()
    {
        OutboxBacklogProbe.Reset();

        OutboxBacklogProbe.Record(5, 1, 60d, TimeSpan.FromSeconds(15));
        Assert.False(OutboxBacklogProbe.IsStale);

        OutboxBacklogProbe.MarkStale();

        Assert.True(OutboxBacklogProbe.IsStale);
        // 数值留着（还能看最后一次的结果），但 stale 明确说别拿它当现状
        Assert.Equal(5, OutboxBacklogProbe.Pending);
    }

    /// <summary>一轮采样必炸的存储：用来验证指标采样不会把宿主拖下水。</summary>
    private sealed class ThrowingOutboxStore : IEventOutboxStore
    {
        public ValueTask EnqueueAsync(OutboxEvent outboxEvent, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<IReadOnlyList<OutboxEvent>> FindPendingAsync(
            DateTime utcNow, int maxCount, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<bool> TryClaimAsync(
            string eventId, DateTime utcNow, DateTime leaseUntilUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<IReadOnlyList<OutboxEvent>> FindAsync(OutboxQuery query, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<int> CountAsync(OutboxQuery query, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<bool> MarkPublishedAsync(string eventId, DateTime nowUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask MarkFailureAsync(
            string eventId, string reason, DateTime nowUtc, DateTime nextAttemptUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<bool> RetryAsync(string eventId, DateTime nowUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<bool> DeferAsync(string eventId, DateTime nextAttemptUtc, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public ValueTask<bool> DeleteAsync(string eventId, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}
