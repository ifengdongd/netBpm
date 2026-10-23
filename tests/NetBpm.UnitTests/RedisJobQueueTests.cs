using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Hosting.JobQueue;
using StackExchange.Redis;
using Xunit;

namespace NetBpm.UnitTests;

/// <summary>
/// Redis 延迟作业队列：对着<b>真实 Redis</b>（127.0.0.1:6379）验证行为契约。
/// </summary>
/// <remarks>
/// 每个用例用随机 <see cref="RedisJobQueueOptions.KeyPrefix"/> 隔离键空间，结束后清理；
/// 本机没有 Redis 时用 <c>Skip.If</c> 跳过，不误报失败。
/// </remarks>
public class RedisJobQueueTests
{
    /// <summary>环境变量：完整连接串（如 <c>127.0.0.1:6379,password=xxx</c>），优先于默认的本机地址。</summary>
    private const string ConnectionEnvironmentVariable = "NETBPM_TEST_REDIS";

    /// <summary>
    /// 可见性超时调短（400ms），让「崩溃回收」用例不用等默认的 6 分钟。
    /// 本机 Redis 若带口令，用环境变量 <c>NETBPM_TEST_REDIS</c> 注入连接串。
    /// </summary>
    private static RedisJobQueueOptions BuildOptions()
    {
        var options = new RedisJobQueueOptions
        {
            KeyPrefix = $"netbpm:test:{Guid.NewGuid():N}:",
            VisibilityTimeout = TimeSpan.FromMilliseconds(400),
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString.Trim();
        }

        return options;
    }

    private static async Task RunWithRealRedisAsync(
        Func<RedisJobQueue, RedisJobQueueOptions, IDatabase, Task> test)
    {
        var options = BuildOptions();

        await using var queue = new RedisJobQueue(
            Options.Create(options),
            NullLogger<RedisJobQueue>.Instance);

        IDatabase database;

        try
        {
            database = queue.Connection.GetDatabase();

            // 连接是惰性建立的：Ping 一次确认真有 Redis，否则跳过而不是误报失败
            await database.PingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            Assert.Skip(
                "没有可用的 Redis（默认 127.0.0.1:6379；带口令时用环境变量 "
                + $"{ConnectionEnvironmentVariable} 注入连接串）：{exception.Message}");

            return;
        }

        try
        {
            await test(queue, options, database);
        }
        finally
        {
            await database.KeyDeleteAsync(
                [options.DelayKey, options.ReadyKey, options.InflightKey]);
        }
    }

