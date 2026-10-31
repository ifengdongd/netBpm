using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace NetBpm.Rest.RateLimiting;

/// <summary>
/// 固定窗口限流器的 <b>Redis 版</b>：配额跨副本共享，一个接入方的配额不再随副本数放大。
/// </summary>
/// <remarks>
/// <para>
/// <b>窗口由键自身表达</b>：<c>{prefix}ratelimit:{分钟序号}:{分区键}</c>。
/// 计数用 <c>INCR</c>，首次自增时顺带 <c>PEXPIRE</c> 到本窗口结束（多留 1 秒吸收时钟漂移），
/// 两步放在同一段 Lua 里保证原子——分开写会出现「INCR 成功、进程崩了没设 TTL」的永不过期键。
/// 窗口切换因此不需要任何清理任务：旧键由 Redis 自己过期回收。
/// </para>
/// <para>
/// <b>只多一个 RTT，且不放大失败面</b>：判定是单次 <c>EVALSHA</c>。
/// 命令超时单独配（默认 200ms，见 <see cref="RedisRateLimitOptions.CommandTimeout"/>）——
/// 限流在每个请求的关键路径上，宁可判定失败也不能让请求排队等 Redis。
/// </para>
/// <para>
/// <b>Redis 不可用时怎么办</b>：默认<b>放行</b>（<see cref="RedisRateLimitOptions.FailOpenOnError"/>），
/// 返回 <c>Counted=false</c> 的判定——中间件不会写限流响应头，调用方看到的是「这次没被计数」，
/// 而不是伪造一个剩余额度。限流是保护手段，让「配额基础设施故障」变成「全站 429」
/// 是把小故障放大成全站事故；代价是故障窗口内失去防护，这个取舍可以显式关掉。
/// </para>
/// <para>
/// ⚠️ <b>各副本时钟需大致同步</b>：分钟序号取自本机 UTC 时钟，副本间偏差超过一分钟会各自算一段窗口。
/// 这与进程内实现的行为一致（本来就按本机时钟滚动），多副本请照常配 NTP。
/// </para>
/// </remarks>
public sealed class RedisFixedWindowRateLimiter : IRateLimiter, IDisposable
{
    /// <summary>窗口长度：与进程内实现保持一致（固定窗口，边界处允许 2 倍突发）。</summary>
    private static readonly TimeSpan WindowSize = TimeSpan.FromMinutes(1);

    /// <summary>过期时间余量：吸收副本间时钟漂移，避免窗口未结束键先没。</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(1);

    private static readonly LuaScript AcquireScript = LuaScript.Prepare(
        "local current = redis.call('INCR', @key)\n"
        + "if current == 1 then\n"
        + "  redis.call('PEXPIRE', @key, @ttl)\n"
        + "end\n"
        + "return current");

    private readonly RedisRateLimitOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly Lazy<IConnectionMultiplexer> _connection;

    private IDatabase? _database;
    private long _lastFailureLoggedAt;
    private bool _disposed;

