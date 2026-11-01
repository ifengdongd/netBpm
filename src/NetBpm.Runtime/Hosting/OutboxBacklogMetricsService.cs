using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Events;

namespace NetBpm.Runtime.Hosting;

/// <summary>
/// outbox 积压采样选项（<c>NetBpm:Outbox:Metrics</c>）。
/// </summary>
public sealed class OutboxBacklogMetricsOptions
{
    /// <summary>是否启用采样（默认开）。关掉则积压指标不再更新（<c>backlog_stale</c> 会一直是 1）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动后先缓一缓（默认 10 秒）：宿主刚起来时连接池还在预热，别让第一次失败进日志。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 采样间隔（默认 15 秒）。
    /// <para>
    /// 一轮两次 <c>count(*)</c> + 最坏一次取最老的一行，成本很低；
    /// 但也别压到秒级 —— 积压是个「分钟级才需要反应」的量。
    /// </para>
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// outbox 积压采样：把「还有多少条没发出去 / 最老的躺了多久」变成可抓取的指标。
/// <para>
/// 为什么需要它：<b>发布链路断了不会报错给任何人。</b>worker 全挂、订阅方 500、
/// 限速把整批事件无限往后推 —— 这些场景下接口照常返回 200，流程照常往下走，
/// 只有 <c>ACT_EVT_OUTBOX</c> 在悄悄变长。此前积压只能靠
/// <c>GET /admin/v1/events/stats</c> 人工查看，Prometheus 抓不到，也就没法告警。
/// </para>
/// <para>
/// 为什么是「采样 + 缓存」而不是在指标回调里直接查库：OTel 的观测式 Gauge
/// 是同步回调，抓取方（Prometheus）决定它多久跑一次；把数据库往返放进去，
/// 等于把查询频率交给外部配置，还会阻塞抓取线程。这里按自己的节奏查，
/// 结果写进 <see cref="OutboxBacklogProbe"/>，回调只读内存。
/// </para>
/// <para>
/// 🔑 <b>采样失败绝不拖垮宿主</b>：指标是观测手段，不是业务链路。
/// 一轮失败只记日志、保留上次的值（并因超时被判为 stale），下一轮继续。
/// </para>
/// </summary>
public sealed class OutboxBacklogMetricsService : BackgroundService
{
    private readonly IEventOutboxStore _store;
    private readonly OutboxBacklogMetricsOptions _options;
    private readonly ILogger<OutboxBacklogMetricsService> _logger;

    public OutboxBacklogMetricsService(
        IEventOutboxStore store,
        IOptions<OutboxBacklogMetricsOptions> options,
        ILogger<OutboxBacklogMetricsService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("outbox 积压采样已禁用（NetBpm:Outbox:Metrics:Enabled=false）。");
            return;
        }

        var interval = _options.Interval > TimeSpan.Zero ? _options.Interval : TimeSpan.FromSeconds(15);

        try
        {
            if (_options.StartupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.StartupDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleOnceAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 保留上一次的值：它会被判为 stale，看板与告警据此知道「数据不可信」，
                // 比直接清零（看起来像「没有积压」）诚实。
                _logger.LogError(exception, "outbox 积压采样失败，将在下一轮重试。");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 停机：本进程不再采样，把快照标成过期，避免别的看板继续拿旧数字当现状
        OutboxBacklogProbe.MarkStale();
    }

    /// <summary>
    /// 采样一轮并写入 <see cref="OutboxBacklogProbe"/>（供测试与手工触发）。
    /// </summary>
    /// <param name="interval">本次使用的采样间隔（决定 stale 判定窗口）。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask SampleOnceAsync(TimeSpan interval, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var pending = await _store
            .CountAsync(new OutboxQuery(Published: false, DeadLetter: false), ct)
            .ConfigureAwait(false);

        var deadLetter = await _store
            .CountAsync(new OutboxQuery(Published: false, DeadLetter: true), ct)
            .ConfigureAwait(false);

        // 只有在确实有积压时才去取最老的一条：没有积压就省掉这次查询
        var ageSeconds = 0d;

        if (pending > 0)
        {
            var oldest = await _store
                .FindAsync(new OutboxQuery(Published: false, DeadLetter: false, MaxResults: 1), ct)
                .ConfigureAwait(false);

            if (oldest.Count > 0 && oldest[0].CreateTime != default)
            {
                // 数据库读回来通常是 Unspecified，按 UTC 解释（写入时就是 UTC）
                var created = DateTime.SpecifyKind(oldest[0].CreateTime, DateTimeKind.Utc);
                var age = now - created;

                // 时钟回拨 / 未来时间戳不该让指标变成负数，那会让「大于」类告警整体失灵
                ageSeconds = age > TimeSpan.Zero ? age.TotalSeconds : 0;
            }
        }

        OutboxBacklogProbe.Record(pending, deadLetter, ageSeconds, interval);
    }
}
