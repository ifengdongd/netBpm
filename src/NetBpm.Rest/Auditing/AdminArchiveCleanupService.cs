using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetBpm.Persistence.Admin;
using NetBpm.Persistence.Platform;

namespace NetBpm.Rest.Auditing;

/// <summary>
/// 归档类数据的保留策略后台服务：定期清理
/// <c>admin_history_archive</c>（按 <c>archived_at</c>）、
/// <c>nb_approval_snapshot</c>（按 <c>captured_at</c>）与
/// <c>nb_parameter_snapshot</c>（按 <c>updated_at</c>），三者共用
/// <c>NetBpm:Admin:ArchiveRetentionDays</c> 一个配置。
/// <para>
/// 归档表只进不出会无限膨胀（payload 是 5 张 ACT_HI_* 行的 JSON 快照，单条可观）；
/// 保留策略与审计清理（<see cref="AdminAuditCleanupService"/>）同款节奏，
/// <c>0</c>（默认）表示关闭自动清理。
/// </para>
/// <para>
/// 🔑 <b>为什么快照必须跟归档同一个保留期</b>：快照的唯一用途是解释「这张单当时怎么填的」，
/// 它靠 <c>task_id</c> 与历史行对齐。历史行没了、快照还在，就只剩一堆谁也定位不到的孤儿数据；
/// 反过来历史还在、快照先没了，历史页就退化成「有结论、没有内容」。
/// 两份保留期各配一个键，最终一定是两边被人分别调整到错位——
/// 所以这里刻意<b>只读一个键</b>，要改就一起改。
/// </para>
/// <para>
/// ⚠️ 与「显式删除实例」不冲突：删实例不级联删快照（快照表刻意无外键），
/// 只有这里的保留期会真正抹掉它。因此该值应当 ≥ 审计/合规要求的年限。
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
                "未启用归档保留策略（{Key} <= 0），历史归档与表单快照将永久保留。", RetentionDaysKey);

            return;
        }

        logger.LogInformation(
            "归档保留策略已启用：保留最近 {Days} 天，每 {Hours} 小时清理一次（历史归档 + 表单快照）。",
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
                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

                var archiveStore = scope.ServiceProvider.GetRequiredService<IHistoryArchiveStore>();
                var removed = await archiveStore.PruneAsync(cutoff, stoppingToken).ConfigureAwait(false);

                if (removed > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 条早于 {Cutoff:O} 的历史归档记录。", removed, cutoff);
                }

                // 快照与归档同一个 cutoff：两张表要么一起有解释力，要么一起过期
                var snapshotStore = scope.ServiceProvider.GetRequiredService<IApprovalSnapshotStore>();
                var prunedSnapshots = await snapshotStore.PruneAsync(cutoff, stoppingToken).ConfigureAwait(false);

                if (prunedSnapshots > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 份早于 {Cutoff:O} 的表单快照。", prunedSnapshots, cutoff);
                }

                // 参数修订历史与归档同一切点：它解释的也是「这张单当时怎么走的」，
                // 归档没了它留着就是一串无法定位的孤儿值
                var parameterStore = scope.ServiceProvider.GetRequiredService<IParameterSnapshotStore>();
                var prunedParameters = await parameterStore.PruneAsync(cutoff, stoppingToken).ConfigureAwait(false);

                if (prunedParameters > 0)
                {
                    logger.LogInformation(
                        "已清理 {Count} 版早于 {Cutoff:O} 的参数修订。", prunedParameters, cutoff);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "归档保留策略清理失败，下一周期重试。");
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
