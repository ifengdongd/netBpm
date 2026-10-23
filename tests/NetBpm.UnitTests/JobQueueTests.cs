using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetBpm.Engine.Jobs;
using NetBpm.Runtime.Hosting.JobQueue;

namespace NetBpm.UnitTests;

/// <summary>
/// 延迟作业队列：配置选择与时序契约。
/// </summary>
/// <remarks>
/// 队列<b>只传作业 id 与到期时间</b>的提示，不传业务负载；
/// 真正的作业数据与租约在 <c>ACT_RU_JOB</c> 里。因此这些用例只验证「提示通道」本身。
/// </remarks>
public class JobQueueTests
{
    private static IServiceCollection BuildServices(string? provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetBpm:Jobs:Queue:Provider"] = provider,
                ["NetBpm:Jobs:Queue:Redis:Host"] = "127.0.0.1",
                ["NetBpm:Jobs:Queue:Redis:Port"] = "6379",
                ["NetBpm:Jobs:Queue:Redis:KeyPrefix"] = "opms:",
            })
            .Build();

        return new ServiceCollection().AddNetBpmJobQueue(configuration, "NetBpm:Jobs:Queue");
    }

    [Fact]
    public void Provider_未配置时_默认用内存队列()
    {
        using var provider = BuildServices(null).BuildServiceProvider();

        Assert.Equal("memory", provider.GetRequiredService<IJobQueue>().Name);
    }

    [Fact]
    public void Provider_Memory_用内存队列()
    {
        using var provider = BuildServices("Memory").BuildServiceProvider();

        Assert.IsType<InMemoryJobQueue>(provider.GetRequiredService<IJobQueue>());
    }

    [Fact]
    public void Provider_Redis_用Redis队列_且大小写不敏感()
    {
        using var provider = BuildServices("redis").BuildServiceProvider();

        Assert.IsType<RedisJobQueue>(provider.GetRequiredService<IJobQueue>());
    }

    [Fact]
    public void Provider_未知_应响亮报错_而不是静默兜底()
    {
        var services = BuildServices("Kafka");

        // 配置了不存在的实现时立刻失败，避免「以为在用 Redis，其实在扫库」这类静默退化
        Assert.Throws<InvalidOperationException>(
            () => services.BuildServiceProvider().GetRequiredService<IJobQueue>());
    }

    [Fact]
    public void Redis队列_取值未知_抛出的异常要说明可选值()
    {
        var services = BuildServices("RabbitMQ");

        var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IJobQueue>());

        Assert.Contains(nameof(JobQueueProvider.Memory), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(JobQueueProvider.Redis), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 内存队列_未到期的条目不应被取走()
    {
        await using var queue = new InMemoryJobQueue(TimeSpan.FromMilliseconds(50));

        await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow.AddMinutes(5)));

        var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(30));

        Assert.Empty(taken);
    }

    [Fact]
    public async Task 内存队列_到期的条目按到期时间升序取走()
    {
        await using var queue = new InMemoryJobQueue(TimeSpan.FromMilliseconds(50));

        var now = DateTime.UtcNow;

        await queue.EnqueueAsync(new JobQueueItem("job-late", now.AddSeconds(2)));
        await queue.EnqueueAsync(new JobQueueItem("job-early", now.AddSeconds(-2)));
        await queue.EnqueueAsync(new JobQueueItem("job-mid", now.AddSeconds(-1)));

        var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(30));

        Assert.Equal(new[] { "job-early", "job-mid" }, taken.Select(item => item.JobId));
    }

    [Fact]
    public async Task 内存队列_入队应能即时唤醒等待中的消费者()
    {
        // 用 60s 的兜底等待上限：若不是被唤醒而是等到兜底超时，用例会超时失败
        await using var queue = new InMemoryJobQueue(TimeSpan.FromSeconds(60));

        var consumer = Task.Run(async () =>
            await queue.TakeDueAsync(10, TimeSpan.FromSeconds(60)));

        // 让消费者先进入等待
        await Task.Delay(100);

        await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));

        var taken = await consumer.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(taken);
        Assert.Equal("job-1", taken[0].JobId);
    }

    [Fact]
    public async Task 内存队列_连续入队不应丢失唤醒()
    {
        await using var queue = new InMemoryJobQueue(TimeSpan.FromMilliseconds(50));

        // 先入队 5 条（此时无消费者，信号量计数应保持 0/1 而不累积）
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(new JobQueueItem($"job-{i}", DateTime.UtcNow));
        }

        var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(50));

        // 计数累积会导致这里只取到 1 条（其余被空转消耗）
        Assert.Equal(5, taken.Count);
    }

    [Fact]
    public async Task 内存队列_Release应把条目放回队首供下一轮立即取()
    {
        await using var queue = new InMemoryJobQueue(TimeSpan.FromMilliseconds(50));

        await queue.EnqueueAsync(new JobQueueItem("job-1", DateTime.UtcNow));

        var taken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(30));
        Assert.Single(taken);

        await queue.ReleaseAsync("job-1");

        var retaken = await queue.TakeDueAsync(10, TimeSpan.FromMilliseconds(30));

        Assert.Equal("job-1", Assert.Single(retaken).JobId);
    }

    [Fact]
    public async Task 内存队列_无到期条目时应等到超时再返回_而不是立刻返回空()
    {
        await using var queue = new InMemoryJobQueue(TimeSpan.FromSeconds(60));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var taken = await queue.TakeDueAsync(
            10,
            TimeSpan.FromMilliseconds(200),
            CancellationToken.None);

        stopwatch.Stop();

        Assert.Empty(taken);

        // 关键：等待时长受调用方传入的超时控制，绝不超过它
        Assert.True(
            stopwatch.ElapsedMilliseconds >= 150,
            $"应至少等待到超时，实际只等了 {stopwatch.ElapsedMilliseconds}ms。");
    }
}
