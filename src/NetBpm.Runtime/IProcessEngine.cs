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

    /// <summary>部署流程定义（解析 → 校验 → 落库）。</summary>
    ValueTask<ProcessDefinition> DeployAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
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

    /// <summary>查全部已部署的流程定义（流程部署列表数据源，按部署时间倒序）。</summary>
    ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(CancellationToken ct = default);

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
}
