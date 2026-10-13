namespace NetBpm.Workflow;

/// <summary>
/// 流程结论的机器名常量（<see cref="WorkflowOutcomeRecord.Outcome"/> 的取值）。
/// </summary>
/// <remarks>
/// 结论是<b>业务语义</b>，引擎不认识它：由宿主在流程结束的那个服务任务里声明
/// （见 <c>IWorkflowService.CompleteAsync</c>），再落进业务数据。
/// 这里只固定两个最常见的取值，避免各处自己拼字符串导致对不上。
/// </remarks>
public static class WorkflowOutcomes
{
    /// <summary>流程审批通过。</summary>
    public const string Approved = "Approved";

    /// <summary>流程被拒绝（终态）。</summary>
    public const string Refused = "Refused";

    /// <summary>实例尚未结束时的状态字样（仅用于列表展示，不是结论）。</summary>
    public const string Running = "Running";
}
