namespace NetBpm.Platform.Client;

/// <summary>
/// 官方 .NET 客户端的连接与身份配置。
/// </summary>
/// <remarks>
/// <para>
/// 身份只有三种形态，必须<b>显式</b>选一种：JWT（<see cref="AccessToken"/> 或
/// <see cref="AccessTokenProvider"/>）、旧版 API Key（<see cref="ApiKey"/>）。
/// 两种都配，则请求同时带 <c>Authorization</c> 与 <c>X-Api-Key</c>——
/// 服务端以凭据为准，但把两把钥匙塞进同一个请求通常意味着配置写错了，
/// 因此这里<b>不</b>替调用方猜，也不做「优先级取一个」的静默行为。
/// </para>
/// <para>
/// <see cref="SubjectId"/> 是<b>代客调用</b>（token exchange 语义）：请求会带 <c>X-Subject-Id</c>，
/// 审批结论会记在这个人身上。它需要额外的 <c>workflow:task:submit:onbehalf</c> scope，
/// 而且<b>读</b>接口也会因此把可见性收窄到该主体——这不是「换个用户视角看看」，是真正的代表他。
/// </para>
/// </remarks>
public sealed class NetBpmClientOptions
{
    /// <summary>服务地址（默认本机 5080，与 <c>NetBpm.Rest</c> 的默认端口一致）。</summary>
    /// <remarks>末尾斜杠由客户端负责补齐，写不写都行。</remarks>
    public Uri BaseAddress { get; set; } = new("http://localhost:5080/");

    /// <summary>旧版服务级 API Key（内网单系统接入用；新接入应申请客户端密钥）。</summary>
    public string? ApiKey { get; set; }

    /// <summary>JWT（RS256）。与 <see cref="AccessTokenProvider"/> 二选一。</summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// 令牌提供者：每次请求前调用，用于短期令牌（有效期通常几十分钟）。
    /// </summary>
    /// <remarks>
    /// 做成委托而不是「调用方自己 set」：长驻进程里令牌会过期，而
    /// 「谁负责在 401 之前换一把新的」如果交给调用方，最常见的结局是每小时集体失败一次。
    /// </remarks>
    public Func<CancellationToken, ValueTask<string?>>? AccessTokenProvider { get; set; }

    /// <summary>代客调用的主体 id（发送 <c>X-Subject-Id</c>）。</summary>
    public string? SubjectId { get; set; }

    /// <summary>租户（发送 <c>X-Tenant-Id</c>）；单租户部署可以不设。</summary>
    public string? TenantId { get; set; }

    /// <summary>单次请求超时（默认 100 秒，与服务端默认请求超时一致）。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// 自定义传输层（单元测试注入假 handler，或接入方要挂 Polly / 代理 / mTLS）。
    /// </summary>
    /// <remarks>给出它时，<see cref="Timeout"/> 仍然作用在 <see cref="HttpClient"/> 上。</remarks>
    public HttpMessageHandler? HttpMessageHandler { get; set; }
}
