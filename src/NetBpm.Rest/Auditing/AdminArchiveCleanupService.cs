using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Admin;

namespace NetBpm.Rest.Auditing;

/// <summary>
/// 历史归档保留策略后台服务：定期删除 <c>admin_history_archive</c> 中
/// 归档时间早于 <c>NetBpm:Admin:ArchiveRetentionDays</c> 的记录。
/// <para>
/// 归档表只进不出会无限膨胀（payload 是 5 张 ACT_HI_* 行的 JSON 快照，单条可观）；
/// 保留策略与审计清理（<see cref="AdminAuditCleanupService"/>）同款节奏，
/// <c>0</c>（默认）表示关闭自动清理。
/// </para>
/// </summary>
public sealed class AdminArchiveCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AdminArchiveCleanupService> logger) : BackgroundService
{
    /// <summary>保留天数配置项；<c>0</c>（默认）表示关闭自动清理。</summary>
    public const string RetentionDaysKey = "NetBpm:Admin:ArchiveRetentionDays";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue(RetentionDaysKey, 0);

        if (retentionDays <= 0)
        {
            logger.LogInformation(
                "未启用归档保留策略（{Key} <= 0），归档记录将永久保留。", RetentionDaysKey);

            return;
        }

        logger.LogInformation(
            "归档保留策略已启用：保留最近 {Days} 天，每 {Hours} 小时清理一次。",
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
                var store = scope.ServiceProvider.GetRequiredService<IHistoryArchiveStore>();
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                var removed = await store.PruneAsync(cutoff, stoppingToken).ConfigureAwait(false);

                if (removed > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 条早于 {Cutoff:O} 的历史归档记录。", removed, cutoff);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "历史归档清理失败，下一周期重试。");
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