    [Fact]
    public async Task Redis队列_到期的条目应被取走并登记到在途集合()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow.AddSeconds(-1)));

            var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));

            var item = Assert.Single(taken);
            Assert.Equal("job-1", item.JobId);
            Assert.Equal(1, await db.SortedSetLengthAsync(options.InflightKey));

            // 在途期内不应被重复投递（第二个消费者取不到）
            Assert.Empty(await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(50)));

            await queue.CompleteAsync("job-1");
            Assert.Equal(0, await db.SortedSetLengthAsync(options.InflightKey));
        });
    }

    [Fact]
    public async Task Redis队列_未到期的条目不应被取走_且不应把等待拖到到期时刻()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow.AddMinutes(5)));

            var startedAt = Stopwatch.GetTimestamp();

            Assert.Empty(await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200)));

            var elapsed = Stopwatch.GetElapsedTime(startedAt);

            // 兜底超时 200ms 必须生效：不能被「下一个到期时刻 5min」带偏，
            // 否则执行器的兜底扫库要空等 5 分钟才轮到。
            Assert.InRange(elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));

            // 条目应原封不动留在延迟段
            Assert.Equal(1, await db.SortedSetLengthAsync(options.DelayKey));
        });
    }

    [Fact]
    public async Task Redis队列_到期条目按到期时间升序投递()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            var now = DateTime.UtcNow;

            await queue.EnqueueAsync(new JobQueueItem("job-early", now.AddSeconds(-2)));
            await queue.EnqueueAsync(new JobQueueItem("job-late", now.AddMinutes(5)));
            await queue.EnqueueAsync(new JobQueueItem("job-mid", now.AddSeconds(-1)));

            var taken = await queue.TakeDueAsync(2, TimeSpan.FromMilliseconds(200));

            Assert.Equal(["job-early", "job-mid"], taken.Select(item => item.JobId));

            // 限额之外未到期的留在延迟段
            Assert.Equal(1, await db.SortedSetLengthAsync(options.DelayKey));
        });
    }

    [Fact]
    public async Task Redis队列_可见性超时后未确认的条目应被回收()
    {
        await RunWithRealRedisAsync(async (queue, _, _) =>
        {
            await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));

            var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));
            Assert.Equal("job-1", Assert.Single(taken).JobId);

            // 可见性超时 400ms：等它过去后，崩溃场景的条目应被另一个消费者接手
            await Task.Delay(600);

            var retaken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));

            Assert.Equal("job-1", Assert.Single(retaken).JobId);
        });
    }

    [Fact]
    public async Task Redis队列_Release应立即放回就绪队列且原子地离开在途集合()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));

            var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));
            Assert.Single(taken);

            await queue.ReleaseAsync("job-1");

            // 出在途 + 进就绪是一次原子操作：两段状态必须同时到位
            Assert.Equal(0, await db.SortedSetLengthAsync(options.InflightKey));
            Assert.Equal(1, await db.ListLengthAsync(options.ReadyKey));

            // 不等可见性超时，立即就能被重新取走
            var retaken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));

            Assert.Equal("job-1", Assert.Single(retaken).JobId);
            Assert.Equal(0, await db.ListLengthAsync(options.ReadyKey));
        });
    }

    [Fact]
    public async Task Redis队列_另一个宿主的入队应通过发布订阅唤醒本宿主的取件()
    {
        await RunWithRealRedisAsync(async (queue, options, _) =>
        {
            // 第二个实例代表「另一个宿主」：共用同一条连接，只共享 Redis 里的三个键
            await using var peer = new RedisJobQueue(
                queue.Connection,
                Options.Create(options),
                NullLogger<RedisJobQueue>.Instance);

            var consumer = Task.Run(async () => await queue.TakeDueAsync(10, TimeSpan.FromSeconds(60)));

            // 让本宿主先进入等待并完成订阅确认
            await Task.Delay(500);

            // peer 的本地信号量与 queue 无关：只有 pub/sub 能把 queue 叫醒
            await peer.EnqueueAsync(new JobQueueItem("job-remote", DateTime.UtcNow));

            var taken = await consumer.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("job-remote", Assert.Single(taken).JobId);
        });
    }

    [Fact]
    public async Task Redis队列_释放共享连接的另一个实例不应摘掉本实例的唤醒订阅()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            var peer = new RedisJobQueue(
                queue.Connection,
                Options.Create(options),
                NullLogger<RedisJobQueue>.Instance);

            var consumer = Task.Run(async () => await queue.TakeDueAsync(10, TimeSpan.FromSeconds(60)));

            await Task.Delay(500);

            // 共享连接上的「退订频道」会摘掉同一频道上的全部处理器，这里必须不碰它
            await peer.DisposeAsync();

            // 绕过队列，直接从 Redis 入队 + 广播（模拟第三个宿主）
            await db.SortedSetAddAsync(options.DelayKey, "job-third", DateTime.UtcNow.Ticks);
            await db.PublishAsync(RedisChannel.Literal(options.WakeChannel), "job-third");

            var taken = await consumer.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("job-third", Assert.Single(taken).JobId);
        });
    }

    [Fact]
    public async Task Redis队列_共享连接不应被队列释放关闭()
    {
        await RunWithRealRedisAsync(async (queue, options, db) =>
        {
            var shared = queue.Connection;

            // 必须复用外层 options：再调一次 BuildOptions() 会拿到另一个随机前缀，
            // peer 写进去的键就落在清理范围之外，每跑一次留一个孤儿键。
            await using (var peer = new RedisJobQueue(
                shared,
                Options.Create(options),
                NullLogger<RedisJobQueue>.Instance))
            {
                await peer.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));
            }

            // 宿主自己的连接不归队列所有：队列释放后必须仍然可用
            var latency = await db.PingAsync().WaitAsync(TimeSpan.FromSeconds(3));

            Assert.True(latency >= TimeSpan.Zero);
            Assert.True(shared.IsConnected);
        });
    }

    [Fact]
    public async Task Redis队列_入队应通过真实发布订阅即时唤醒等待中的消费者()
    {
        // 60s 兜底上限：若唤醒失效，用例会等满 5s 的 WaitAsync 上限而失败
        await RunWithRealRedisAsync(async (queue, _, _) =>
        {
            var consumer = Task.Run(async () =>
                await queue.TakeDueAsync(10, TimeSpan.FromSeconds(60)));

            // 让消费者先进入等待
            await Task.Delay(300);

            await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));

            var taken = await consumer.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("job-1", Assert.Single(taken).JobId);
        });
    }

    [Fact]
    public async Task Redis队列_重复入队同一作业应幂等()
    {
        await RunWithRealRedisAsync(async (queue, _, _) =>
        {
            var due = DateTime.UtcNow;

            await queue.EnqueueAsync(new JobQueueItem("job-1", due));
            await queue.EnqueueAsync(new JobQueueItem("job-1", due));

            var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(200));

            // 有序集合按 member 去重：同一 jobId 只投递一次
            Assert.Equal("job-1", Assert.Single(taken).JobId);
        });
    }

    [Fact]
    public async Task Redis队列_入队参数校验_不需要连接()
    {
        var queue = new RedisJobQueue(Options.Create(new RedisJobQueueOptions()));

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await queue.EnqueueAsync(null!));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await queue.EnqueueAsync(new JobQueueItem(" ", DateTime.UtcNow)));
    }

    [Fact]
    public async Task Redis队列_取件数量校验_不需要连接()
    {
        var queue = new RedisJobQueue(Options.Create(new RedisJobQueueOptions()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await queue.TakeDueAsync(0, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void BuildConfiguration_连接串非空时应优先使用连接串()
    {
        var options = new RedisJobQueueOptions
        {
            ConnectionString = "1.2.3.4:6380,password=secret,ssl=true",
            Host = "ignored",
            Port = 9999,
            Auth = "also-ignored",
        };

        var configuration = RedisJobQueue.BuildConfiguration(options);

        Assert.Contains(configuration.EndPoints, endpoint =>
            endpoint.ToString() == "1.2.3.4:6380" ||
            endpoint.ToString() == "Unspecified/1.2.3.4:6380");
        Assert.Equal("secret", configuration.Password);
        Assert.True(configuration.Ssl);
    }

    [Fact]
    public void BuildConfiguration_未配连接串时用主机端口且不因启动期连不上而失败()
    {
        var options = new RedisJobQueueOptions
        {
            Host = "10.0.0.8",
            Port = 6380,
            Auth = "pass",
            Database = 3,
            Ssl = true,
        };

        var configuration = RedisJobQueue.BuildConfiguration(options);

        Assert.Contains(configuration.EndPoints, endpoint =>
            endpoint.ToString() == "10.0.0.8:6380" ||
            endpoint.ToString() == "Unspecified/10.0.0.8:6380");
        Assert.Equal("pass", configuration.Password);
        Assert.Equal(3, configuration.DefaultDatabase);
        Assert.True(configuration.Ssl);

        // Redis 不可用不能拖垮宿主启动：交给后台重连 + 队列层吞异常降级扫库
        Assert.False(configuration.AbortOnConnectFail);
    }
}
