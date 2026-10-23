using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Diagnostics;
using NetBpm.Engine.Events;

namespace NetBpm.Runtime.Hosting;

/// <summary>
/// outbox 发布器：把 <c>ACT_EVT_OUTBOX</c> 里待发布的事件投递给宿主注册的
/// <see cref="IOutboxEventHandler"/>，成功后打标，失败按退避重试，超过上限进死信。
/// <para>
/// <b>至少一次</b>：数据库提交成功但进程在打标前崩溃，事件会再次投递；
/// 订阅方必须以 <see cref="OutboxEvent.Id"/> 做幂等。
/// </para>
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private readonly IEventOutboxStore _store;
    private readonly IOutboxEventHandler[] _handlers;
    private readonly OutboxPublisherOptions _options;
    private readonly ILogger<OutboxPublisherService> _logger;

    public OutboxPublisherService(
        IEventOutboxStore store,
        IEnumerable<IOutboxEventHandler> handlers,
        IOptions<OutboxPublisherOptions> options,
        ILogger<OutboxPublisherService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _handlers = handlers.ToArray();
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("outbox 发布器已禁用（NetBpm:Outbox:Enabled=false）。");
            return;
        }

        if (_handlers.Length == 0)
        {
            _logger.LogWarning(
                "outbox 已启用但没有注册 IOutboxEventHandler：事件会停留在 ACT_EVT_OUTBOX，发布器不消费。");
            return;
        }

        try
        {
            await Task.Delay(_options.StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 启动延迟窗口内宿主已在停机：正常退出，别让取消变成「后台服务崩溃」。
            return;
        }

        var batchSize = Math.Max(1, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;

            try
            {
                var pending = await _store
                    .FindPendingAsync(DateTime.UtcNow, batchSize, stoppingToken)
                    .ConfigureAwait(false);

                foreach (var outboxEvent in pending)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    if (await PublishOneAsync(outboxEvent, stoppingToken).ConfigureAwait(false))
                    {
                        published++;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "outbox 发布轮询失败，将在下一轮重试。");
            }

            if (published < batchSize)
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

    private async ValueTask<bool> PublishOneAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        try
        {
            foreach (var handler in _handlers)
            {
                await handler.HandleAsync(outboxEvent, ct).ConfigureAwait(false);
            }

            return await _store
                .MarkPublishedAsync(outboxEvent.Id, DateTime.UtcNow, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var nextAttempt = DateTime.UtcNow + OutboxRetry.Backoff(outboxEvent.PublishAttempts + 1);

            try
            {
                await _store
                    .MarkFailureAsync(outboxEvent.Id, exception.Message, DateTime.UtcNow, nextAttempt, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception markException)
            {
                _logger.LogError(markException, "记录 outbox 事件 {EventId} 失败状态时出错。", outboxEvent.Id);
            }

            EngineDiagnostics.OutboxPublishFailures.Add(
                1,
                new KeyValuePair<string, object?>(
                    "dead_letter",
                    outboxEvent.PublishAttempts + 1 >= OutboxRetry.DefaultMaxAttempts));

            _logger.LogWarning(
                exception,
                "outbox 事件 {EventId}（{EventType}）发布失败，下次尝试 {NextAttempt:O}。",
                outboxEvent.Id,
                outboxEvent.EventType,
                nextAttempt);

            return false;
        }
    }
}
