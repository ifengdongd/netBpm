using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;

namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// 启动自检：<c>NetBpm:Jobs:Queue:Provider=Redis</c> 时确认作业提示队列<b>真的连得上</b>，
/// 连不上打一条 Error。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：<see cref="RedisJobQueue"/> 刻意把连不上 Redis 处理成<b>静默降级</b>
/// （<c>AbortOnConnectFail = false</c> + 异常吞掉记警告，作业靠扫库兜底照常执行）。
/// 功能不受影响，但「配了 Redis、实际一直没连上」这种配置错误只会在日志里留下零星 Warning，
/// 运维看到的却是「Redis db 里什么都没有」。这里把这种情况提升为启动期 Error。
/// </para>
/// <para>
/// 刻意<b>不阻塞宿主启动</b>：作为 <see cref="BackgroundService"/> 运行，宿主先起来再接流量，
/// 探测在自己的任务里做；任何失败只记日志、不抛异常（不能因为 Redis 挂了让宿主起不来）。
/// </para>
/// <para>
/// 只探一次，不周期重试：Redis 运行期掉线属于运行时故障，由 <see cref="RedisJobQueue"/>
/// 的 <c>ConnectionFailed</c> 警告持续暴露；启动期反复重试只会刷日志。
/// 探测只用 <c>PING</c>，不写任何键。
/// </para>
/// </remarks>
public sealed class RedisJobQueueStartupProbe : BackgroundService
{
    /// <summary>探测次数：首次失败后再给一次机会，避开「建连还没完成」的瞬时状态。</summary>
    private const int Attempts = 2;

    /// <summary>两次探测之间的间隔。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>单次探测的宽限：建连超时之上再留一点余量给 PING 往返（PING 本身是微秒级）。</summary>
    private static readonly TimeSpan ProbeGrace = TimeSpan.FromSeconds(2);

    private readonly IJobQueue _queue;
    private readonly RedisJobQueueOptions _options;
    private readonly ILogger<RedisJobQueueStartupProbe> _logger;

    public RedisJobQueueStartupProbe(
        IJobQueue queue,
        IOptions<RedisJobQueueOptions> options,
        ILogger<RedisJobQueueStartupProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>探测目标（诊断用）：尽量写清「连的是哪儿」，让 Error 日志本身可定位。</summary>
    /// <remarks>与 <see cref="RedisJobQueue.DescribeTarget"/> 同源：口令永远不会出现在日志里。</remarks>
    private string Target => string.IsNullOrWhiteSpace(_options.ConnectionString)
        ? $"{RedisJobQueue.DescribeTarget(_options)} / db{_options.Database}"
        : $"{RedisJobQueue.DescribeTarget(_options)}（ConnectionString 指定，Database 配置项被忽略）";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 宿主自行注册了其它 IJobQueue（TryAdd 未覆盖）时，没有 Redis 可探。
        if (_queue is not RedisJobQueue queue)
        {
            return;
        }

        var timeout = _options.ConnectTimeout + ProbeGrace;
        Exception? failure = null;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            attemptTimeout.CancelAfter(timeout);

            try
            {
                var (database, latency) = await queue.PingAsync(attemptTimeout.Token).ConfigureAwait(false);

                _logger.LogInformation(
                    "Redis 作业队列连接正常：{Target}（实际库 db{Database}），PING 往返 {Latency:0.0} ms。",
                    Target,
                    database,
                    latency.TotalMilliseconds);

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 宿主在探测窗口内停机（端口冲突 / 快速重启 / Ctrl+C）：正常退出。
                return;
            }
            catch (OperationCanceledException exception)
            {
                failure = new TimeoutException(
                    $"PING 在 {timeout.TotalSeconds:0.#}s 内没有返回。",
                    exception);
            }
            catch (Exception exception)
            {
                // Redis 连不上 / 口令错 / 库号非法：只记录，绝不影响宿主启动。
                failure = exception;
            }

            if (attempt < Attempts)
            {
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        _logger.LogError(
            failure,
            "Redis 作业队列连接不可用：{Target}（配置节 NetBpm:Jobs:Queue:Redis，重试 {Attempts} 次）。" +
            "异步作业仍会由数据库兜底扫库执行，但「到期即时唤醒」与「多宿主共享队列」已经失效；" +
            "请检查 Redis 是否已启动、Host / Port / Auth / Database（或 ConnectionString）是否正确、网络与防火墙是否放通。",
            Target,
            Attempts);
    }
}
