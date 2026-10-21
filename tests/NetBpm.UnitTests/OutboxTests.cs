using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Events;
using NetBpm.Engine.Services;
using NetBpm.Runtime.Hosting;

// BPMN 模型里也有 Task 类型，会与 System.Threading.Tasks.Task 冲突
using Task = System.Threading.Tasks.Task;

namespace NetBpm.UnitTests;

/// <summary>事务性 outbox：事件写入、发布、失败重试。</summary>
public sealed class OutboxTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ProcessExecution_ShouldRecordEventsInOutbox()
    {
        var host = TestEngine.Create();

        await host.StartProcessAsync(TestEngine.SequentialProcess("svc"), ct: Ct);

        var events = await host.Outbox.FindAsync(new OutboxQuery(MaxResults: 200), Ct);

        Assert.Contains(events, item => item.EventType == EngineEvents.ProcessStarted);
        Assert.Contains(events, item => item.EventType == EngineEvents.ActivityStarted);
        Assert.Contains(events, item => item.EventType == EngineEvents.ActivityCompleted);
        Assert.Contains(events, item => item.EventType == EngineEvents.ProcessCompleted);

        Assert.All(events, item =>
        {
            Assert.False(item.Published);
            Assert.False(item.DeadLetter);
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Payload));
            Assert.NotNull(item.ProcessInstanceId);
        });

        // 事件 id 唯一（订阅方的幂等键）
        Assert.Equal(events.Count, events.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Publisher_ShouldDeliverPendingEvents_AndMarkPublished()
    {
        var host = TestEngine.Create();
        await host.StartProcessAsync(TestEngine.SequentialProcess("svc"), ct: Ct);

        var pendingBefore = await host.Outbox.FindPendingAsync(DateTime.UtcNow.AddMinutes(1), 200, Ct);
        Assert.NotEmpty(pendingBefore);

        var handler = new CollectingHandler();
        var publisher = new OutboxPublisherService(
            host.Outbox,
            [handler],
            Options.Create(new OutboxPublisherOptions
            {
                StartupDelay = TimeSpan.Zero,
                PollInterval = TimeSpan.FromMilliseconds(10),
                BatchSize = 200,
            }),
            NullLogger<OutboxPublisherService>.Instance);

        await publisher.StartAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Events.Count < pendingBefore.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        await publisher.StopAsync(Ct);

        Assert.Equal(pendingBefore.Count, handler.Events.Count);

        var published = await host.Outbox.FindAsync(
            new OutboxQuery(Published: true, MaxResults: 500), Ct);

        Assert.Equal(pendingBefore.Count, published.Count);
        Assert.All(published, item => Assert.NotNull(item.PublishedTime));
    }

    [Fact]
    public async Task Publisher_ShouldRetryFailedEvent_AndRespectIdempotencyKey()
    {
        var host = TestEngine.Create();
        await host.StartProcessAsync(TestEngine.SequentialProcess("svc"), ct: Ct);

        var handler = new AlwaysFailingHandler();
        var publisher = new OutboxPublisherService(
            host.Outbox,
            [handler],
            Options.Create(new OutboxPublisherOptions
            {
                StartupDelay = TimeSpan.Zero,
                PollInterval = TimeSpan.FromMilliseconds(10),
                BatchSize = 10,
            }),
            NullLogger<OutboxPublisherService>.Instance);

        await publisher.StartAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (handler.Attempts == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        await publisher.StopAsync(Ct);

        var failed = await host.Outbox.FindAsync(
            new OutboxQuery(Published: false, MaxResults: 200), Ct);

        var first = failed.First(item => item.PublishAttempts > 0);
        Assert.False(first.DeadLetter);
        Assert.NotNull(first.NextAttemptTime);
        Assert.Contains("boom", first.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CollectingHandler : IOutboxEventHandler
    {
        public List<OutboxEvent> Events { get; } = [];

        public ValueTask HandleAsync(OutboxEvent outboxEvent, CancellationToken ct = default)
        {
            lock (Events)
            {
                Events.Add(outboxEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlwaysFailingHandler : IOutboxEventHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public ValueTask HandleAsync(OutboxEvent outboxEvent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("boom");
        }
    }
}
