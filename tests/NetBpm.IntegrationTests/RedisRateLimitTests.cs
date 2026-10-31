using NetBpm.Rest.RateLimiting;
using StackExchange.Redis;
using Xunit;

namespace NetBpm.IntegrationTests;

/// <summary>
/// 全局配额（<see cref="RateLimitStore.Redis"/>）的真 Redis 用例。
/// </summary>
/// <remarks>
/// <para>
/// 需要真 Redis：环境变量 <c>NETBPM_TEST_REDIS</c> 给连接串（如
/// <c>127.0.0.1:6379,password=xxx</c>），没给就整类 <c>Skip</c>——
/// 「没有 Redis 就跳过」比「用一个假 Redis 假装通过」诚实。
/// </para>
/// <para>
/// 每个用例用独立的键前缀（<c>netbpm:test:{guid}:</c>），跑完清理，
/// 不依赖库是空的——本机 Redis 上还跑着作业队列，别互相打扰。
/// </para>
/// </remarks>
public sealed class RedisRateLimitTests
{
    private const string ConnectionEnvironmentVariable = "NETBPM_TEST_REDIS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuotaShouldBeShared_AcrossLimiterInstances()
    {
        var (options, prefix) = BuildOptions();

        // 两个 limiter 实例 = 两个副本：它们必须共用一个桶。
        // 这正是进程内实现做不到、也是「配置 600 实际能打 1800」的根因。
        using var replicaA = new RedisFixedWindowRateLimiter(options);
        using var replicaB = new RedisFixedWindowRateLimiter(options);

        try
        {
            Assert.True((await replicaA.AcquireAsync("client:cl_shared", 3, Ct)).Allowed);
            Assert.True((await replicaB.AcquireAsync("client:cl_shared", 3, Ct)).Allowed);
            Assert.True((await replicaA.AcquireAsync("client:cl_shared", 3, Ct)).Allowed);

            // 第 4 次：两份计数加在一起已经用满 3
            var denied = await replicaB.AcquireAsync("client:cl_shared", 3, Ct);

            Assert.False(denied.Allowed);
            Assert.Equal(0, denied.Remaining);
            Assert.Equal(3, denied.Limit);
            Assert.True(denied.Counted);

            // 另一个分区不受影响（分区键进了窗口键）
            Assert.True((await replicaB.AcquireAsync("client:cl_other", 3, Ct)).Allowed);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task WindowShouldRollOver_WithControlledClock()
    {
        var (options, prefix) = BuildOptions();
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));

        using var limiter = new RedisFixedWindowRateLimiter(options, clock);

        try
        {
            var first = await limiter.AcquireAsync("client:cl_window", 1, Ct);

            Assert.True(first.Allowed);
            Assert.Equal(0, first.Remaining);
            Assert.False((await limiter.AcquireAsync("client:cl_window", 1, Ct)).Allowed);

            // 跨过窗口边界：窗口由键里的分钟序号表达，新窗口是另一个键，计数从头开始
            clock.Advance(TimeSpan.FromSeconds(61));

            var afterReset = await limiter.AcquireAsync("client:cl_window", 1, Ct);

            Assert.True(afterReset.Allowed);
            Assert.Equal(0, afterReset.Remaining);
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task UnlimitedPartition_ShouldNotTouchRedis()
    {
        var (options, prefix) = BuildOptions();

        using var limiter = new RedisFixedWindowRateLimiter(options);

        try
        {
            for (var index = 0; index < 5; index++)
            {
                var decision = await limiter.AcquireAsync("client:cl_unlimited", 0, Ct);

                Assert.True(decision.Allowed);
                Assert.False(decision.Counted);
            }
        }
        finally
        {
            await CleanupAsync(prefix);
        }
    }

    [Fact]
    public async Task UnavailableRedis_ShouldFailOpen_ByDefault()
    {
        // 一个没人监听的端口：连不上，但 Connect 不会抛（AbortOnConnectFail=false）
        var options = new RedisRateLimitOptions
        {
            Host = "127.0.0.1",
            Port = 6399,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            FailOpenOnError = true,
        };

        using var limiter = new RedisFixedWindowRateLimiter(options);

        var decision = await limiter.AcquireAsync("client:cl_down", 1, Ct);

        // 放行但不计数：不写限流响应头，调用方看到的是「这次没被计数」而不是伪造的剩余额度
        Assert.True(decision.Allowed);
        Assert.False(decision.Counted);
    }

    [Fact]
    public async Task UnavailableRedis_ShouldFailClosed_WhenConfigured()
    {
        var options = new RedisRateLimitOptions
        {
            Host = "127.0.0.1",
            Port = 6399,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            FailOpenOnError = false,
        };

        using var limiter = new RedisFixedWindowRateLimiter(options);

        var decision = await limiter.AcquireAsync("client:cl_down", 1, Ct);

        Assert.False(decision.Allowed);
        Assert.True(decision.Counted);
    }

    private static (RedisRateLimitOptions Options, string Prefix) BuildOptions()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(connectionString),
            $"未设置环境变量 {ConnectionEnvironmentVariable}（真实 Redis 连接串），跳过全局配额用例。");

        var prefix = $"netbpm:test:{Guid.NewGuid():N}:";

        var options = new RedisRateLimitOptions
        {
            ConnectionString = connectionString!.Trim(),
            KeyPrefix = prefix,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            CommandTimeout = TimeSpan.FromSeconds(3),
        };

        return (options, prefix);
    }

    /// <summary>清掉本用例造出来的窗口键（按前缀 SCAN，不影响队列键）。</summary>
    private static async Task CleanupAsync(string prefix)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        try
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString.Trim());
            var database = connection.GetDatabase();
            var server = connection.GetServer(connection.GetEndPoints().First());

            await foreach (var key in server.KeysAsync(pattern: prefix + "ratelimit:*").WithCancellation(Ct))
            {
                await database.KeyDeleteAsync(key);
            }
        }
        catch (Exception)
        {
            // 清理失败不该让用例变红：键带 TTL，最多一分钟后自行消失
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
