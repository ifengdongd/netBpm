using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Hosting.JobQueue;

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
    private readonly IJobQueue _queue;
    private readonly ILogger<AsyncJobExecutorService> _logger;

    public AsyncJobExecutorService(
        IProcessEngine engine,
        IOptions<AsyncJobExecutorOptions> options,
        IJobQueue queue,
        ILogger<AsyncJobExecutorService> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(logger);

        _engine = engine;
        _options = options.Value;
        _queue = queue;
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

        _logger.LogInformation(
            "异步作业执行器已启动：队列实现 = {Queue}，批次 = {BatchSize}，基准间隔 = {PollInterval}。",
            _queue.Name,
            batchSize,
            _options.PollInterval);

        // 自适应退避：连续空转就按 2 倍递增，直到上限；一旦有作业执行立刻回落到基准间隔。
        // 目的：空闲库不做无意义的高频轮询（原固定 2s），忙碌库则满速消费。
        var idleRounds = 0;
        var maxInterval = _options.MaxPollInterval < _options.PollInterval
            ? _options.PollInterval
            : _options.MaxPollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            var executed = 0;

            try
            {
                // 队列只给「作业 id + 到期时间」的提示：取到后仍回数据库抢占租约再执行。
                // 因此队列重复 / 提前投递都不会造成重复执行 —— 数据库租约是唯一权威。
                var candidates = await _queue
                    .TakeDueAsync(batchSize, CurrentInterval(idleRounds), stoppingToken)
                    .ConfigureAwait(false);

                executed = candidates.Count == 0
                    // 队列里没有任何提示（进程刚重启 / Redis 未持久化）：兜底扫一次库，
                    // 保证「只靠数据库也能把活干完」。
                    ? await _engine.ExecuteDueAsyncJobsAsync(DateTime.UtcNow, batchSize, stoppingToken)
                        .ConfigureAwait(false)
                    : await _engine.ExecuteAsyncJobsByIdAsync(
                            candidates.Select(item => item.JobId).ToList(),
                            DateTime.UtcNow,
                            stoppingToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 数据库暂时不可用等：记录后继续下一轮，不能因为一次异常让执行器永久停摆。
                // 出错也计入空转，从而在故障期间自动退避，避免高频重试把数据库打得更惨。
                _logger.LogError(exception, "异步作业轮询失败，将在下一轮重试。");
            }

            idleRounds = executed > 0 ? 0 : idleRounds + 1;
        }
    }

    private TimeSpan CurrentInterval(int idleRounds)
    {
        var maxInterval = _options.MaxPollInterval < _options.PollInterval
            ? _options.PollInterval
            : _options.MaxPollInterval;

        var interval = _options.PollInterval * Math.Pow(2, Math.Min(idleRounds, 8));

        return interval > maxInterval ? maxInterval : interval;
    }
}
