using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetBpm.Runtime.Hosting;

/// <summary>
/// 异步作业执行器：后台轮询 <c>ACT_RU_JOB</c>，逐条在自己的命令/事务里推进流程。
/// <para>
/// 多宿主可以同时运行：每条作业通过「条件删除 + 影响行数」抢占，只有一个实例能执行。
/// 单条失败只影响该条（按退避重试，超过上限进死信），不会中断整批。
/// </para>
/// </summary>
public sealed class AsyncJobExecutorService : BackgroundService
{
    private readonly IProcessEngine _engine;
    private readonly AsyncJobExecutorOptions _options;
    private readonly ILogger<AsyncJobExecutorService> _logger;

    public AsyncJobExecutorService(
        IProcessEngine engine,
        IOptions<AsyncJobExecutorOptions> options,
        ILogger<AsyncJobExecutorService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("异步作业执行器已禁用（NetBpm:Jobs:Enabled=false）。");
            return;
        }

        await Task.Delay(_options.StartupDelay, stoppingToken).ConfigureAwait(false);

        var batchSize = Math.Max(1, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var executed = 0;

            try
            {
                executed = await _engine
                    .ExecuteDueAsyncJobsAsync(DateTime.UtcNow, batchSize, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 数据库暂时不可用等：记录后继续下一轮，不能因为一次异常让执行器永久停摆
                _logger.LogError(exception, "异步作业轮询失败，将在下一轮重试。");
            }

            // 本轮跑满批次说明大概率还有积压，立即继续；否则按轮询间隔休眠
            if (executed < batchSize)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
