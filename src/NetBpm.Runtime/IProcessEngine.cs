using NetBpm.Bpmn.Model;
using NetBpm.Engine.Command;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 引擎门面。对应 Activiti 的 <c>ProcessEngine</c> 上最常用的几个服务方法。
/// <para>REST 层（P9）只依赖本接口，不直接触碰 <c>ICommandExecutor</c> 与仓储。</para>
/// </summary>
public interface IProcessEngine
{
    /// <summary>执行任意命令（一个命令 = 一个事务）。</summary>
    ValueTask<TResult> ExecuteAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);

    /// <summary>
    /// 部署流程定义（解析 → 校验 → 落库）。
    /// <paramref name="tenantId"/> 为部署租户（B2.3）；<see langword="null"/> 表示无租户。
    /// </summary>
    ValueTask<ProcessDefinition> DeployAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>启动流程实例，返回流程实例 id（= 根执行 id）。</summary>
    ValueTask<string> StartProcessInstanceAsync(
        string processDefinitionId,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default);

    /// <summary>取流程定义的 BPMN XML（设计器回显契约，见 §6.3）。</summary>
    ValueTask<string?> GetProcessDefinitionXmlAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// 查全部已部署的流程定义（流程部署列表数据源，按部署时间倒序）；
    /// <paramref name="tenantId"/> 非空时按租户过滤（B2.3）。
    /// </summary>
    ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>取流程模型（校验 / 查询用）。</summary>
    ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>查待办（P7 / P9）。</summary>
    ValueTask<IReadOnlyList<TaskEntity>> FindTasksAsync(TaskQuery query, CancellationToken ct = default);

    /// <summary>
    /// 查流程实例的变量（P5）。变量落在流程实例执行上，读取时沿作用域链合并。
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, object?>> GetVariablesAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 删除流程实例作用域上的变量（B2.4）：沿作用域链查找并删除，
    /// longString 的字节行一并清理；历史行保留（历史账本只增不删）。
    /// </summary>
    /// <returns>变量是否存在并被删除。</returns>
    ValueTask<bool> RemoveVariableAsync(string processInstanceId, string name, CancellationToken ct = default);

    /// <summary>
    /// 写任务局部变量（B2.4）：变量挂在 <c>ACT_RU_VARIABLE.TASK_ID_</c> 行上，
    /// 随任务完成 / 删除而清理，不写回流程实例作用域。
    /// </summary>
    ValueTask SetTaskVariableAsync(
        string taskId,
        string name,
        object? value,
        CancellationToken ct = default);

    /// <summary>读单个任务局部变量（B2.4）；未命中返回 <see langword="null"/>（不上溯到流程实例作用域）。</summary>
    ValueTask<object?> GetTaskVariableAsync(string taskId, string name, CancellationToken ct = default);

    /// <summary>读任务的全部局部变量（B2.4，不含流程实例作用域的变量）。</summary>
    ValueTask<IReadOnlyDictionary<string, object?>> GetTaskVariablesAsync(string taskId, CancellationToken ct = default);

    /// <summary>删除单个任务局部变量（B2.4）；未命中返回 <see langword="false"/>。</summary>
    ValueTask<bool> RemoveTaskVariableAsync(string taskId, string name, CancellationToken ct = default);

    /// <summary>查流程实例的历史活动（流程图着色数据源，§6.2 第 5 项）。</summary>
    ValueTask<IReadOnlyList<HistoricActivityInstance>> FindHistoricActivitiesAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>查历史流程实例；实例结束后仍可查到（P9「查历史」）。</summary>
    ValueTask<HistoricProcessInstance?> FindHistoricProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>查流程实例的历史任务（已办查询 / 审计）。</summary>
    ValueTask<IReadOnlyList<HistoricTaskInstance>> FindHistoricTasksAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 取流程图数据（P9「流程图数据」）：BPMN XML + 活动高亮序列。
    /// <para>实例既不存在于执行树也无历史记录时返回 <see langword="null"/>（端点转 404）。</para>
    /// </summary>
    ValueTask<DiagramData?> GetDiagramAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>完成任务，并继续推进流程（P7）。</summary>
    ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default);

    /// <summary>认领 / 取消认领任务（P7）。<paramref name="userId"/> 为 <see langword="null"/> 即取消认领。</summary>
    ValueTask ClaimTaskAsync(string taskId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// 改派任务（转办）：把待办的办理人直接改为指定用户（管理动作，不校验是否已被认领）。
    /// <paramref name="assignee"/> 为 <see langword="null"/> 表示退回未分配。
    /// </summary>
    ValueTask SetAssigneeAsync(string taskId, string? assignee, CancellationToken ct = default);

    /// <summary>
    /// 流程实例跳转：把运行中的实例从当前等待节点搬到指定节点并继续推进（到下一个等待点）。
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="targetActivityId">目标节点 id（节点 / 网关 / 结束事件）。</param>
    /// <param name="reason">跳转原因（写进被删任务与活动的历史）。</param>
    ValueTask MoveActivityToAsync(
        string processInstanceId,
        string targetActivityId,
        string? reason = null,
        CancellationToken ct = default);
}
