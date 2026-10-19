using NetBpm.Persistence.Idempotency;

namespace NetBpm.Rest;

/// <summary>
/// 幂等记录清理：<c>ACT_IDEMPOTENCY</c> 只增不减会持续膨胀，这里按记录自带的过期时间定期删除。
/// <para>
/// 启动后先等一分钟再首轮执行（避免与迁移 / 健康检查抢资源），之后每小时一次；
/// 清理失败只告警，不影响宿主存活。
/// </para>
/// <para>内存实现同样可用：<see cref="InMemoryIdempotencyStore.PurgeExpiredAsync"/> 清理字典项。</para>
/// </summary>
public sealed class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
                var removed = await store.PurgeExpiredAsync(DateTime.UtcNow, stoppingToken)
                    .ConfigureAwait(false);

                if (removed > 0)
                {
                    logger.LogInformation("已清理 {Count} 条过期幂等记录。", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "幂等记录清理失败，下一周期重试。");
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
