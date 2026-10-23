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
/// 拾取流程（每次 <see cref="TakeDueAsync"/> 的<b>一轮</b>，全部在一次 <c>EVAL</c> 里完成）：
/// ① <c>delay → ready</c> 搬运到期项；② <c>inflight → ready</c> 回收超时未确认项（崩溃场景）；
/// ③ 从 <c>ready</c> 取走并登记到 <c>inflight</c>；④ 顺带算出「下一次可能到期的时刻」。
/// </para>
/// <para>
/// 脚本连 ④ 一起做掉，是为了省掉「取件为空后再查一次 <c>ZRANGE</c>」的第二轮 RTT ——
/// 空闲库每轮只有一次往返。
/// </para>
/// <para>
/// 搬运脚本<b>先 LPUSH 再 ZREM</b>：宁可重复投递，不可丢条目。
/// 重复投递的后果只是一次无效的数据库查询（租约会挡住重复执行），见 <see cref="IJobQueue"/> 契约。
/// </para>
/// <para>
/// <b>唤醒</b>：入队时先本地唤醒本进程的等待者（即使 pub/sub 不可用也不至于干等超时），
/// 再向 <c>job:wake</c> 频道发布一条消息唤醒<b>其它宿主</b>。
/// 订阅在首次取件时惰性建立，失败只记警告、不抛异常（跨宿主唤醒退化为「按到期时刻轮询」）。
/// </para>
/// <para>
/// <b>断连降级</b>：<see cref="AbortOnConnectFail"/> = <see langword="false"/>，
/// 启动时不因 Redis 不可用而失败；所有 Redis 异常在此吞掉并记警告，
/// 执行器随即退化成定期扫库，作业不会丢。
/// </para>
/// </remarks>
public sealed class RedisJobQueue : IJobQueue, IDisposable
{
    /// <summary>
    /// 单轮拾取脚本（KEYS[1]=delay，KEYS[2]=inflight，KEYS[3]=ready）：
    /// ①② 把 delay / inflight 里 score ≤ now 的条目搬到 ready（先 LPUSH 再 ZREM，宁可重复不可丢），
    /// ③ 从 ready 取走最多 limit 条并登记到 inflight，
    /// ④ 返回「下一次可能到期的时刻」ticks（0 表示两段都空）。
    /// 四次往返合并成一次 EVAL，降低高频轮询下的 RTT 开销。
    /// </summary>
    private const string PickScript = """
        local now = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local visibilityUntil = tonumber(ARGV[3])

        -- ①② 到期搬运：延迟段 / 在途回收段 → 就绪段
        for s = 1, 2 do
            local items = redis.call('ZRANGEBYSCORE', KEYS[s], '-inf', now, 'LIMIT', 0, limit)
            if #items > 0 then
                redis.call('LPUSH', KEYS[3], unpack(items))
                for i = 1, #items do
                    redis.call('ZREM', KEYS[s], items[i])
                end
            end
        end

        -- ③ 取走并登记在途；LPUSH 入队、RPOP 出队，天然按到期时间先后投递
        local out = {}
        for i = 1, limit do
            local id = redis.call('RPOP', KEYS[3])
            if not id then break end
            redis.call('ZADD', KEYS[2], visibilityUntil, id)
            out[#out + 1] = id
        end

        -- ④ 下一次可能有条目到期的时刻：取两个集合里「第一个 score > now」的最小值
        local nextDue = 0
        for s = 1, 2 do
            local head = redis.call('ZRANGEBYSCORE', KEYS[s], '(' .. ARGV[1], '+inf', 'WITHSCORES', 'LIMIT', 0, 1)
            if head[1] then
                local score = tonumber(head[2])
                if nextDue == 0 or score < nextDue then
                    nextDue = score
                end
            end
        end

        return { string.format('%.0f', nextDue), unpack(out) }
        """;

