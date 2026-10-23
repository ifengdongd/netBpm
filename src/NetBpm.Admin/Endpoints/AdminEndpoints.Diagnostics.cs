using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Hosting.JobQueue;

namespace NetBpm.Admin.Endpoints;

/// <summary>
/// 管理端端点（partial）：运维诊断（Redis 作业队列）。
/// 路由表见 <see cref="AdminEndpoints.MapAdminEndpoints"/>。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// Redis 作业队列诊断：队列实现、目标地址、键前缀、PING 时延、三个队列键的条目数、服务端 INFO 摘要。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只读：不写入、不消费、不改配置。实现是 <c>Memory</c> 时如实返回
    /// <c>provider = memory</c>，而不是伪造一个「空的 Redis 连接」——「看不到 netbpm 的键」
    /// 有一半原因就是配置没生效，页面上必须能一眼看出来。
    /// </para>
    /// <para>
    /// 口令永不回传：连接串只以 <c>usesConnectionString</c> 布尔标记体现，目标地址取解析出的端点。
    /// </para>
    /// </remarks>
    private static async Task<AdminRedisInfoResponse> GetRedisInfoAsync(
        IJobQueue queue,
        IOptions<RedisJobQueueOptions> queueOptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(queueOptions);

        var options = queueOptions.Value;
        var checkedAt = DateTime.UtcNow;

        if (queue is not RedisJobQueue redisQueue)
        {
            return new AdminRedisInfoResponse(
                Provider: queue.Name,
                IsRedis: false,
                Target: null,
                Database: null,
                KeyPrefix: null,
                Ssl: null,
                UsesConnectionString: false,
                CheckedAt: checkedAt,
                Reachable: false,
                LatencyMs: null,
                Error: "当前宿主的作业队列实现不是 Redis（NetBpm:Jobs:Queue:Provider）。"
                    + "改成 Redis 后重启宿主，队列键才会出现在对应逻辑库里。",
                Keys: [],
                Server: null);
        }

        var snapshot = await redisQueue.InspectAsync(ct).ConfigureAwait(false);

        return new AdminRedisInfoResponse(
            Provider: redisQueue.Name,
            IsRedis: true,
            Target: RedisJobQueue.DescribeTarget(options),
            // 连不上时不给猜测值：连接串模式下库号只有连上才知道
            Database: snapshot.Reachable ? snapshot.Database : null,
            KeyPrefix: options.KeyPrefix,
            Ssl: options.Ssl,
            UsesConnectionString: !string.IsNullOrWhiteSpace(options.ConnectionString),
            CheckedAt: checkedAt,
            Reachable: snapshot.Reachable,
            LatencyMs: snapshot.LatencyMs,
            Error: snapshot.Error,
            Keys: snapshot.Keys,
            Server: snapshot.Server);
    }
}
