using System.ComponentModel.DataAnnotations;

namespace NetBpm.Runtime.Hosting.JobQueue;

/// <summary>
/// Redis 延迟队列配置（配置节 <c>NetBpm:Jobs:Queue:Redis</c>）。
/// </summary>
public sealed class RedisJobQueueOptions
{
    /// <summary>Redis 主机地址。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Redis 端口。</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 6379;

    /// <summary>
    /// Redis 访问口令（<c>requirepass</c>）；为空表示无口令。
    /// </summary>
    /// <remarks>
    /// 生产环境请放到用户机密 / 环境变量 / 密钥管理服务里，不要把明文口令提交进仓库。
    /// </remarks>
    public string? Auth { get; set; }

    /// <summary>逻辑库编号。</summary>
    [Range(0, 15)]
    public int Database { get; set; }

    /// <summary>
    /// 键前缀：多系统共用同一 Redis 实例时的命名空间隔离。
    /// </summary>
    /// <remarks>建议以英文冒号结尾（如 <c>opms:</c>），便于在 redis-cli 里按层级 <c>SCAN</c>。</remarks>
    public string KeyPrefix { get; set; } = "netbpm:";

    /// <summary>是否启用 TLS。</summary>
    public bool Ssl { get; set; }

    /// <summary>建连超时。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 完整连接串；<b>非空时优先生效</b>，此时 <see cref="Host"/> / <see cref="Port"/> /
    /// <see cref="Auth"/> / <see cref="Database"/> / <see cref="Ssl"/> 均被忽略。
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// 可见性超时：条目被取走后在这个时长内不会被其它消费者再取到。
    /// </summary>
    /// <remarks>
    /// 必须 <b>≥ 作业租约时长</b>（<c>NetBpm:Jobs:LeaseSeconds</c>）。
    /// 否则会出现「数据库租约还没过期，Redis 已把条目放回就绪队列」，
    /// 让别的宿主白白抢一次（不会重复执行，只是多出无效竞争）。
    /// 默认 6min，对应默认租约 5min。
    /// </remarks>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(6);

    /// <summary>就绪列表键。</summary>
    public string ReadyKey => Normalize(KeyPrefix) + "job:ready";

    /// <summary>延迟有序集合键（score = 到期时间 ticks）。</summary>
    public string DelayKey => Normalize(KeyPrefix) + "job:delay";

    /// <summary>在途有序集合键（score = 可见性超时到期时刻 ticks）。</summary>
    public string InflightKey => Normalize(KeyPrefix) + "job:inflight";

    /// <summary>跨宿主唤醒频道。</summary>
    public string WakeChannel => Normalize(KeyPrefix) + "job:wake";

    private static string Normalize(string prefix) =>
        string.IsNullOrEmpty(prefix) || prefix[^1] == ':'
            ? prefix
            : prefix + ":";
}
