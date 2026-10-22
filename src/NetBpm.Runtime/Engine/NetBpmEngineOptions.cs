namespace NetBpm.Runtime;

/// <summary>
/// 引擎宿主级配置（<c>NetBpm</c> 配置节中与「本进程如何参与调度」有关的部分）。
/// </summary>
public sealed class NetBpmEngineOptions
{
    /// <summary>
    /// 本宿主实例的标识，用于作业租约抢占与诊断。
    /// </summary>
    /// <remarks>
    /// 多实例部署时<b>必须各不相同</b>：租约以它为持有者，两个宿主用同一个 id
    /// 会互相误判对方已崩溃并重复接管作业。
    /// <para>为空时默认取 <c>MachineName#ProcessId</c>。</para>
    /// </remarks>
    public string? JobOwner { get; set; }

    /// <summary>
    /// 一次拾取的租约时长。执行超过该时长仍未完成的作业会被视为「宿主可能已崩溃」并可被接管。
    /// </summary>
    /// <remarks>
    /// 默认 5min：应远大于任何单条作业的合理执行时长，避免正常长作业被误判；
    /// 同时也要够短，让崩溃宿主的作业能较快被回收。
    /// </remarks>
    public TimeSpan JobLease { get; set; } = TimeSpan.FromMinutes(5);
}
