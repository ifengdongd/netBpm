using NetBpm.Persistence.Admin;

namespace NetBpm.Rest.Auditing;

/// <summary>
/// 审计保留策略：<c>admin_audit_log</c> 只增不删会持续膨胀，这里按
/// <c>NetBpm:Admin:AuditRetentionDays</c>（默认 0 = 关闭）定期删除过期记录。
/// <para>
/// 容错口径与 <see cref="IdempotencyCleanupService"/> 一致：启动后先等 5 分钟（避开迁移 / 健康检查），
/// 之后每 6 小时一次；清理失败只告警，不影响宿主存活。
/// </para>
/// <para>
/// 手工清理入口：<c>DELETE /api/admin/audit?before=...</c>（需要 <c>netbpm.admin.audit.purge</c>）。
/// </para>
/// </summary>
public sealed class AdminAuditCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AdminAuditCleanupService> logger) : BackgroundService
{
    /// <summary>保留天数配置项；<c>0</c>（默认）表示关闭自动清理。</summary>
    public const string RetentionDaysKey = "NetBpm:Admin:AuditRetentionDays";

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue(RetentionDaysKey, 0);

        if (retentionDays <= 0)
        {
            logger.LogInformation(
                "未启用审计保留策略（{Key} <= 0），审计记录将永久保留。", RetentionDaysKey);

            return;
        }

        logger.LogInformation(
            "审计保留策略已启用：保留最近 {Days} 天，每 {Hours} 小时清理一次。",
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
                var store = scope.ServiceProvider.GetRequiredService<IAdminAuditStore>();
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                // tenantId = null：保留策略是系统级口径，跨租户统一生效
                var removed = await store.PurgeAsync(null, cutoff, stoppingToken).ConfigureAwait(false);

                if (removed > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 条早于 {Cutoff:O} 的审计记录。", removed, cutoff);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "审计记录清理失败，下一周期重试。");
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
