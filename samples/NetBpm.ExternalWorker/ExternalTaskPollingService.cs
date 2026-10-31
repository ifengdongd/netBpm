using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetBpm.ExternalWorker;

/// <summary>
/// worker 主循环：领 → 做 → 回执。
/// </summary>
/// <remarks>
/// <para><b>这个进程里不存在重试</b>：退避、重试计数、死信全在服务端。
/// worker 的职责边界是「把这次处理的结果如实回执」——成功就 complete，业务失败就 failure。
/// 如果 worker 自己攒重试，就会出现「worker 重试了 3 次」叠加「引擎重试 3 次」的乘法级别的重复执行。
/// </para>
/// <para>
/// 网络异常时不回执也不本地重做：放任锁到期让服务端回收，
/// 由别的 worker（或下一轮自己）重新领走。这比在本地「猜要不要重试」可靠得多。
/// </para>
/// </remarks>
public sealed class ExternalTaskPollingService : BackgroundService
{
    private readonly HttpClient _client;
    private readonly ExternalWorkerOptions _options;
    private readonly Dictionary<string, IExternalTaskHandler> _handlers;
    private readonly ILogger<ExternalTaskPollingService> _logger;

    /// <summary>构造。</summary>
    public ExternalTaskPollingService(
        HttpClient client,
        IOptions<ExternalWorkerOptions> options,
        IEnumerable<IExternalTaskHandler> handlers,
        ILogger<ExternalTaskPollingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlers);

        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _handlers = new Dictionary<string, IExternalTaskHandler>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            _handlers[handler.Topic] = handler;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "外部任务 worker 启动：workerId={WorkerId} topics={Topics} server={Server} tenant={Tenant}",
            _options.WorkerId,
            string.Join(",", _options.Topics),
            _options.NormalizedBaseUrl,
            _options.TenantId ?? "(不限)");

        // 并发闸门：一轮领到几条不代表能同时往下游压多少负载。
        // 真实的下游系统有自己的容量，worker 必须让这个上限可控。
        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tasks = await FetchAsync(stoppingToken).ConfigureAwait(false);

                if (tasks.Count == 0)
                {
                    // fetch 是服务端短轮询：请求会挂起到有任务或超时，因此这里不用自己 delay。
                    // 只有服务端不支持 waitSeconds 时才退化成「立刻返回空」，下面的 ||
                    // 分支就是为这种情况保底（否则会变成忙循环打满 CPU 与服务端）。
                    continue;
                }

                var pending = tasks
                    .Select(task => RunOneAsync(task, gate, stoppingToken))
                    .ToArray();

                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 服务端不可达是常态（滚动发布、网络分区）：退避后重试，别让 worker 退出
                _logger.LogWarning(
                    exception,
                    "拉取外部任务失败，{Backoff}ms 后重试：{Message}",
                    _options.ErrorBackoffMs,
                    exception.Message);

                try
                {
                    await Task.Delay(_options.ErrorBackoffMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("外部任务 worker 已停止：{WorkerId}", _options.WorkerId);
    }

    private async Task RunOneAsync(
        ExternalTaskDto task,
        SemaphoreSlim gate,
        CancellationToken stoppingToken)
    {
        await gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await ProcessAsync(task, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机中：不回执，让锁自然过期后被重新领取
            return;
        }
        catch (Exception exception)
        {
            // 兜底：连回执都失败了就不能假装无事发生。锁会过期，任务会被重新领走，
            // 因此「什么都不做」反而比重试一次更接近幂等。
            _logger.LogError(exception, "处理任务 {TaskId} 出现异常，等待服务端回收锁。", task.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessAsync(ExternalTaskDto task, CancellationToken ct)
    {
        _logger.LogInformation(
            "领到任务 {TaskId}（topic={Topic}，实例 {InstanceId}，剩余重试 {Retries}）",
            task.Id,
            task.Topic,
            task.ProcessInstanceId,
            task.Retries);

        if (!_handlers.TryGetValue(task.Topic, out var handler))
        {
            // 没接到的主题要主动报失败：让它快点按退避重投或进死信，
            // 而不是「领了却什么都不做」——那样要等锁过期才有人发现。
            await ReportFailureAsync(
                    task,
                    $"worker '{_options.WorkerId}' 未注册主题 '{task.Topic}' 的处理逻辑",
                    null,
                    null,
                    null,
                    ct)
                .ConfigureAwait(false);

            return;
        }

        Dictionary<string, object?> variables;

        try
        {
            variables = await handler.ExecuteAsync(task, ct).ConfigureAwait(false);

            using var complete = await CompleteAsync(task, variables, ct).ConfigureAwait(false);

            complete.EnsureSuccessStatusCode();

            _logger.LogInformation(
                "任务 {TaskId} 完成，写回变量：{Keys}",
                task.Id,
                string.Join(",", variables.Keys));
        }
        catch (ExternalTaskFailedException failure)
        {
            // 业务失败：扣重试额度交给服务端裁决（耗尽即死信，流程停在原地）
            await ReportFailureAsync(
                    task,
                    failure.Message,
                    failure.Details,
                    failure.RemainingRetries,
                    failure.RetryAfter,
                    ct)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "任务 {TaskId} 业务失败：{Message}（服务端决定是否重试）",
                task.Id,
                failure.Message);
        }
        catch (Exception exception)
        {
            // 非业务异常（NRE、反序列化失败）同样按失败回执上报：
            // 吞掉会让任务带着锁消失到超时为止，比立刻失败更难排查。
            await TryReportFailureAsync(task, exception.Message, exception.ToString(), ct)
                .ConfigureAwait(false);

            _logger.LogError(exception, "任务 {TaskId} 处理异常，已上报失败。", task.Id);
        }
    }

    private async Task<IReadOnlyList<ExternalTaskDto>> FetchAsync(CancellationToken ct)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/runtime/external-tasks/fetch",
            new FetchRequest
            {
                WorkerId = _options.WorkerId,
                Topics = _options.Topics,
                MaxTasks = _options.MaxTasks,
                LockDurationMs = _options.LockDurationMs,
                WaitSeconds = _options.PollIntervalSeconds,
            },
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var tasks = await response.Content
            .ReadFromJsonAsync<List<ExternalTaskDto>>(JsonDefaults.Options, ct)
            .ConfigureAwait(false);

        return tasks ?? [];
    }

    private Task<HttpResponseMessage> CompleteAsync(
        ExternalTaskDto task,
        Dictionary<string, object?> variables,
        CancellationToken ct) =>
        _client.PostAsJsonAsync(
            $"/api/runtime/external-tasks/{Uri.EscapeDataString(task.Id)}/complete",
            new CompleteRequest
            {
                WorkerId = _options.WorkerId,
                Variables = PayloadConverter.ToElements(variables),
            },
            JsonDefaults.Options,
            ct);

    private async Task ReportFailureAsync(
        ExternalTaskDto task,
        string message,
        string? details,
        int? retries,
        DateTimeOffset? retryAfter,
        CancellationToken ct)
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/runtime/external-tasks/{Uri.EscapeDataString(task.Id)}/failure",
            new FailureRequest
            {
                WorkerId = _options.WorkerId,
                ErrorMessage = message,
                ErrorDetails = details,
                Retries = retries,
                RetryAfter = retryAfter,
            },
            JsonDefaults.Options,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private async Task TryReportFailureAsync(
        ExternalTaskDto task,
        string message,
        string? details,
        CancellationToken ct)
    {
        // 回执自己也可能失败（网络断了）。此时唯一正确的做法是放弃 ——
        // 锁到期后任务会被重新领取，服务端不会丢它。
        try
        {
            await ReportFailureAsync(task, message, details, null, null, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "任务 {TaskId} 的失败回执未能送达。", task.Id);
        }
    }
}
