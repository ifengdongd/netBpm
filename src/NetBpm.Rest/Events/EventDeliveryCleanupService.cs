using NetBpm.Engine.Events;

namespace NetBpm.Rest.Events;

/// <summary>
/// 事件投递的保留策略：<c>nb_event_delivery</c> 只增不减会持续膨胀（粒度是「一条事件 × 一个订阅」，
/// 一条事件发给三个订阅就是三行），这里按 <c>NetBpm:Events:DeliveryRetentionDays</c>
/// （默认 0 = 关闭）定期删除<b>已终结</b>的投递记录。
/// <para>
/// 只删终结态：<see cref="EventDeliveryStatus.Delivered"/> 按 <c>delivered_at</c>、
/// <see cref="EventDeliveryStatus.DeadLetter"/> 按 <c>updated_at</c>；
/// <see cref="EventDeliveryStatus.Pending"/> / <see cref="EventDeliveryStatus.Failed"/>
/// 一律不动 —— 它们还在重试链路上。
/// </para>
/// <para>
/// ⚠️ 死信同时是运维的人工重投队列，保留期到点等于「放弃这条投递」。
/// 因此默认关闭，且该值应当 ≥ 运维实际响应窗口；开启后每轮会把「按状态清理了多少条」写进日志，
/// 死信被清掉这件事有据可查。
/// </para>
/// <para>
/// 节奏与审计 / 归档清理一致：启动后先等 5 分钟（避开迁移 / 健康检查），之后每 6 小时一次；
/// 清理失败只告警，不影响宿主存活。
/// </para>
/// </summary>
public sealed class EventDeliveryCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<EventDeliveryCleanupService> logger) : BackgroundService
{
    /// <summary>保留天数配置项；<c>0</c>（默认）表示关闭自动清理。</summary>
    public const string RetentionDaysKey = "NetBpm:Events:DeliveryRetentionDays";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue(RetentionDaysKey, 0);

        if (retentionDays <= 0)
        {
            logger.LogInformation(
                "未启用事件投递保留策略（{Key} <= 0），投递记录将永久保留。", RetentionDaysKey);

            return;
        }

        logger.LogInformation(
            "事件投递保留策略已启用：清理 {Days} 天前已终结（成功 / 死信）的投递记录，每 {Hours} 小时一次。",
            retentionDays,
            Interval.TotalHours);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IEventDeliveryStore>();
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                var removed = await store.PruneAsync(cutoff, stoppingToken).ConfigureAwait(false);

                if (removed > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 条早于 {Cutoff:O} 的已终结事件投递记录（成功 + 死信）。",
                        removed,
                        cutoff);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "事件投递记录清理失败，下一周期重试。");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
