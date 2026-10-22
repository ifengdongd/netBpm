using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetBpm.Runtime;

namespace NetBpm.Workflow.Hosting;

/// <summary>定时作业轮询配置（对应 <c>NetBpm:Timer</c> 配置节）。</summary>
public sealed class TimerJobSchedulerOptions
{
    /// <summary>是否启用轮询（多宿主部署时建议只在<b>一个</b>宿主启用，或依赖作业的条件删除抢占）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔（秒）。默认 5 秒：审批流的"超时"通常以小时计，不需要秒级精度。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>每轮最多处理的作业数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// 配置自检结论（由配置绑定器写入，宿主一般不用管）：非空时轮询服务启动后会打一条告警。
    /// </summary>
    public string? ConfigWarning { get; set; }
}

/// <summary>
/// 定时作业轮询宿主服务。
/// <para>
/// 每轮调用 <see cref="IProcessEngine.TriggerDueTimersAsync"/>；每条作业一个独立命令（独立事务），
/// 单条失败只记日志，不影响其它作业，也不会让后台服务退出。
/// </para>
/// <para>
/// <b>多宿主</b>：作业以条件删除抢占，多个宿主同时轮询不会重复触发；
/// 但建议仍只在一个宿主启用，减少无效查询。
/// </para>
/// </summary>
public sealed class TimerJobSchedulerService : BackgroundService
{
    private readonly IProcessEngine _engine;
    private readonly TimerJobSchedulerOptions _options;
    private readonly ILogger<TimerJobSchedulerService> _logger;

    public TimerJobSchedulerService(
        IProcessEngine engine,
        TimerJobSchedulerOptions options,
        ILogger<TimerJobSchedulerService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("定时作业轮询已禁用（NetBpm:Timer:Enabled = false）。");
            return;
        }

        var interval = _options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : _options.PollInterval;

        _logger.LogInformation("定时作业轮询已启动：间隔 {Interval}，每轮最多 {BatchSize} 条。", interval, _options.BatchSize);

        if (!string.IsNullOrWhiteSpace(_options.ConfigWarning))
        {
            _logger.LogWarning("{Warning}", _options.ConfigWarning);
        }

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                var fired = await _engine
                    .TriggerDueTimersAsync(DateTime.UtcNow, _options.BatchSize, stoppingToken)
                    .ConfigureAwait(false);

                if (fired > 0)
                {
                    _logger.LogInformation("已触发 {Count} 条到期的边界定时器。", fired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 一条坏作业 / 一次数据库抖动不应该让轮询服务停摆
                _logger.LogError(exception, "定时作业轮询失败，将在下一个周期重试。");
            }
        }
    }
}

/// <summary>定时作业轮询的注册入口。</summary>
public static class TimerJobSchedulerExtensions
{
    /// <summary>注册定时作业轮询（<c>NetBpm:Timer</c> 配置节）。</summary>
    public static IServiceCollection AddNetBpmTimerScheduler(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new TimerJobSchedulerOptions();
        var section = configuration.GetSection("NetBpm:Timer");

        if (bool.TryParse(section["Enabled"], out var enabled))
        {
            options.Enabled = enabled;
        }

        if (int.TryParse(section["PollIntervalMs"], out var intervalMs) && intervalMs > 0)
        {
            options.PollInterval = TimeSpan.FromMilliseconds(intervalMs);
            options.ConfigWarning = DescribeIntervalRisk(intervalMs);
        }

        if (int.TryParse(section["BatchSize"], out var batchSize) && batchSize > 0)
        {
            options.BatchSize = batchSize;
        }

        services.AddSingleton(options);
        services.AddHostedService<TimerJobSchedulerService>();

        return services;
    }

    /// <summary>
    /// 单位误配自检：<c>Timer:PollIntervalMs</c> 与 <c>Jobs:PollIntervalSeconds</c> 单位不同，
    /// 把秒数填进毫秒位会让超时定时器迟发几十分钟（表现为"超时审批永远不触发"），
    /// 把毫秒填进秒位则变成高频空转（每轮一次全表查询）。
    /// </summary>
    private static string? DescribeIntervalRisk(int intervalMs) => intervalMs switch
    {
        < 200 => $"NetBpm:Timer:PollIntervalMs={intervalMs} 小于 200ms：轮询会非常密集，确认不是把秒数填进了毫秒位。",
        >= 600_000 => $"NetBpm:Timer:PollIntervalMs={intervalMs}（约 {intervalMs / 1000} 秒）：边界定时器会迟发这么久，确认不是把毫秒填进了秒位。",
        _ => null,
    };

    /// <summary>注册定时作业轮询（代码配置，测试 / 自定义宿主用）。</summary>
    public static IServiceCollection AddNetBpmTimerScheduler(
        this IServiceCollection services,
        Action<TimerJobSchedulerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TimerJobSchedulerOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddHostedService<TimerJobSchedulerService>();

        return services;
    }
}
