using NetBpm.Bpmn.Model;
using NetBpm.Bpmn.Validation;
using NetBpm.Engine.Command;
using NetBpm.Engine.Execution;
using NetBpm.Engine.History;
using NetBpm.Engine.Tasks;
using NetBpm.Persistence.Deployment;

namespace NetBpm.Runtime;

/// <summary>
/// 部署结果：流程定义 + 不阻断部署的<b>提示级</b>校验问题。
/// <para>
/// 对应 Activiti 把 <c>ValidationError.isWarning()</c> 为真的问题随解析结果带出的做法：
/// 调用方（设计器）可以把它们显示为"能部署，但建议检查"，而不是当作失败。
/// </para>
/// </summary>
/// <param name="ProcessDefinition">已落库的流程定义。</param>
/// <param name="Warnings">提示级校验结果（为空表示没有任何提示）。</param>
public sealed record DeployResult(
    ProcessDefinition ProcessDefinition,
    IReadOnlyList<ValidationError> Warnings);

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
    /// <para>
    /// 存在<b>阻断级</b>校验问题时抛 <see cref="ProcessValidationException"/>；
    /// 只有提示级问题时照常部署（提示随 <see cref="DeployWithValidationAsync"/> 返回）。
    /// </para>
    /// </summary>
    ValueTask<ProcessDefinition> DeployAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// 部署流程定义并带回提示级校验结果（设计器部署契约的完整形态）。
    /// <para>阻断级问题同样抛 <see cref="ProcessValidationException"/>。</para>
    /// </summary>
    ValueTask<DeployResult> DeployWithValidationAsync(
        Stream bpmnXml,
        string resourceName,
        string? deploymentName = null,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// 解析并校验 BPMN XML，<b>不落库</b>（设计器「保存前试校验」）。
    /// <para>
    /// 返回全部校验结果（含提示级）；XML 解析失败抛 <c>BpmnXmlParseException</c>。
    /// 与部署共用同一个 <see cref="NetBpm.Bpmn.Validation.IProcessValidator"/>，不存在两套口径。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<ValidationError>> ValidateAsync(Stream bpmnXml, CancellationToken ct = default);

    /// <summary>启动流程实例，返回流程实例 id（= 根执行 id）。</summary>
    ValueTask<string> StartProcessInstanceAsync(
        string processDefinitionId,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default);

    /// <summary>
    /// 按流程 key 启动<b>最新版本</b>的流程定义（对应 <c>runtimeService.startProcessInstanceByKey</c>）。
    /// <para>
    /// <paramref name="tenantId"/> 为 <see langword="null"/> 表示无租户的定义（不是「任意租户」），
    /// 与 <see cref="StartProcessInstanceAsync"/> 的租户继承口径一致。
    /// </para>
    /// </summary>
    /// <exception cref="NetBpm.Engine.EngineException">key（+租户）下没有任何流程定义，或最新版本已挂起。</exception>
    ValueTask<string> StartProcessInstanceByKeyAsync(
        string processDefinitionKey,
        string? businessKey = null,
        string? startUserId = null,
        IDictionary<string, object?>? variables = null,
        string? tenantId = null,
        CancellationToken ct = default);

    /// <summary>
    /// 查运行中的流程实例（在办监控 / 按业务单号反查）。
    /// <para>
    /// 数据源是 <c>ACT_RU_EXECUTION</c> 的根执行；实例结束后运行时行被删除，
    /// 已归档的数据请用 <see cref="FindHistoricProcessInstancesAsync"/>。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<IExecutionEntity>> FindProcessInstancesAsync(
        ProcessInstanceQuery query,
        CancellationToken ct = default);

    /// <summary>数运行中的流程实例（分页配套查询，条件与 <see cref="FindProcessInstancesAsync"/> 同口径）。</summary>
    ValueTask<int> CountProcessInstancesAsync(
        ProcessInstanceQuery query,
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

    /// <summary>
    /// 按条件分页查流程定义（定义列表 / 按 key 查全部版本）。
    /// <para>过滤与分页在门面内完成；排序沿用仓储口径：部署时间倒序。</para>
    /// </summary>
    ValueTask<IReadOnlyList<DeployedProcessDefinition>> FindProcessDefinitionsAsync(
        ProcessDefinitionQuery query,
        CancellationToken ct = default);

    /// <summary>数流程定义（<see cref="FindProcessDefinitionsAsync(ProcessDefinitionQuery, CancellationToken)"/> 的配套查询）。</summary>
    ValueTask<int> CountProcessDefinitionsAsync(
        ProcessDefinitionQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// 挂起流程定义（对应 <c>repositoryService.suspendProcessDefinitionById</c>）：
    /// 挂起后<b>不能再启动新实例</b>。
    /// </summary>
    /// <param name="processDefinitionId">流程定义 id。</param>
    /// <param name="suspendProcessInstances">
    /// 是否<b>一并挂起</b>该定义下已经运行中的实例（对应 Activiti 的同名参数）：
    /// <see langword="true"/> 时实例的待办也不能再完成 / 认领；<see langword="false"/> 时运行中的实例不受影响。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>重复挂起是幂等的（与 Activiti 一致）。</remarks>
    /// <exception cref="NetBpm.Engine.EngineException">流程定义不存在。</exception>
    ValueTask SuspendProcessDefinitionAsync(
        string processDefinitionId,
        bool suspendProcessInstances = false,
        CancellationToken ct = default);

    /// <summary>
    /// 激活流程定义（对应 <c>repositoryService.activateProcessDefinitionById</c>）：
    /// 激活后恢复「可启动新实例」。
    /// </summary>
    /// <param name="processDefinitionId">流程定义 id。</param>
    /// <param name="activateProcessInstances">
    /// 是否一并激活该定义下被挂起的运行实例（与 <c>SuspendProcessDefinitionAsync</c> 对称）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>重复激活是幂等的。Activiti 的「按日期平移作业」参数在本子集不适用（无定时器）。</remarks>
    /// <exception cref="NetBpm.Engine.EngineException">流程定义不存在。</exception>
    ValueTask ActivateProcessDefinitionAsync(
        string processDefinitionId,
        bool activateProcessInstances = false,
        CancellationToken ct = default);

    /// <summary>
    /// 挂起单个流程实例（对应 <c>runtimeService.suspendProcessInstanceById</c>）：
    /// 执行树与任务一起挂起，待办仍可查询，但<b>完成 / 认领会被拒绝</b>。
    /// </summary>
    /// <exception cref="NetBpm.Engine.EngineException">实例不存在 / 已结束，或传的不是流程实例 id。</exception>
    ValueTask SuspendProcessInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>激活单个流程实例（与 <see cref="SuspendProcessInstanceAsync"/> 对称）。</summary>
    /// <exception cref="NetBpm.Engine.EngineException">实例不存在 / 已结束，或传的不是流程实例 id。</exception>
    ValueTask ActivateProcessInstanceAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>
    /// 删除部署（对应 <c>repositoryService.deleteDeployment(deploymentId, cascade)</c>）：
    /// 删定义、资源与部署行。
    /// </summary>
    /// <param name="deploymentId">部署 id。</param>
    /// <param name="cascade">
    /// <see langword="true"/> 时连同该部署下运行中的流程实例一起删除（历史保留）；
    /// <see langword="false"/> 时若仍有运行实例则<b>拒绝删除</b>（Activiti 同语义）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="NetBpm.Engine.EngineException">部署不存在，或非级联删除时仍有运行中的实例。</exception>
    ValueTask DeleteDeploymentAsync(
        string deploymentId,
        bool cascade = false,
        CancellationToken ct = default);

    /// <summary>取流程模型（校验 / 查询用）。</summary>
    ValueTask<BpmnModel?> GetBpmnModelAsync(string processDefinitionId, CancellationToken ct = default);

    /// <summary>
    /// 查待办（P7 / P9）。分页由 <see cref="TaskQuery.FirstResult"/> / <see cref="TaskQuery.MaxResults"/> 控制，
    /// 结果按 (创建时间, id) 升序 —— 排序稳定是跨页不重不漏的前提。
    /// </summary>
    ValueTask<IReadOnlyList<TaskEntity>> FindTasksAsync(TaskQuery query, CancellationToken ct = default);

    /// <summary>
    /// 数待办（分页的配套查询：条件与 <see cref="FindTasksAsync"/> 完全同口径，只是不取行）。
    /// <para>对应 Activiti 的 <c>TaskQuery.count()</c>。</para>
    /// </summary>
    ValueTask<int> CountTasksAsync(TaskQuery query, CancellationToken ct = default);

    /// <summary>
    /// 按 id 查单个待办（任务详情）。任务不存在 / 已完成返回 <see langword="null"/>。
    /// <para>详情比列表多出 <c>Description</c> / <c>Category</c> / <c>BusinessKey</c> / 委派状态等字段。</para>
    /// </summary>
    ValueTask<TaskEntity?> GetTaskAsync(string taskId, CancellationToken ct = default);

    /// <summary>查任务的身份关系（候选人 / 候选组 / 办理人 / 所有者）。</summary>
    ValueTask<IReadOnlyList<IdentityLinkRecord>> FindTaskIdentityLinksAsync(
        string taskId,
        CancellationToken ct = default);

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
    /// 写流程实例作用域的变量（运行期补偿 / 纠错）。
    /// <para>
    /// 语义与 Activiti <c>runtimeService.setVariable</c> 一致：本作用域已有则改本地，
    /// 否则上溯父作用域，到顶仍无则建在流程实例上。历史行由变量管理器同步更新。
    /// </para>
    /// </summary>
    ValueTask SetVariableAsync(
        string processInstanceId,
        string name,
        object? value,
        CancellationToken ct = default);

    /// <summary>批量写流程实例变量（同一个命令 / 同一个事务内完成）。</summary>
    ValueTask SetVariablesAsync(
        string processInstanceId,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken ct = default);

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
    /// 分页查历史任务（已办列表数据源）。
    /// <para>
    /// 条件比待办更窄：<b>必须给流程实例 id</b>（历史管理器只提供「按实例」的查询，
    /// 不给历史任务表做任意组合 SQL）；可再用 <see cref="HistoricTaskQuery.Assignee"/> /
    /// <see cref="HistoricTaskQuery.OnlyFinished"/> 收窄。
    /// </para>
    /// <para>
    /// 排序口径与待办一致：按 (开始时间, id) 升序再切片，保证跨页不重不漏。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<HistoricTaskInstance>> FindHistoricTasksAsync(
        HistoricTaskQuery query,
        CancellationToken ct = default);

    /// <summary>数历史任务（<see cref="FindHistoricTasksAsync(HistoricTaskQuery, CancellationToken)"/> 的配套查询）。</summary>
    ValueTask<int> CountHistoricTasksAsync(HistoricTaskQuery query, CancellationToken ct = default);

    /// <summary>
    /// 分页查历史流程实例（已办结检索 / 审计 / 报表）。
    /// <para>实例结束后运行时行已删除，这是唯一能按业务主键 / 发起人 / 时间范围反查的入口。</para>
    /// </summary>
    ValueTask<IReadOnlyList<HistoricProcessInstance>> FindHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default);

    /// <summary>数历史流程实例（分页配套查询）。</summary>
    ValueTask<int> CountHistoricProcessInstancesAsync(
        HistoricProcessInstanceQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// 查某流程实例的历史变量（<c>ACT_HI_VARINST</c>，含已删除的运行态变量）。
    /// <para>历史未启用时返回空集合。</para>
    /// </summary>
    ValueTask<IReadOnlyList<HistoricVariableInstance>> FindHistoricVariablesAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 取流程图数据（P9「流程图数据」）：BPMN XML + 活动高亮序列。
    /// <para>实例既不存在于执行树也无历史记录时返回 <see langword="null"/>（端点转 404）。</para>
    /// </summary>
    ValueTask<DiagramData?> GetDiagramAsync(string processInstanceId, CancellationToken ct = default);

    /// <summary>
    /// 删除（撤销）运行中的流程实例（对应 <c>runtimeService.deleteProcessInstance</c>）：
    /// 待办、变量与执行树一并清理，历史保留并把 <c>DELETE_REASON_</c> 写成
    /// <paramref name="deleteReason"/>。
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="deleteReason">删除原因；为空时写「删除流程实例」。</param>
    /// <exception cref="NetBpm.Engine.EngineException">实例不存在 / 已结束，或传的不是流程实例 id。</exception>
    ValueTask DeleteProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason = null,
        CancellationToken ct = default);

    /// <summary>完成任务，并继续推进流程（P7）。</summary>
    ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables = null,
        CancellationToken ct = default);

    /// <summary>
    /// 完成任务并做乐观并发校验：<paramref name="expectedRevision"/> 与任务当前 <c>REV_</c> 不一致时
    /// 抛 <c>OptimisticLockingException</c>（前端应先 GET 任务详情拿 <c>revision</c>）。
    /// </summary>
    ValueTask CompleteTaskAsync(
        string taskId,
        IDictionary<string, object?>? variables,
        int? expectedRevision,
        CancellationToken ct = default);

    /// <summary>
    /// 认领任务（P7）。<paramref name="userId"/> 必填（认领人）；取消认领请用
    /// <see cref="UnclaimTaskAsync"/>，不再用 <see langword="null"/> 表达（与 REST 同口径）。
    /// </summary>
    ValueTask ClaimTaskAsync(string taskId, string userId, CancellationToken ct = default);

    /// <summary>认领任务并做乐观并发校验（<c>REV_</c> 不一致抛 <c>OptimisticLockingException</c>）。</summary>
    ValueTask ClaimTaskAsync(
        string taskId,
        string userId,
        int? expectedRevision,
        CancellationToken ct = default);

    /// <summary>取消认领任务（清空办理人与认领时间）；已未认领时幂等。</summary>
    ValueTask UnclaimTaskAsync(string taskId, int? expectedRevision = null, CancellationToken ct = default);

    /// <summary>
    /// 改派任务（转办）：把待办的办理人直接改为指定用户（管理动作，不校验是否已被认领）。
    /// <paramref name="assignee"/> 为 <see langword="null"/> 表示退回未分配。
    /// </summary>
    ValueTask SetAssigneeAsync(string taskId, string? assignee, CancellationToken ct = default);

    /// <summary>
    /// 修改任务可变字段（优先级 / 到期时间 / 描述 / 分类 / 名称 / 所有者）。
    /// <para>每个字段独立可空，<see langword="null"/> 表示不修改；<c>Owner</c> 空串表示清空。</para>
    /// </summary>
    ValueTask UpdateTaskAsync(string taskId, TaskUpdate update, CancellationToken ct = default);

    /// <summary>增加候选用户（管理动作，不改办理人）。</summary>
    ValueTask AddTaskCandidateUsersAsync(
        string taskId,
        IReadOnlyCollection<string> userIds,
        CancellationToken ct = default);

    /// <summary>增加候选组。</summary>
    ValueTask AddTaskCandidateGroupsAsync(
        string taskId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken ct = default);

    /// <summary>移除候选用户（仅删任务级候选关系，流程实例参与者记录保留）。</summary>
    ValueTask RemoveTaskCandidateUserAsync(string taskId, string userId, CancellationToken ct = default);

    /// <summary>移除候选组。</summary>
    ValueTask RemoveTaskCandidateGroupAsync(string taskId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// 委派任务（对应 <c>taskService.delegateTask</c>）：原办理人记入 <c>OWNER_</c>，
    /// 委派状态置 <c>PENDING</c>，之后必须 <see cref="ResolveTaskAsync"/> 才能完成。
    /// </summary>
    ValueTask DelegateTaskAsync(string taskId, string userId, CancellationToken ct = default);

    /// <summary>
    /// 解决委派（对应 <c>taskService.resolveTask</c>）：办理人回到 <c>OWNER_</c>，
    /// 委派状态置 <c>RESOLVED</c>；任务不会因此完成。
    /// </summary>
    ValueTask ResolveTaskAsync(string taskId, CancellationToken ct = default);

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
