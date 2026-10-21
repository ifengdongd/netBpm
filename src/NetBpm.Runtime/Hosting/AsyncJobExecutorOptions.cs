namespace NetBpm.Runtime.Hosting;

/// <summary>异步作业执行器配置（<c>NetBpm:Jobs</c>）。</summary>
public sealed class AsyncJobExecutorOptions
{
    /// <summary>是否启用后台执行器；关闭时作业只能通过运维接口手动执行。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔；本轮有作业执行时立即继续下一轮，不等待。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>每轮最多处理的作业数。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>启动后延迟多久开始轮询（让宿主完成初始化 / 迁移）。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>outbox 发布器配置（<c>NetBpm:Outbox</c>）。</summary>
public sealed class OutboxPublisherOptions
{
    /// <summary>是否启用后台发布器。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询间隔。</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>每轮最多发布的事件数。</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>启动后延迟多久开始发布。</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(5);
}
