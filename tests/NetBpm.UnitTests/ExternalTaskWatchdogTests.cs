using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Diagnostics;
using NetBpm.Runtime.Hosting;

namespace NetBpm.UnitTests;

/// <summary>
/// 外部任务巡检（<c>external_task.no_worker</c> / <c>external_task.dead_letter</c>）。
/// </summary>
/// <remarks>
/// 「没人来领」是唯一一类不会体现在任何响应码上的故障：fetch 只会正常返回空列表，
/// 死信也不会让谁报错。这里锁住巡检能把它变成一条告警，并且<b>不会把告警通道刷爆</b>。
/// </remarks>
public sealed class ExternalTaskWatchdogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scan_ShouldAlertTask_NobodyPickedUp()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        await broker.CreateAsync(Starving("task-1", createdMinutesAgo: 30), Ct);

        await RunOnceAsync(broker, alerts);

        var alert = Assert.Single(alerts.Alerts);

        Assert.Equal(AlertCodes.ExternalTaskNoWorker, alert.Code);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("task-1", alert.Subject);
        Assert.Equal("acme", alert.Data!["topic"]);
    }

    [Fact]
    public async Task Scan_ShouldNotAlert_BeforeThreshold()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        // 刚登记 10 秒：worker 批量轮询，头几秒没人领完全正常
        await broker.CreateAsync(Starving("task-2", createdMinutesAgo: 0), Ct);

        await RunOnceAsync(broker, alerts);

        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task Scan_ShouldNotAlert_WhenWorkerOnceClaimedIt()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        // 被领过（失败退避中）的任务走死信告警通道，不该混进「没人领」
        var task = Starving("task-3", createdMinutesAgo: 30);
        task.ErrorMessage = "worker 崩了";

        await broker.CreateAsync(task, Ct);

        await RunOnceAsync(broker, alerts);

        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task Scan_ShouldAlertOncePerCooldown_NotOncePerPoll()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        await broker.CreateAsync(Starving("task-4", createdMinutesAgo: 30), Ct);

        var service = BuildService(broker, alerts, new ExternalTaskWatchdogOptions
        {
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20),
            NoWorkerThreshold = TimeSpan.FromMinutes(5),
            AlertCooldown = TimeSpan.FromMinutes(30),
        });

        await service.StartAsync(Ct);
        await Task.Delay(250, Ct);
        await service.StopAsync(Ct);

        // 一条卡死的任务每轮都报会把告警通道淹掉，反而让人看不到真正的新问题
        Assert.True(alerts.Alerts.Count >= 1, "至少要告警一次。");
        Assert.Equal(1, alerts.Alerts.Count);
    }

    [Fact]
    public async Task Scan_ShouldAlertDeadLetter()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        var task = Starving("task-5", createdMinutesAgo: 30);
        task.Status = ExternalTaskStatus.DeadLetter;
        task.ErrorMessage = "重试耗尽";

        await broker.CreateAsync(task, Ct);

        await RunOnceAsync(broker, alerts);

        var alert = Assert.Single(alerts.Alerts);

        Assert.Equal(AlertCodes.ExternalTaskDeadLetter, alert.Code);
    }

    [Fact]
    public async Task Scan_ShouldStayQuiet_WhenDisabled()
    {
        var broker = new InMemoryExternalTaskBroker();
        var alerts = new InMemoryAlertSink();

        await broker.CreateAsync(Starving("task-6", createdMinutesAgo: 30), Ct);

        var service = BuildService(broker, alerts, new ExternalTaskWatchdogOptions
        {
            Enabled = false,
            StartupDelay = TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(20),
        });

        await service.StartAsync(Ct);
        await Task.Delay(120, Ct);
        await service.StopAsync(Ct);

        Assert.Empty(alerts.Alerts);
    }

    private static async Task RunOnceAsync(
        IExternalTaskBroker broker,
        IAlertSink sink,
        ExternalTaskWatchdogOptions? options = null)
    {
        var service = BuildService(
            broker,
            sink,
            options ?? new ExternalTaskWatchdogOptions
            {
                StartupDelay = TimeSpan.Zero,
                PollInterval = TimeSpan.FromSeconds(30),
                NoWorkerThreshold = TimeSpan.FromMinutes(5),
                AlertCooldown = TimeSpan.FromMinutes(15),
            });

        await service.StartAsync(Ct);
        await Task.Delay(80, Ct);
        await service.StopAsync(Ct);
    }

    private static ExternalTaskWatchdogService BuildService(
        IExternalTaskBroker broker,
        IAlertSink sink,
        ExternalTaskWatchdogOptions options) =>
        new(
            broker,
            [sink],
            Options.Create(options),
            NullLogger<ExternalTaskWatchdogService>.Instance);

    private static ExternalTask Starving(string id, int createdMinutesAgo) => new()
    {
        Id = id,
        ProcessInstanceId = "inst-1",
        ExecutionId = "exec-1",
        ActivityId = "svc_invoice",
        Topic = "acme",
        TenantId = "default",
        Status = ExternalTaskStatus.Pending,
        CreatedAt = DateTime.UtcNow.AddMinutes(-createdMinutesAgo),
        Retries = 3,
    };
}
