using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetBpm.Runtime.Hosting.JobQueue;
using Xunit;

namespace NetBpm.UnitTests;

/// <summary>
/// Redis 作业队列的<b>启动自检</b>：连不上必须打 Error，连得上打 Information。
/// </summary>
/// <remarks>
/// 失败用例完全确定性（指向一个必定连不上的端口）；成功用例对着真实 Redis，
/// 本机没有时用 <c>Assert.Skip</c> 跳过，不误报失败（口径同 <see cref="RedisJobQueueTests"/>）。
/// </remarks>
public class RedisJobQueueStartupProbeTests
{
    /// <summary>环境变量：完整连接串（如 <c>127.0.0.1:6379,password=xxx</c>），与 RedisJobQueueTests 保持一致。</summary>
    private const string ConnectionEnvironmentVariable = "NETBPM_TEST_REDIS";

    [Fact]
    public async Task 连接不可用时应打Error日志()
    {
        // 端口 1 是保留端口：本机必定拒绝连接，用例不依赖「恰好没有 Redis」
        var options = new RedisJobQueueOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };

        await using var queue = new RedisJobQueue(Options.Create(options), NullLogger<RedisJobQueue>.Instance);
        var logger = new CapturingLogger<RedisJobQueueStartupProbe>();
        using var probe = new RedisJobQueueStartupProbe(queue, Options.Create(options), logger);

        await probe.StartAsync(CancellationToken.None);

        var entry = await logger.FirstLog.WaitAsync(TimeSpan.FromSeconds(30));

        await probe.StopAsync(CancellationToken.None);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("连接不可用", entry.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1:1", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    [Fact]
    public async Task 连接可用时应打Information日志()
    {
        var options = new RedisJobQueueOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };

        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString.Trim();
        }

        await using var queue = new RedisJobQueue(Options.Create(options), NullLogger<RedisJobQueue>.Instance);
        var logger = new CapturingLogger<RedisJobQueueStartupProbe>();
        using var probe = new RedisJobQueueStartupProbe(queue, Options.Create(options), logger);

        await probe.StartAsync(CancellationToken.None);

        var entry = await logger.FirstLog.WaitAsync(TimeSpan.FromSeconds(30));

        await probe.StopAsync(CancellationToken.None);

        if (entry.Level == LogLevel.Error)
        {
            Assert.Skip(
                "没有可用的 Redis（默认 127.0.0.1:6379；带口令时用环境变量 "
                + $"{ConnectionEnvironmentVariable} 注入连接串）：{entry.Message}");

            return;
        }

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("连接正常", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>只关心「第一条日志是什么」的最小实现。</summary>
    private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource<CapturedLog> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CapturedLog> FirstLog => _first.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _first.TrySetResult(new CapturedLog(logLevel, formatter(state, exception), exception));
        }
    }
}
