namespace NetBpm.Demo.ExternalTasks;

/// <summary>
/// 外部任务的「注册」侧：一个 handler 声明自己订阅哪个 topic，并给出回执时要写回的变量。
/// <para>
/// 真实项目里这段代码跑在<b>客户系统的独立进程</b>里（见 <c>samples/NetBpm.ExternalWorker</c>，
/// 用 HTTP 拉取任务）。Demo 为了自证闭环，把 handler 注册进本宿主、由
/// <see cref="DemoExternalTaskWorker"/> 在进程内领取执行 —— 换到真实部署，只需把 worker
/// 换成「HTTP 轮询」，handler 一行都不用改。
/// </para>
/// </summary>
public interface IExternalTaskHandler
{
    /// <summary>订阅的主题，必须与 BPMN 里的 <c>activiti:topic</c> 逐字一致。</summary>
    string Topic { get; }

    /// <summary>
    /// 执行任务并返回要写回流程的变量。返回 <see langword="null"/> 表示不需要写回。
    /// <para>抛异常会被 worker 转成失败回执（扣一次重试），流程原地不动。</para>
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, object?>?> ExecuteAsync(
        ExternalTaskInvocation invocation,
        CancellationToken ct = default);
}

/// <summary>worker 领到的一条任务（只有执行 handler 需要的字段，不暴露引擎实体）。</summary>
/// <param name="TaskId">外部任务 id（幂等键）。</param>
/// <param name="ProcessInstanceId">流程实例 id。</param>
/// <param name="ActivityId">停留的节点 id。</param>
/// <param name="BusinessKey">业务键（可空）。</param>
/// <param name="Payload">登记那一刻的变量快照。</param>
public sealed record ExternalTaskInvocation(
    string TaskId,
    string ProcessInstanceId,
    string ActivityId,
    string? BusinessKey,
    IReadOnlyDictionary<string, object?> Payload);
