using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Hosting.JobQueue;
using Xunit;

namespace NetBpm.UnitTests;

/// <summary>
/// Redis 队列快照（管理端「Redis 诊断」页的数据源）：不可达时如实报告、可达时反映三个键的条目数。
/// </summary>
/// <remarks>
/// 快照是<b>观测</b>接口，契约是「任何情况都不抛异常、不写键」——
/// 诊断页不该因为被诊断对象故障而变成 500，这条比数字准不准更重要。
/// </remarks>
public class RedisJobQueueInspectTests
{
    /// <summary>环境变量：完整连接串（如 <c>127.0.0.1:6379,password=xxx</c>），与其它 Redis 用例保持一致。</summary>
    private const string ConnectionEnvironmentVariable = "NETBPM_TEST_REDIS";

    [Fact]
    public async Task 快照_连接不可用时应如实报告而不抛异常()
    {
        // 端口 1 是保留端口：本机必定拒绝连接，用例不依赖「恰好没有 Redis」
        var options = new RedisJobQueueOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = 1,
            KeyPrefix = "netbpm:test:inspect:",
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };

        await using var queue = new RedisJobQueue(Options.Create(options), NullLogger<RedisJobQueue>.Instance);

        var snapshot = await queue.InspectAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Reachable);
        Assert.Null(snapshot.LatencyMs);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Error));
        Assert.Empty(snapshot.Keys);
        Assert.Null(snapshot.Server);
    }

    [Fact]
    public async Task 快照_应反映三个队列键的条目数()
    {
        var ct = TestContext.Current.CancellationToken;

        // 随机前缀隔离键空间：用例之间、以及与真实业务队列之间互不干扰
        var options = new RedisJobQueueOptions
        {
            KeyPrefix = $"netbpm:test:inspect:{Guid.NewGuid():N}:",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString.Trim();
        }

        await using var queue = new RedisJobQueue(Options.Create(options), NullLogger<RedisJobQueue>.Instance);

        var empty = await queue.InspectAsync(ct);

        if (!empty.Reachable)
        {
            Assert.Skip(
                "没有可用的 Redis（默认 127.0.0.1:6379；带口令时用环境变量 "
                + $"{ConnectionEnvironmentVariable} 注入连接串）：{empty.Error}");

            return;
        }

        try
        {
            Assert.Equal(3, empty.Keys.Count);
            Assert.Equal(
                new[] { "delay", "ready", "inflight" },
                empty.Keys.Select(item => item.Role).ToArray());
            Assert.All(empty.Keys, item => Assert.Equal(0L, item.Length));
            Assert.NotNull(empty.LatencyMs);
            Assert.True(empty.LatencyMs >= 0);
            Assert.Equal(options.KeyPrefix + "job:delay", empty.Keys[0].Key);

            // 服务端 INFO 必须真读到：连接层 AllowAdmin 关掉时 INFO 会被 StackExchange.Redis 拒绝，
            // 这一块会静默变 null，前端「服务端信息」卡片就永远空着（回归就靠这几条断言）。
            var server = Assert.IsType<RedisServerInfo>(empty.Server);

            Assert.False(string.IsNullOrWhiteSpace(server.Version));
            Assert.NotNull(server.UsedMemoryBytes);
            Assert.NotNull(server.ConnectedClients);
            Assert.NotNull(server.UptimeSeconds);

            // 入队一条到期提示：延迟段应当立刻多出一条
            await queue.EnqueueAsync(
                new JobQueueItem($"inspect-{Guid.NewGuid():N}", DateTime.UtcNow),
                ct);

            var after = await queue.InspectAsync(ct);

            Assert.Equal(1, after.Keys.Single(item => item.Role == "delay").Length);
        }
        finally
        {
            await queue.Connection
                .GetDatabase()
                .KeyDeleteAsync([options.DelayKey, options.ReadyKey, options.InflightKey]);
        }
    }
}
