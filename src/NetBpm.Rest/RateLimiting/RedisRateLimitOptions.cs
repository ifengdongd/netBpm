namespace NetBpm.Rest.RateLimiting;

/// <summary>
/// 全局配额的 Redis 连接配置（配置节 <c>NetBpm:RateLimit:Redis</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与作业队列（<c>NetBpm:Jobs:Queue:Redis</c>）<b>刻意分开配置</b>：两者通常指向同一个 Redis 实例，
/// 但一旦想拆开（队列走专属实例、配额走共享实例）就不该改代码；反过来若强行共用一个配置节，
/// 「只想改队列地址」会连带把配额搬走。
/// </para>
/// <para>
/// 口令不要写进 <c>appsettings.json</c>：用 <c>NetBpm__RateLimit__Redis__Auth</c> 环境变量
/// 或 <c>NetBpm__RateLimit__Redis__ConnectionString</c> 注入。
/// </para>
/// </remarks>
public sealed class RedisRateLimitOptions
{
    /// <summary>Redis 主机地址。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>Redis 端口。</summary>
    public int Port { get; set; } = 6379;

    /// <summary>Redis 访问口令（<c>requirepass</c>）；为空表示无口令。</summary>
    public string? Auth { get; set; }

    /// <summary>逻辑库编号（连接串模式下被忽略，以连接串为准）。</summary>
    public int Database { get; set; }

    /// <summary>键前缀：多系统共用同一 Redis 时的命名空间隔离，建议以冒号结尾。</summary>
    public string KeyPrefix { get; set; } = "netbpm:";

    /// <summary>是否启用 TLS。</summary>
    public bool Ssl { get; set; }

    /// <summary>建连超时。</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 单次配额判定的命令超时。
    /// </summary>
    /// <remarks>
    /// 限流在<b>每个请求</b>的关键路径上：这里必须短，宁可判定失败（按
    /// <see cref="FailOpenOnError"/> 处理）也不能让请求排队等 Redis。默认 200ms。
    /// </remarks>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>完整连接串；<b>非空时优先生效</b>，此时 Host / Port / Auth / Database / Ssl 均被忽略。</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Redis 不可用时是否<b>放行</b>（默认 <see langword="true"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 fail-open 与「分区表满时放行」是同一个判断：限流是保护手段，
    /// 让「配额基础设施故障」变成「全站 429」是把一个小故障放大成一次全站事故。
    /// </para>
    /// <para>
    /// 代价是 Redis 故障期间失去限流能力（包括登录口的爆破防护）。若某个环境认为
    /// 「宁可拒绝也不能失守」，把它设成 <see langword="false"/>——但要同时接受
    /// 「Redis 抖动 = 全站 429」这个新故障模式。
    /// </para>
    /// </remarks>
    public bool FailOpenOnError { get; set; } = true;

    /// <summary>规范化后的键前缀（保证以 <c>:</c> 结尾）。</summary>
    public string NormalizedKeyPrefix =>
        string.IsNullOrEmpty(KeyPrefix) ? string.Empty : KeyPrefix[^1] == ':' ? KeyPrefix : KeyPrefix + ":";
}
