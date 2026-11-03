using System.Globalization;

namespace NetBpm.ServerTaskWorker;

/// <summary>
/// worker 的运行参数。
/// </summary>
/// <remarks>
/// <para>默认值刻意就是「本机演示能直接跑」的那一套：5081 引擎、3007 业务接口、两个请假主题。</para>
/// <para>
/// 环境变量有两种写法：标准层级 <c>Worker__NetBpmBaseUrl</c>，以及与原 Node 版一致的
/// <c>NETBPM_BASE_URL</c> / <c>WORKER_TOPICS</c> 等（映射见 <see cref="FromLegacyEnvironment"/>）。
/// 后者优先级更高 —— 换语言重写时，运维脚本不用改。
/// </para>
/// </remarks>
public sealed class WorkerOptions
{
    /// <summary>配置段名。</summary>
    public const string SectionName = "Worker";

    /// <summary>NetBpm 引擎地址。控制台与引擎端点同端口。</summary>
    public string NetBpmBaseUrl { get; set; } = "http://localhost:5081";

    /// <summary>
    /// 客户端密钥全文：<c>nbc.{clientId}.{secret}</c>。
    /// </summary>
    /// <remarks>
    /// 控制台「接入管理 → 客户端」签发，只显示一次。
    /// ⚠️ 不要自己拼 <c>nbc.</c> 前缀 —— 接口返回的字符串已经带前缀，拼一次就成了 <c>nbc.nbc.…</c>。
    /// </remarks>
    public string ClientKey { get; set; } = string.Empty;

    /// <summary>租户。请假流程的定义部署在 <c>default</c>，收窄错了会一条都领不到。</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>
    /// worker 标识 —— 锁的持有者。
    /// </summary>
    /// <remarks>
    /// 回执时必须与领取时一致，所以它要「每个进程一个、重启就变」。
    /// 别写成固定的 <c>worker</c>：两个副本同 id 会互相抢锁，回执撞 409。
    /// </remarks>
    public string WorkerId { get; set; } = DefaultWorkerId();

    /// <summary>订阅的主题。BPMN 里 <c>activiti:topic</c> 写什么这里就写什么，大小写敏感。</summary>
    public string[] Topics { get; set; } = ["approveComplete", "refuseComplete"];

    /// <summary>一次最多领几条（服务端上限 100）。</summary>
    public int MaxTasks { get; set; } = 5;

    /// <summary>请求锁多久（毫秒）。必须显著大于单条处理超时，否则还在干活锁就过期了。</summary>
    public int LockDurationMs { get; set; } = 120_000;

    /// <summary>fetch 在没有任务时最多等几秒（服务端上限 30，内部每秒看一次）。</summary>
    public int WaitSeconds { get; set; } = 20;

    /// <summary>单条业务处理的超时（毫秒）。超时按「可重试失败」处理。</summary>
    public int TaskTimeoutMs { get; set; } = 30_000;

    /// <summary>领取失败时的指数退避上限（秒）。</summary>
    public int ErrorBackoffMaxSeconds { get; set; } = 30;

    /// <summary>是否在本进程内同时起 worker。<c>false</c> 就只跑 HTTP 业务接口。</summary>
    public bool RunWorker { get; set; } = true;

    /// <summary>本进程监听地址。容器/局域网部署把它改成 <c>http://0.0.0.0:3007</c>。</summary>
    public string ListenUrl { get; set; } = "http://localhost:3007";

    /// <summary>
    /// 单据仓储的落盘文件。相对路径按「进程当前目录」解析。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这只是让样例开箱能跑的替身。接真实系统时换成你们的仓储（MySQL / Oracle / 内部服务），
    /// 幂等 SQL 写法见 <c>docs/leave-worker-3007-integration.md</c> §3.4。
    /// </remarks>
    public string DataFile { get; set; } = "data/leave-bills.json";

    /// <summary>拼路径用：去掉结尾斜杠，避免出现双斜杠。</summary>
    public string NormalizedBaseUrl => NetBpmBaseUrl.TrimEnd('/');

    /// <summary>是否配了客户端密钥。没配时降级成「只有业务接口」，不领任务。</summary>
    public bool HasClientKey => !string.IsNullOrWhiteSpace(ClientKey);

