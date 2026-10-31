using System.Text.Json;

namespace NetBpm.ExternalWorker;

/// <summary>
/// worker 的运行参数。全部可用环境变量覆盖（<c>NetBpm__xxx</c> 形如 appsettings 的层级）。
/// </summary>
public sealed class ExternalWorkerOptions
{
    /// <summary>配置段名。</summary>
    public const string SectionName = "NetBpm";

    /// <summary>平台服务地址，例如 <c>http://localhost:8080</c>。</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// 凭据：客户端模式下是 <c>nbc.{clientId}.{secret}</c>，走 <c>X-Api-Key</c> 头。
    /// 演示档（服务端 RequireCredential=false）留空即可。
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// worker 标识。必须是稳定的：锁按 workerId 归属，重启后换了 id，
    /// 之前领走但还没来得及回执的任务就只能等锁过期。
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    /// <summary>订阅的主题。</summary>
    public string[] Topics { get; set; } = ["invoice"];

    /// <summary>租户；不填表示不收窄（服务端仍有自己的默认租户规则）。</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// 领不到任务时，请求在服务端挂起的秒数（服务端短轮询，上限 30 秒）。
    /// 比「worker 自己 sleep 轮询」少了很多空往返，也让任务一到就能被立刻拿走。
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 单条任务的持锁时长（毫秒）。要覆盖「处理耗时 + 一次网络抖动」：
    /// 处理完但回执丢了时，锁到期会让别的 worker 重领并重复执行同一份业务。
    /// </summary>
    public int LockDurationMs { get; set; } = 120_000;

    /// <summary>每轮最多领几条。</summary>
    public int MaxTasks { get; set; } = 5;

    /// <summary>本地并发处理上限。</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>网络异常后的退避等待（毫秒）。</summary>
    public int ErrorBackoffMs { get; set; } = 3_000;

    /// <summary>构造。</summary>
    public ExternalWorkerOptions()
    {
    }

    /// <summary>终点/backstop：确保 URL 不带结尾斜杠，拼接路径时不会变成双斜杠。</summary>
    internal string NormalizedBaseUrl => BaseUrl.TrimEnd('/');
}

/// <summary>
/// worker 主动声明「这一步我做不了」，交给服务端决定是否扣重试、是否进死信。
/// </summary>
/// <remarks>
/// 与「异常」的区别：异常表示 worker 自己的锅（网络、反序列化），
/// 本异常表示业务口径上的失败（对端系统报错、校验不通过），应当消耗重试额度。
/// </remarks>
public sealed class ExternalTaskFailedException : Exception
{
    /// <summary>构造。</summary>
    /// <param name="message">失败摘要（进 <c>errorMessage</c>，会展示在运维端）。</param>
    /// <param name="details">失败详情。</param>
    /// <param name="remainingRetries">指定剩余重试次数；为空表示由服务端 -1。</param>
    /// <param name="retryAfter">指定下次可领时间；为空走服务端指数退避。</param>
    public ExternalTaskFailedException(
        string message,
        string? details = null,
        int? remainingRetries = null,
        DateTimeOffset? retryAfter = null)
        : base(message)
    {
        Details = details;
        RemainingRetries = remainingRetries;
        RetryAfter = retryAfter;
    }

    /// <summary>失败详情。</summary>
    public string? Details { get; }

    /// <summary>剩余重试次数。</summary>
    public int? RemainingRetries { get; }

    /// <summary>下次可领时间。</summary>
    public DateTimeOffset? RetryAfter { get; }
}

/// <summary>
/// 把对象转成服务端要的 <c>JsonElement</c> 字典。
/// </summary>
internal static class PayloadConverter
{
    internal static Dictionary<string, JsonElement> ToElements(IEnumerable<KeyValuePair<string, object?>> values) =>
        values.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.Ordinal);
}
