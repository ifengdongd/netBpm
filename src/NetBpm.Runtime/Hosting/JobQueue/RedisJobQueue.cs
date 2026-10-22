using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using StackExchange.Redis;

namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// 基于 Redis 的延迟作业队列实现（可跨宿主共享、进程重启不丢提示）。
/// </summary>
/// <remarks>
/// <para><b>三个键</b>（均在 <see cref="RedisJobQueueOptions.KeyPrefix"/> 命名空间下）：</para>
/// <list type="bullet">
///   <item><description><c>job:delay</c> — 有序集合，score = 到期时间 ticks，延迟段。</description></item>
///   <item><description><c>job:ready</c> — 列表，已到期、等待被取走。</description></item>
///   <item><description><c>job:inflight</c> — 有序集合，score = 可见性超时时刻 ticks，
///   表示「已被某个宿主取走但还没确认完成」。</description></item>
/// </list>
/// <para>
/// 拾取流程（每次 <see cref="TakeDueAsync"/>）：
/// ① <c>delay → ready</c> 搬运到期项；② <c>inflight → ready</c> 回收超时未确认项（崩溃场景）；
/// ③ 从 <c>ready</c> 取走并登记到 <c>inflight</c>。
/// </para>
/// <para>
/// 搬运脚本<b>先 LPUSH 再 ZREM</b>：宁可重复投递，不可丢条目。
/// 重复投递的后果只是一次无效的数据库查询（租约会挡住重复执行），见 <see cref="IJobQueue"/> 契约。
/// </para>
/// <para>
/// <b>断连降级</b>：<see cref="AbortOnConnectFail"/> = <see langword="false"/>，
/// 启动时不因 Redis 不可用而失败；所有 Redis 异常在此吞掉并记警告，
/// 执行器随即退化成定期扫库，作业不会丢。
/// </para>
/// </remarks>
public sealed class RedisJobQueue : IJobQueue
{
    /// <summary>把 source 里 score ≤ now 的条目搬到 target 列表（最多 limit 条），先写后删。</summary>
    private const string MoveDueScript = """
        local now = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local items = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now, 'LIMIT', 0, limit)
        local n = #items
        if n == 0 then return 0 end
        for i = 1, n do
            redis.call('LPUSH', KEYS[2], items[i])
            redis.call('ZREM', KEYS[1], items[i])
        end
        return n
        """;

    /// <summary>从就绪列表取最多 limit 条，并登记到在途集合。</summary>
    private const string TakeScript = """
        local limit = tonumber(ARGV[1])
        local visibilityUntil = tonumber(ARGV[2])
        local out = {}
        for i = 1, limit do
            local id = redis.call('RPOP', KEYS[1])
            if not id then break end
            redis.call('ZADD', KEYS[2], visibilityUntil, id)
            out[#out + 1] = id
        end
        return out
        """;

    private const string RemoveScript = "return redis.call('ZREM', KEYS[1], ARGV[1])";

    private readonly RedisJobQueueOptions _options;
    private readonly Lazy<IConnectionMultiplexer> _connection;
    private readonly SemaphoreSlim _wakeup = new(0);
    private readonly object _gate = new();
    private readonly ILogger<RedisJobQueue>? _logger;
    private bool _subscribed;
    private volatile bool _disposed;