    /// <summary>
    /// 启动前自检：只拦「配错了」，不拦「还没配」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两者的处置方式不同，别混在一起：
    /// 密钥格式不对 / 主题为空属于**配错**，带病启动只会用 401 或空转刷日志，直接退出；
    /// 密钥为空属于**还没接上**（第一次跑、本地只想看业务接口），此时照样起 HTTP 服务，
    /// 由 <see cref="HasClientKey"/> 决定要不要起 worker。
    /// </para>
    /// <para>
    /// 早先这里把「没配密钥」也当成致命错误，结果从 IDE 直接按运行会静默退出 —— 看日志的空当进程已经没了。
    /// </para>
    /// </remarks>
    /// <param name="error">校验失败的原因；成功时为 <c>null</c>。</param>
    /// <returns>配置是否可用于启动。</returns>
    public bool TryValidate(out string? error)
    {
        if (HasClientKey && !ClientKey.StartsWith("nbc.", StringComparison.Ordinal))
        {
            error = "客户端密钥必须是 nbc.{clientId}.{secret} 形式（控制台签发的原文，别只截 secret 段）。"
                + $"当前值以「{ClientKey[..Math.Min(ClientKey.Length, 8)]}…」开头。";
            return false;
        }

        if (Topics.Length == 0)
        {
            error = "订阅主题为空：设置环境变量 WORKER_TOPICS（或 Worker__Topics），"
                + "与 BPMN 里 activiti:topic 的值严格对应。";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// 把配置规整成可直接使用的形态：URL 去斜杠、主题去空去重、数值夹到合法区间。
    /// </summary>
    /// <remarks>
    /// 主题必须在这里规整：配置绑定虽然会把 <c>"a,b"</c> 拆成数组，但不会去空格，
    /// 而 <c>approveComplete, refuseComplete</c>（逗号后一个空格）会让后一个主题永远领不到任务。
    /// </remarks>
    public void Normalize()
    {
        NetBpmBaseUrl = string.IsNullOrWhiteSpace(NetBpmBaseUrl) ? "http://localhost:5081" : NetBpmBaseUrl.Trim();
        ClientKey = ClientKey.Trim();
        TenantId = string.IsNullOrWhiteSpace(TenantId) ? "default" : TenantId.Trim();
        WorkerId = string.IsNullOrWhiteSpace(WorkerId) ? DefaultWorkerId() : WorkerId.Trim();
        ListenUrl = string.IsNullOrWhiteSpace(ListenUrl) ? "http://localhost:3007" : ListenUrl.Trim();

        Topics = Topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Select(topic => topic.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        MaxTasks = Math.Clamp(MaxTasks, 1, 100);
        LockDurationMs = Math.Max(LockDurationMs, 1_000);
        WaitSeconds = Math.Clamp(WaitSeconds, 0, 30);
        TaskTimeoutMs = Math.Max(TaskTimeoutMs, 1_000);
        ErrorBackoffMaxSeconds = Math.Clamp(ErrorBackoffMaxSeconds, 1, 300);
    }

    /// <summary>
    /// 与原 Node 版一致的环境变量名 → 配置键。
    /// </summary>
    /// <remarks>
    /// 只映射「实际存在」的变量：返回空集合时不会覆盖 appsettings 里的任何值。
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string?>> FromLegacyEnvironment()
    {
        (string EnvironmentName, string ConfigurationKey)[] map =
        [
            ("NETBPM_BASE_URL", "Worker:NetBpmBaseUrl"),
            ("NETBPM_CLIENT_KEY", "Worker:ClientKey"),
            ("NETBPM_TENANT_ID", "Worker:TenantId"),
            ("WORKER_ID", "Worker:WorkerId"),
            ("WORKER_TOPICS", "Worker:Topics"),
            ("WORKER_MAX_TASKS", "Worker:MaxTasks"),
            ("WORKER_LOCK_MS", "Worker:LockDurationMs"),
            ("WORKER_WAIT_SECONDS", "Worker:WaitSeconds"),
            ("WORKER_TASK_TIMEOUT_MS", "Worker:TaskTimeoutMs"),
            ("RUN_WORKER", "Worker:RunWorker"),
            ("LEAVE_BILL_FILE", "Worker:DataFile"),
            ("LISTEN_URL", "Worker:ListenUrl"),
        ];

        foreach (var (environmentName, configurationKey) in map)
        {
            var value = Environment.GetEnvironmentVariable(environmentName);

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return new KeyValuePair<string, string?>(configurationKey, value);
            }
        }

        // PORT 单独处理：Node 生态里它是惯例变量，这里换算成监听地址。
        var port = Environment.GetEnvironmentVariable("PORT");

        if (!string.IsNullOrWhiteSpace(port)
            && int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65_535)
        {
            yield return new KeyValuePair<string, string?>("Worker:ListenUrl", $"http://localhost:{parsed}");
        }
    }

    private static string DefaultWorkerId() =>
        $"stw-{Environment.MachineName.ToLowerInvariant()}-{Environment.ProcessId}-{Random.Shared.Next(0x100000, 0xFFFFFF):x}";
}