    /// <summary>
    /// 释放脚本（KEYS[1]=inflight，KEYS[2]=ready）：ZREM + LPUSH 必须原子，
    /// 否则「ZREM 成功、LPUSH 失败」会让提示同时从两段消失，只能等兜底扫库。
    /// </summary>
    private const string ReleaseScript = """
        redis.call('ZREM', KEYS[1], ARGV[1])
        redis.call('LPUSH', KEYS[2], ARGV[1])
        return 1
        """;

    private readonly RedisJobQueueOptions _options;
    private readonly Lazy<IConnectionMultiplexer> _connection;
    private readonly bool _ownsConnection;
    private readonly RedisKey _delayKey;
    private readonly RedisKey _readyKey;
    private readonly RedisKey _inflightKey;
    private readonly RedisChannel _wakeChannel;
    private readonly string _wakeChannelName;
    private readonly SemaphoreSlim _wakeup = new(0);
    private readonly object _gate = new();
    private readonly ILogger<RedisJobQueue>? _logger;
    private volatile bool _subscribed;
    private volatile bool _disposed;
    private Task? _subscribeTask;
    private IDatabase? _database;

    /// <summary>用现成的连接创建（便于测试与复用宿主已有的 <c>IConnectionMultiplexer</c>）。</summary>
    /// <remarks>
    /// 连接<b>不归本实例所有</b>：<see cref="DisposeAsync"/> 只退订唤醒频道，不会关闭连接。
    /// </remarks>
    public RedisJobQueue(
        IConnectionMultiplexer connection,
        IOptions<RedisJobQueueOptions> options,
        ILogger<RedisJobQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value ?? throw new ArgumentException("Redis 队列配置为空。", nameof(options));
        _logger = logger;
        _ownsConnection = false;
        _connection = new Lazy<IConnectionMultiplexer>(
            () => connection,
            LazyThreadSafetyMode.ExecutionAndPublication);

        (_delayKey, _readyKey, _inflightKey, _wakeChannel, _wakeChannelName) = BuildKeys(_options);
    }

    /// <summary>按配置惰性建连：第一次真正用到时才连，Redis 不可用时不影响宿主启动。</summary>
    public RedisJobQueue(
        IOptions<RedisJobQueueOptions> options,
        ILogger<RedisJobQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value ?? throw new ArgumentException("Redis 队列配置为空。", nameof(options));
        _logger = logger;
        _ownsConnection = true;
        _connection = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(BuildConfiguration(_options)),
            LazyThreadSafetyMode.ExecutionAndPublication);

