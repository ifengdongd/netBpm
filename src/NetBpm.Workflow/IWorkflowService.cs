using NetBpm.Engine.Execution;
using NetBpm.Runtime;

namespace NetBpm.Workflow;

/// <summary>
/// 审批流程应用层门面 —— 把散落在宿主的四件事收成一处：
/// <b>发起流程申请</b>、<b>获取流程环节信息</b>、<b>提交流程审批</b>、<b>获取审批历史记录</b>。
/// </summary>
/// <remarks>
/// <para>
/// 它只依赖引擎门面（<see cref="IProcessEngine"/>）与一个业务数据端口（<see cref="IWorkflowDataStore"/>），
/// 因此 HTTP 宿主、定时任务、控制台程序都可以直接调，宿主自己决定「怎么暴露、存哪张表」。
/// </para>
/// <para>四条主流程各自的职责边界：</para>
/// <list type="table">
///   <item><description><see cref="StartAsync"/>：登记业务单 → 写参数 → 起实例 → 返回首个环节；</description></item>
///   <item><description><see cref="FindInstanceAsync"/>：把「现在停在哪儿、要填什么、之前填了什么」一次给全；</description></item>
///   <item><description><see cref="SubmitAsync"/>：表单映射成变量 → 更新参数 → <b>先落业务数据再推进流程</b> → 记审批记录；</description></item>
///   <item><description><see cref="FindHistoryAsync"/>：优先引擎历史，内存宿主回退到业务审批记录合成。</description></item>
/// </list>
/// <para>
/// ⚠️ 引擎自己抛的 <c>EngineException</c>（如「任务已完成」）不从这里的异常类型里重复包装 ——
/// 宿主按需映射成业务错误码即可。
/// </para>
/// </remarks>
public interface IWorkflowService
{
    /// <summary>
    /// 取当前生效的流程定义 id（同 key 取版本最高者）；未部署返回 <see langword="null"/>。
    /// </summary>
    /// <param name="processDefinitionKey">流程 key（= BPMN 里 <c>process/@id</c>）。</param>
    /// <param name="ct">取消标记。</param>
    ValueTask<string?> ResolveDefinitionIdAsync(string processDefinitionKey, CancellationToken ct = default);

    /// <summary>
    /// <b>发起流程申请</b>：建流程实例，返回首个环节（含它的表单契约）。
    /// </summary>
    /// <param name="processDefinitionKey">流程 key（= BPMN 里 <c>process/@id</c>）。</param>
    /// <param name="request">申请人与业务上下文。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>新实例的环节视图。</returns>
    /// <exception cref="WorkflowValidationException">申请人缺失、流程未部署、参数名与审批元数据冲突。</exception>
    ValueTask<WorkflowInstanceView> StartAsync(
        string processDefinitionKey,
        StartWorkflowRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// <b>获取流程环节信息</b>：当前待办（含表单契约与审批选项）+ 流程变量 + 流程参数 + 审批结论。
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>
    /// 实例视图；引擎里没有运行态行、业务侧也没有登记与结论时返回 <see langword="null"/>（宿主转 404）。
    /// <para>已结束的实例仍会返回视图（<c>Running = false</c>），内容取自业务数据。</para>
    /// </returns>
    ValueTask<WorkflowInstanceView?> FindInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>查待办（候选人 / 办理人 / 流程实例任一条件）。</summary>
    /// <param name="query">查询条件。</param>
    /// <param name="ct">取消标记。</param>
    /// <exception cref="WorkflowValidationException">一个条件都没给。</exception>
    ValueTask<IReadOnlyList<WorkflowTaskView>> FindTasksAsync(TaskQuery query, CancellationToken ct = default);

    /// <summary>
    /// <b>提交流程审批</b>：表单值按流程定义映射成流程变量，参数按 key 更新，然后推进流程。
    /// </summary>
    /// <param name="taskId">待办任务 id。</param>
    /// <param name="request">表单值 + 参数更新 + 办理人。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>
    /// 提交后的实例视图；任务没有所属流程实例时返回 <see langword="null"/>（宿主转 204）。
    /// </returns>
    /// <exception cref="WorkflowNotFoundException">任务不存在（可能已完成）。</exception>
    /// <exception cref="WorkflowValidationException">表单校验不通过、参数名与审批元数据冲突。</exception>
    ValueTask<WorkflowInstanceView?> SubmitAsync(
        string taskId,
        SubmitWorkflowTaskRequest? request,
        CancellationToken ct = default);

    /// <summary>
    /// <b>获取审批历史记录</b>：环节序列 + 审批记录（谁在什么时候批了什么）+ 审批结论。
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>历史视图；引擎与业务侧都查不到该实例时返回 <see langword="null"/>。</returns>
    ValueTask<WorkflowHistoryView?> FindHistoryAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>查业务登记列表（新的在前），列表页用。</summary>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyList<WorkflowInstanceSummary>> FindInstancesAsync(CancellationToken ct = default);

    /// <summary>
    /// 流程结束时的业务回调：把「走到了这个服务任务」翻译成一条业务结论落盘。
    /// </summary>
    /// <remarks>
    /// 给宿主的服务任务委托调用（<c>leave.ApproveComplete</c> 之类），委托因此只需声明结论名：
    /// <c>await workflow.CompleteAsync(execution, WorkflowOutcomes.Approved, ct);</c>
    /// </remarks>
    /// <param name="execution">委托拿到的执行上下文。</param>
    /// <param name="outcome">结论机器名，见 <see cref="WorkflowOutcomes"/>。</param>
    /// <param name="ct">取消标记。</param>
    ValueTask CompleteAsync(IDelegateExecution execution, string outcome, CancellationToken ct = default);
}
