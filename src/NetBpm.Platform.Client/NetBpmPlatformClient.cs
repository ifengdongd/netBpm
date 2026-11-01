namespace NetBpm.Platform.Client;

/// <summary>
/// NetBpm 平台官方 .NET 客户端。
/// </summary>
/// <remarks>
/// <para>
/// 一句话用法：
/// <code>
/// using var client = new NetBpmPlatformClient(new NetBpmClientOptions
/// {
///     BaseAddress = new Uri("https://netbpm.internal/"),
///     AccessTokenProvider = ct =&gt; tokenSource.GetTokenAsync(ct),
/// });
///
/// var page = await client.Instances.ListAsync(new InstanceQuery(ProcessKey: "leave_process"));
/// </code>
/// </para>
/// <para>
/// <b>为什么模型直接复用 <c>NetBpm.Platform.Contracts</c></b>：这份契约就是服务端生成 OpenAPI 快照的
/// 同一份类型定义。再从快照生成一套 DTO，等于在同一个仓库里维护两份「真源」，
/// 迟早会出现「SDK 的字段名对、服务端的字段名也対，但两者不一样」。
/// 需要生成的是仓库外的语言（TS / Java），见 <c>tools/NetBpm.SdkGen</c>。
/// </para>
/// <para>
/// <b>线程安全</b>：客户端与四个子客户端都是无状态的，可跨线程共用（底层 <see cref="HttpClient"/> 同样设计如此）。
/// 唯一例外是 <see cref="NetBpmClientOptions"/>：它在构造时被读取，之后修改不会生效。
/// </para>
/// </remarks>
public sealed class NetBpmPlatformClient : IDisposable
{
    private readonly PlatformTransport _transport;

    /// <summary>按配置创建客户端（自建并持有 <see cref="HttpClient"/>）。</summary>
    /// <param name="options">连接与身份配置。</param>
    public NetBpmPlatformClient(NetBpmClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _transport = new PlatformTransport(options);

        Processes = new ProcessesClient(_transport);
        Instances = new InstancesClient(_transport);
        Tasks = new TasksClient(_transport);
        AdminProcesses = new AdminProcessesClient(_transport);
    }

    /// <summary>
    /// 接管一个已经配好的 <see cref="HttpClient"/>（配合 <c>IHttpClientFactory</c> 使用，不负责释放它）。
    /// </summary>
    /// <remarks>
    /// 用这个构造时，<see cref="HttpClient.BaseAddress"/> 必须已经指向服务根（末尾带斜杠），
    /// 而身份头来自 <paramref name="options"/>：<c>HttpClient</c> 管连接，<c>options</c> 管身份。
    /// 这样「连接池怎么配」与「用谁的身份调」可以分开演进。
    /// </remarks>
    /// <param name="httpClient">已配置的 <see cref="HttpClient"/>。</param>
    /// <param name="options">身份配置（可为空，表示匿名）。</param>
    public NetBpmPlatformClient(HttpClient httpClient, NetBpmClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _transport = new PlatformTransport(httpClient, options);

        Processes = new ProcessesClient(_transport);
        Instances = new InstancesClient(_transport);
        Tasks = new TasksClient(_transport);
        AdminProcesses = new AdminProcessesClient(_transport);
    }

    /// <summary>流程目录（只读）。</summary>
    public ProcessesClient Processes { get; }

    /// <summary>流程实例（发起 / 查询 / 终止）。</summary>
    public InstancesClient Instances { get; }

    /// <summary>待办与审批提交。</summary>
    public TasksClient Tasks { get; }

    /// <summary>管理面：流程目录覆盖行。</summary>
    public AdminProcessesClient AdminProcesses { get; }

    /// <inheritdoc />
    public void Dispose() => _transport.Dispose();
}