        (_delayKey, _readyKey, _inflightKey, _wakeChannel, _wakeChannelName) = BuildKeys(_options);
    }

    /// <inheritdoc />
    public string Name => "redis";

    /// <summary>当前连接（惰性建连）。</summary>
    public IConnectionMultiplexer Connection => _connection.Value;

    /// <summary>
    /// 目标逻辑库：配了 <see cref="RedisJobQueueOptions.ConnectionString"/> 时以连接串为准
    /// （<see cref="RedisJobQueueOptions.Database"/> 按契约被忽略），否则用配置里的库号。
    /// </summary>
    /// <remarks>解析一次后缓存：<c>GetDatabase</c> 本身不贵，但没理由每轮轮询都算一遍。</remarks>
    private IDatabase Database =>
        _database ??= string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? Connection.GetDatabase(_options.Database)
            : Connection.GetDatabase();

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(JobQueueItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.JobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = Database;

            // 本进程的等待者立刻放行，不等发布订阅的往返
            SignalWakeup();

            // 两条命令并行下发：StackExchange.Redis 会复用同一条连接做管线化，
            // 不并行的话这里要串行等两次 RTT。
            await Task.WhenAll(
                    db.SortedSetAddAsync(_delayKey, item.JobId, item.DueAtUtc.Ticks),
                    db.PublishAsync(_wakeChannel, item.JobId))
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
    /// <remarks>
    /// 与 <see cref="InMemoryJobQueue"/> 一致：被唤醒后<b>继续取</b>，而不是空手返回。
    /// 空手返回会让执行器立刻做一次全量扫库 —— 唤醒越多、白扫越多。
    /// </remarks>
    public async ValueTask<IReadOnlyList<JobQueueItem>> TakeDueAsync(
        int maxCount,
        TimeSpan waitTimeout,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        // 订阅只在首次取件时建立：宿主启动期不做多余动作，且订阅失败不影响取件
        EnsureSubscribed();

        var startedAt = DateTime.UtcNow;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            List<JobQueueItem> items;
            long nextDueTicks;

            try
            {
                (items, nextDueTicks) = await PickAsync(Database, DateTime.UtcNow, maxCount)
                    .ConfigureAwait(false);
            }
            catch (RedisException exception)
            {
                _logger?.LogWarning(exception, "Redis 队列取件失败，本轮退化为空结果。");

                return Array.Empty<JobQueueItem>();
            }

            if (items.Count > 0)
            {
                return items;
            }

            // ④ 无就绪条目：睡到「下一个到期时刻」「可见性超时回收时刻」或兜底超时，
            //    期间可被入队通知（本进程信号量 / 跨宿主 pub/sub）提前唤醒。
            var remaining = waitTimeout - (DateTime.UtcNow - startedAt);

            if (remaining <= TimeSpan.Zero)
            {
                return Array.Empty<JobQueueItem>();
            }

            var wait = remaining;

            if (nextDueTicks > 0)
            {
                var untilNext = new DateTime(nextDueTicks, DateTimeKind.Utc) - DateTime.UtcNow;

                if (untilNext < wait)
                {
                    wait = untilNext;
                }
            }

            // 已经到期却被 limit 截断（或被别的宿主抢先搬走）：立刻再取，
            // 但不允许完全零等待，避免把一次抖动放大成自旋。
            if (wait < MinimumWait)
            {
                wait = MinimumWait;
            }

            await _wakeup.WaitAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // 单命令即可，没必要走 EVAL（少一次脚本缓存查找）
            await Database.SortedSetRemoveAsync(_inflightKey, jobId).ConfigureAwait(false);
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
            // 提前放回就绪队列头部（比等可见性超时更快重试）。
            // 出在途 + 进就绪必须原子：分两步发的话，中间失败会让提示凭空消失。
            await Database.ScriptEvaluateAsync(
                    ReleaseScript,
                    [_inflightKey, _readyKey],
                    [(RedisValue)jobId])
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            _logger?.LogWarning(exception, "Redis 队列释放失败（作业 {JobId}）。", jobId);
        }
    }

    /// <summary>
    /// 同步释放：DI 容器用 <c>provider.Dispose()</c> 收尾时只找 <see cref="IDisposable"/>，
    /// 缺少它会让宿主停机时抛「type only implements IAsyncDisposable」。
    /// </summary>
    /// <remarks>与 <see cref="DisposeAsync"/> 语义一致，只是不等异步命令完成。</remarks>
    public void Dispose()
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

                // 只有自己建的连接才退订：共享连接上「退订频道」会把别人的订阅一起摘掉，
                // 而残留的处理器已经带了 _disposed 检查，留着只是空转，不会出错。
                if (_ownsConnection)
                {
                    try
                    {
                        connection.GetSubscriber().Unsubscribe(_wakeChannel);
                    }
                    catch (RedisException exception)
                    {
                        _logger?.LogDebug(exception, "退订 Redis 唤醒频道失败（忽略）。");
                    }

                    connection.Close(allowCommandsToComplete: false);
                    connection.Dispose();
                }
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

                // 只有自己建的连接才退订：共享连接上「退订频道」会把别人的订阅一起摘掉，
                // 而残留的处理器已经带了 _disposed 检查，留着只是空转，不会出错。
                if (_ownsConnection)
                {
                    try
                    {
                        await connection.GetSubscriber().UnsubscribeAsync(_wakeChannel).ConfigureAwait(false);
                    }
                    catch (RedisException exception)
                    {
                        _logger?.LogDebug(exception, "退订 Redis 唤醒频道失败（忽略）。");
                    }

                    await connection.CloseAsync().ConfigureAwait(false);
                    connection.Dispose();
                }
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

    /// <summary>最短等待：避免「明明刚到期、却算出一个 0 等待」导致的自旋。</summary>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(1);

    private static (RedisKey Delay, RedisKey Ready, RedisKey Inflight, RedisChannel Wake, string WakeName) BuildKeys(
        RedisJobQueueOptions options)
    {
        // 键与频道在构造时算一次：options 的属性每次访问都会重新拼接前缀字符串
        var wakeName = options.WakeChannel;

        return (
            options.DelayKey,
            options.ReadyKey,
            options.InflightKey,
            RedisChannel.Literal(wakeName),
            wakeName);
    }

    /// <summary>
    /// 取件并把「下一次到期时刻」一并带回来（单次 EVAL）。
    /// </summary>
    /// <returns>条目列表 + 下次到期 ticks（0 = 无）。</returns>
    private async ValueTask<(List<JobQueueItem> Items, long NextDueTicks)> PickAsync(
        IDatabase db,
        DateTime now,
        int maxCount)
    {
        var visibilityUntil = (now + _options.VisibilityTimeout).Ticks;

        var raw = (RedisValue[]?)await db.ScriptEvaluateAsync(
                PickScript,
                [_delayKey, _inflightKey, _readyKey],
                [now.Ticks, maxCount, visibilityUntil])
            .ConfigureAwait(false);

        var items = new List<JobQueueItem>();

        if (raw is null || raw.Length == 0)
        {
            return (items, 0);
        }

        // 首元素是下次到期时刻（ticks），其余是作业 id
        var nextDueTicks = raw[0].TryParse(out long parsed) ? parsed : 0;

        for (var i = 1; i < raw.Length; i++)
        {
            items.Add(new JobQueueItem((string?)raw[i] ?? string.Empty, now));
        }

        return (items, nextDueTicks);
    }

    /// <summary>
    /// 为首个消费者建立跨宿主唤醒订阅（幂等、非阻塞）。
    /// </summary>
    /// <remarks>
    /// 订阅刻意<b>不等</b>完成：Redis 断连时 <c>SubscribeAsync</c> 会一直挂到重连，
    /// 若在此 await 就会把「快速失败 → 退化成扫库」的降级路径堵死。
    /// </remarks>
    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed || _subscribeTask is not null)
            {
                return;
            }

            Connection.ConnectionFailed += OnConnectionFailed;

            _subscribeTask = SubscribeAsync();
        }
    }

    private async Task SubscribeAsync()
    {
        try
        {
            await Connection.GetSubscriber()
                .SubscribeAsync(_wakeChannel, (_, _) => SignalWakeup())
                .ConfigureAwait(false);

            _subscribed = true;

            _logger?.LogDebug("已订阅 Redis 作业唤醒频道：{Channel}", _wakeChannelName);
        }
        catch (Exception exception)
        {
            // 订阅只是「更快」，不是「正确」；下次取件时重试
            _logger?.LogWarning(
                exception,
                "订阅 Redis 唤醒频道失败（{Channel}），跨宿主唤醒暂不可用；本进程入队仍会本地唤醒。",
                _wakeChannelName);

            lock (_gate)
            {
                _subscribeTask = null;
            }
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args) =>
        _logger?.LogWarning(
            args.Exception,
            "Redis 连接失败：{EndPoint}（{FailureType}）",
            args.EndPoint,
            args.FailureType);

    /// <summary>唤醒本进程的等待者；信号量计数钉在 0/1，避免信号累积后空转。</summary>
    private void SignalWakeup()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_wakeup.CurrentCount == 0)
                {
                    _wakeup.Release();
                }
            }
            catch (ObjectDisposedException)
            {
                // 与 DisposeAsync 竞争：宿主正在停，忽略
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