    /// <summary>用现成的连接创建（便于测试与复用宿主已有的 <c>IConnectionMultiplexer</c>）。</summary>
    public RedisJobQueue(
        IConnectionMultiplexer connection,
        IOptions<RedisJobQueueOptions> options,
        ILogger<RedisJobQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value ?? throw new ArgumentException("Redis 队列配置为空。", nameof(options));
        _logger = logger;
        _connection = new Lazy<IConnectionMultiplexer>(
            () => connection,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>按配置惰性建连：第一次真正用到时才连，Redis 不可用时不影响宿主启动。</summary>
    public RedisJobQueue(
        IOptions<RedisJobQueueOptions> options,
        ILogger<RedisJobQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value ?? throw new ArgumentException("Redis 队列配置为空。", nameof(options));
        _logger = logger;
        _connection = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(BuildConfiguration(_options)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name => "redis";

    /// <summary>当前连接（惰性建连）。</summary>
    public IConnectionMultiplexer Connection => _connection.Value;

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(JobQueueItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.JobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = Connection.GetDatabase(_options.Database);

            await db.SortedSetAddAsync(_options.DelayKey, item.JobId, item.DueAtUtc.Ticks)
                .ConfigureAwait(false);

            // 顺带唤醒本进程的等待者：即使 pub/sub 暂时不可用也不至于干等兜底超时
            SignalWakeup();

            await db.PublishAsync(RedisChannel.Literal(_options.WakeChannel), item.JobId)
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            // 队列只是提示通道，Redis 不可用时不影响正确性
            _logger?.LogWarning(
                exception,
                "Redis 队列入队失败（作业 {JobId}），将依赖定期扫库兜底。",
                item.JobId);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<JobQueueItem>> TakeDueAsync(
        int maxCount,
        TimeSpan waitTimeout,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        try
        {
            EnsureSubscribed();

            var db = Connection.GetDatabase(_options.Database);
            var now = DateTime.UtcNow;

            // ① 延迟段到期 → 就绪段
            await MoveDueAsync(db, _options.DelayKey, _options.ReadyKey, now, maxCount)
                .ConfigureAwait(false);

            // ② 回收超时未确认的在途条目（宿主崩溃场景）
            await MoveDueAsync(db, _options.InflightKey, _options.ReadyKey, now, maxCount)
                .ConfigureAwait(false);

            // ③ 取走就绪条目并登记到在途集合
            var taken = await TakeReadyAsync(db, maxCount, now).ConfigureAwait(false);

            if (taken.Count > 0)
            {
                return taken;
            }

            // ④ 无就绪条目：睡到下一个到期时刻（或兜底超时），期间可被入队通知唤醒
            await WaitUntilNextDueAsync(db, waitTimeout, ct).ConfigureAwait(false);

            return Array.Empty<JobQueueItem>();
        }
        catch (RedisException exception)
        {
            _logger?.LogWarning(exception, "Redis 队列取件失败，本轮退化为空结果。");

            return Array.Empty<JobQueueItem>();
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = Connection.GetDatabase(_options.Database);

            // 从在途集合移除，避免可见性超时后被重复拾取
            await db.ScriptEvaluateAsync(
                RemoveScript,
                [(RedisKey)_options.InflightKey],
                [(RedisValue)jobId]).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger?.LogWarning(exception, "Redis 队列确认完成失败（作业 {JobId}）。", jobId);
        }
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = Connection.GetDatabase(_options.Database);

            // 提前放回就绪队列头部（比等可见性超时更快重试）
            await db.ScriptEvaluateAsync(
                RemoveScript,
                [(RedisKey)_options.InflightKey],
                [(RedisValue)jobId]).ConfigureAwait(false);

            await db.ListLeftPushAsync(_options.ReadyKey, jobId).ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger?.LogWarning(exception, "Redis 队列释放失败（作业 {JobId}）。", jobId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_connection.IsValueCreated)
            {
                var connection = _connection.Value;

                connection.ConnectionFailed -= OnConnectionFailed;

                await connection.CloseAsync().ConfigureAwait(false);
                connection.Dispose();
            }
        }
        catch (RedisException exception)
        {
            _logger?.LogWarning(exception, "关闭 Redis 连接时出错。");
        }
        finally
        {
            _wakeup.Dispose();
        }
    }

    private static async ValueTask MoveDueAsync(
        IDatabase db,
        RedisKey source,
        RedisKey target,
        DateTime now,
        int limit) =>
        await db.ScriptEvaluateAsync(
            MoveDueScript,
            [source, target],
            [now.Ticks, limit]).ConfigureAwait(false);

    private async ValueTask<List<JobQueueItem>> TakeReadyAsync(
        IDatabase db,
        int maxCount,
        DateTime now)
    {
        var visibilityUntil = (now + _options.VisibilityTimeout).Ticks;

        var taken = (RedisValue[]?)await db.ScriptEvaluateAsync(
            TakeScript,
            [(RedisKey)_options.ReadyKey, (RedisKey)_options.InflightKey],
            [maxCount, visibilityUntil]).ConfigureAwait(false);

        var items = new List<JobQueueItem>();

        if (taken is null)
        {
            return items;
        }

        foreach (var value in taken)
        {
            items.Add(new JobQueueItem((string?)value ?? string.Empty, now));
        }

        return items;
    }

    private async ValueTask WaitUntilNextDueAsync(IDatabase db, TimeSpan waitTimeout, CancellationToken ct)
    {
        var wait = waitTimeout;

        var next = await db.SortedSetRangeByRankWithScoresAsync(_options.DelayKey, 0, 0)
            .ConfigureAwait(false);

        if (next.Length > 0)
        {
            var nextDue = new DateTime((long)next[0].Score, DateTimeKind.Utc);
            var untilNext = nextDue - DateTime.UtcNow;

            if (untilNext < wait)
            {
                wait = untilNext;
            }
        }

        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        await _wakeup.WaitAsync(wait, ct).ConfigureAwait(false);
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            Connection.ConnectionFailed += OnConnectionFailed;

            Connection.GetSubscriber().Subscribe(
                RedisChannel.Literal(_options.WakeChannel),
                (_, _) => SignalWakeup());

            _subscribed = true;
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args) =>
        _logger?.LogWarning(
            args.Exception,
            "Redis 连接失败：{EndPoint}（{FailureType}）",
            args.EndPoint,
            args.FailureType);

    private void SignalWakeup()
    {
        lock (_gate)
        {
            // 计数钉在 0/1：避免连续入队后信号累积，导致后续等待全部空转
            if (_wakeup.CurrentCount == 0)
            {
                _wakeup.Release();
        }
        }
    }

    /// <summary>把 <see cref="RedisJobQueueOptions"/> 编译成连接配置。</summary>
    public static ConfigurationOptions BuildConfiguration(RedisJobQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ConfigurationOptions.Parse(options.ConnectionString.Trim());
        }

        var configuration = new ConfigurationOptions
        {
            DefaultDatabase = options.Database,
            Ssl = options.Ssl,
            ConnectTimeout = (int)Math.Clamp(options.ConnectTimeout.TotalMilliseconds, 100, 60_000),

            // Redis 不可用时不要在启动时炸掉宿主，交给后台重连
            AbortOnConnectFail = false,
            AllowAdmin = false,
        };

        configuration.EndPoints.Add(options.Host, options.Port);

        if (!string.IsNullOrWhiteSpace(options.Auth))
        {
            configuration.Password = options.Auth;
        }

        return configuration;
    }
}