    /// <summary>构造（测试直接传选项；生产由 DI 从 <c>IOptions</c> 解包）。</summary>
    /// <param name="options">Redis 连接与行为配置。</param>
    /// <param name="clock">时钟（测试可替换，用于验证窗口滚动）。</param>
    /// <param name="logger">日志（Redis 故障时限频告警）。</param>
    public RedisFixedWindowRateLimiter(
        RedisRateLimitOptions options,
        TimeProvider? clock = null,
        ILogger<RedisFixedWindowRateLimiter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _connection = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(BuildConfiguration(options)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>目标地址摘要（<b>不含口令</b>），供启动横幅与诊断复用。</summary>
    /// <param name="options">Redis 配置。</param>
    public static string DescribeTarget(RedisRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{options.Host}:{options.Port}");
        }

        try
        {
            // 连接串里可能带 password=，绝不能原样透出
            var configuration = ConfigurationOptions.Parse(options.ConnectionString.Trim());

            return string.Join(", ", configuration.EndPoints.Select(endPoint => endPoint.ToString()));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return "ConnectionString 指定的实例（解析失败）";
        }
    }

    /// <inheritdoc />
    public async ValueTask<RateLimitDecision> AcquireAsync(
        string partitionKey,
        int permitLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (permitLimit <= 0)
        {
            return RateLimitDecision.Unlimited;
        }

        var now = _clock.GetUtcNow();
        var windowId = now.UtcTicks / WindowSize.Ticks;
        var resetAfter = TimeSpan.FromTicks((windowId + 1) * WindowSize.Ticks - now.UtcTicks);

        long count;

        try
        {
            count = await IncrementAsync(
                    BuildWindowKey(windowId, partitionKey),
                    (long)Math.Ceiling(resetAfter.TotalMilliseconds) + (long)ExpiryMargin.TotalMilliseconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 请求被取消（客户端断开）：按约定抛出，不做「放行 / 拒绝」的判定
            throw;
        }
        catch (Exception exception)
        {
            LogStoreFailure(exception);

            return _options.FailOpenOnError
                ? new RateLimitDecision(true, permitLimit, permitLimit, resetAfter, Counted: false)
                : new RateLimitDecision(false, permitLimit, 0, resetAfter, Counted: true);
        }

        var allowed = count <= permitLimit;
        var remaining = allowed ? (int)(permitLimit - count) : 0;

        return new RateLimitDecision(allowed, permitLimit, remaining, resetAfter, Counted: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 只有真的建过连才需要关：从未下发过命令的宿主停机时不应产生连接动作
        if (_connection.IsValueCreated)
        {
            _connection.Value.Dispose();
        }
    }

    private async ValueTask<long> IncrementAsync(string key, long ttlMilliseconds, CancellationToken ct)
    {
        var database = _database ??= string.IsNullOrWhiteSpace(_options.ConnectionString)
            ? _connection.Value.GetDatabase(_options.Database)
            : _connection.Value.GetDatabase();

        var result = await database
            .ScriptEvaluateAsync(
                AcquireScript,
                new { key = (RedisKey)key, ttl = (RedisValue)ttlMilliseconds },
                CommandFlags.None)
            .WaitAsync(_options.CommandTimeout, ct)
            .ConfigureAwait(false);

        return (long)result;
    }

    private string BuildWindowKey(long windowId, string partitionKey) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.NormalizedKeyPrefix}ratelimit:{windowId}:{partitionKey}");

    /// <summary>
    /// 记录配额存储故障：<b>限频</b>（10 秒一条）。
    /// </summary>
    /// <remarks>
    /// Redis 挂掉时每个请求都会走到这里。不限频的话，一次 Redis 故障会把日志卷打满，
    /// 真正有用的第一条（含原因）反而被冲掉——这正是「故障期间看不到故障原因」的经典成因。
    /// </remarks>
    private void LogStoreFailure(Exception exception)
    {
        if (_logger is null)
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcTicks;
        var previous = Interlocked.Read(ref _lastFailureLoggedAt);

        if (previous != 0 && now - previous < TimeSpan.TicksPerSecond * 10)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastFailureLoggedAt, now, previous) != previous)
        {
            return;
        }

        _logger.LogError(
            exception,
            "配额存储不可用（{Target}）：本次请求按 FailOpenOnError={FailOpen} 处理，限流暂时失效。"
            + "10 秒内的同类故障不再重复记录。",
            DescribeTarget(_options),
            _options.FailOpenOnError);
    }

    /// <summary>把选项编译成连接配置。</summary>
    private static ConfigurationOptions BuildConfiguration(RedisRateLimitOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ConfigurationOptions.Parse(options.ConnectionString.Trim());
        }

        var configuration = new ConfigurationOptions
        {
            DefaultDatabase = options.Database,
            Ssl = options.Ssl,
            ConnectTimeout = (int)Math.Clamp(options.ConnectTimeout.TotalMilliseconds, 100, 60_000),

            // Redis 不可用时不要在启动时炸掉宿主：限流自己会按 FailOpenOnError 处理
            AbortOnConnectFail = false,
        };

        configuration.EndPoints.Add(options.Host, options.Port);

        if (!string.IsNullOrWhiteSpace(options.Auth))
        {
            configuration.Password = options.Auth;
        }

        return configuration;
    }
}
