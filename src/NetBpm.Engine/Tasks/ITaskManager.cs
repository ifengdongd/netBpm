using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Tasks;

/// <summary>
/// 任务管理器（P7）。对应 Activiti 的 <c>TaskEntityManager</c> + <c>IdentityLinkEntityManager</c>。
/// <para>接口留在引擎侧，实现落在 <c>NetBpm.Persistence</c>（Dapper）与 <c>InMemoryTaskManager</c>。</para>
/// </summary>
public interface ITaskManager
{
    /// <summary>创建一个尚未落库的任务（<c>Revision == 0</c>）。</summary>
    TaskEntity CreateTask();

    /// <summary>插入任务并把执行上的上下文补齐（对应 <c>insert(task, execution)</c>）。</summary>
    ValueTask InsertTaskAsync(TaskEntity task, IExecutionEntity execution, CancellationToken ct = default);

    /// <summary>按 id 查任务。</summary>
    ValueTask<TaskEntity?> FindTaskByIdAsync(string taskId, CancellationToken ct = default);

    /// <summary>查某执行上的全部任务（用户任务触发前的校验依赖它）。</summary>
    ValueTask<IList<TaskEntity>> FindTasksByExecutionIdAsync(string executionId, CancellationToken ct = default);

    /// <summary>查某人的待办（对应 <c>TaskQuery.taskAssignee</c>）。</summary>
    ValueTask<IList<TaskEntity>> FindTasksByAssigneeAsync(string assignee, CancellationToken ct = default);

    /// <summary>
    /// 查全部待办（<b>全表</b>）。仅供管理视图的全量待办统计使用
    /// （<c>TaskQuery.AllUsers</c> 显式放行后才可达）；引擎的防呆校验默认仍拒绝无条件查询。
    /// </summary>
    ValueTask<IList<TaskEntity>> FindAllOpenTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// 查某流程实例的全部待办（对应 <c>TaskQuery.processInstanceId</c>）。
    /// <para>
    /// 用途有两个：对拍脚本的「完成任一待办」动作（两条引擎必须看到同一批待办），
    /// 以及实例视图回答"现在停在哪几个待办上"。
    /// </para>
    /// </summary>
    ValueTask<IList<TaskEntity>> FindTasksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 查候选人待办（对应 <c>TaskQuery.taskCandidateUser</c>）：
    /// 未分配 + （候选用户本人 或 候选组在用户所属组内）。
    /// </summary>
    ValueTask<IList<TaskEntity>> FindTasksByCandidateUserAsync(
        string userId,
        IReadOnlyCollection<string> groupIds,
        CancellationToken ct = default);

    /// <summary>
    /// 查候选组待办（对应 <c>TaskQuery.taskCandidateGroup</c>）：
    /// 未分配 + 任务级身份关系里存在该组（<c>TYPE_ = 'candidate'</c>）。
    /// <para>不做用户组展开 —— 调用方已经拿到了组 id。</para>
    /// </summary>
    ValueTask<IList<TaskEntity>> FindTasksByCandidateGroupAsync(
        string groupId,
        CancellationToken ct = default);

    /// <summary>
    /// 更新任务行（乐观锁）。
    /// <para>
    /// ⚠️ Activiti 靠 <c>DbSqlSession</c> 的脏检查在 flush 时统一写库；本实现不做任务级脏检查，
    /// 因此「只改了 <c>CLAIM_TIME_</c> 之类字段」的场景必须显式调用本方法。
    /// </para>
    /// </summary>
    ValueTask UpdateTaskAsync(TaskEntity task, CancellationToken ct = default);

    /// <summary>
    /// 变更办理人。对应 Activiti 的 <c>changeTaskAssignee</c> / <c>changeTaskAssigneeNoEvents</c>
    /// （本子集无事件分发与监听器，两者合一）。
    /// <para>内部会按需为流程实例补 <c>TYPE_ = 'participant'</c> 的身份关系。</para>
    /// </summary>
    ValueTask ChangeTaskAssigneeAsync(TaskEntity task, string? assignee, CancellationToken ct = default);

    /// <summary>变更所有者。对应 <c>changeTaskOwner</c>。</summary>
    ValueTask ChangeTaskOwnerAsync(TaskEntity task, string? owner, CancellationToken ct = default);

    /// <summary>加候选用户（任务级 <c>'candidate'</c> + 流程实例级 <c>'participant'</c>）。</summary>
    ValueTask AddCandidateUsersAsync(TaskEntity task, IEnumerable<string> candidateUsers, CancellationToken ct = default);

    /// <summary>加候选组（仅任务级 <c>'candidate'</c>）。</summary>
    ValueTask AddCandidateGroupsAsync(TaskEntity task, IEnumerable<string> candidateGroups, CancellationToken ct = default);

    /// <summary>
    /// 移除候选用户（仅删任务级 <c>'candidate'</c> 行）。
    /// <para>流程实例级 <c>'participant'</c> 行保留：参与者是审计信息，不随候选关系撤销。</para>
    /// </summary>
    ValueTask RemoveCandidateUsersAsync(
        TaskEntity task,
        IEnumerable<string> candidateUsers,
        CancellationToken ct = default);

    /// <summary>移除候选组（仅删任务级 <c>'candidate'</c> 行）。</summary>
    ValueTask RemoveCandidateGroupsAsync(
        TaskEntity task,
        IEnumerable<string> candidateGroups,
        CancellationToken ct = default);

    /// <summary>
    /// 插入一条身份关系。对应 <c>addIdentityLink</c> 的两个重载
    /// （传 <paramref name="task"/> 为任务级，<paramref name="task"/> 为 <see langword="null"/> 且给
    /// <paramref name="processInstanceId"/> 为流程实例级）。
    /// </summary>
    ValueTask AddIdentityLinkAsync(IdentityLinkRecord link, CancellationToken ct = default);

    /// <summary>
    /// 删除任务及其关联数据（身份关系、任务局部变量、历史）。对应 <c>deleteTask</c>。
    /// </summary>
    ValueTask DeleteTaskAsync(TaskEntity task, string? deleteReason, CancellationToken ct = default);

    /// <summary>
    /// 删除某流程实例下的<b>全部任务</b>，并清掉流程实例级的身份关系行。
    /// 对应 <c>TaskEntityManagerImpl.deleteTasksByProcessInstanceId</c> 与
    /// <c>ExecutionEntityManagerImpl</c> 中对 <c>PROC_INST_ID_</c> 身份关系的清理。
    /// <para>流程实例结束时由执行树管理器调用（否则两个宿主会留下不同的残留数据）。</para>
    /// </summary>
    /// <param name="processInstanceId">流程实例 id。</param>
    /// <param name="deleteReason">
    /// 删除原因，写进每个任务的历史行（<c>ACT_HI_TASKINST.DELETE_REASON_</c>）。
    /// 正常结束传 <see langword="null"/>；删除流程实例时传同一原因（Activiti 的
    /// <c>deleteTasksByProcessInstanceId(id, deleteReason)</c> 就是这么传的）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    ValueTask DeleteTasksByProcessInstanceAsync(
        string processInstanceId,
        string? deleteReason = null,
        CancellationToken ct = default);

    // ─────────────────────────── 挂起 / 激活 ───────────────────────────

    /// <summary>
    /// 把某流程实例下所有任务置为挂起 / 激活（<c>ACT_RU_TASK.SUSPENSION_STATE_</c>）。
    /// <para>
    /// 对应 Activiti <c>TaskEntityManager.updateTaskSuspensionState</c>：挂起的任务
    /// <b>不能完成、不能认领</b>（<c>CompleteTaskCommand</c> / <c>ClaimTaskCommand</c> 会抛
    /// "Cannot complete/claim a suspended task"），但仍在待办查询里可见。
    /// </para>
    /// </summary>
    /// <returns>受影响的任务行数。</returns>
    ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default);

    /// <summary>
    /// 把某流程定义下所有任务置为挂起 / 激活。
    /// 对应 <c>suspendProcessDefinitionById(id, suspendProcessInstances=true)</c> 中批量挂起实例的配套动作。
    /// </summary>
    /// <returns>受影响的任务行数。</returns>
    ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default);

    /// <summary>查任务的身份关系（测试与诊断用）。</summary>
    ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByTaskIdAsync(string taskId, CancellationToken ct = default);

    /// <summary>查流程实例的身份关系（测试与诊断用）。</summary>
    ValueTask<IList<IdentityLinkRecord>> FindIdentityLinksByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default);

    // ─────────────────────────── 任务局部变量（B2.4）───────────────────────────

    /// <summary>
    /// 写任务局部变量（<c>ACT_RU_VARIABLE.TASK_ID_</c> 行）。
    /// <para>
    /// 语义与 Activiti 的 <c>task.setVariableLocal</c> 一致：局部变量随任务存亡 ——
    /// 任务完成 / 删除时随之清理（不写回流程实例作用域）。
    /// </para>
    /// </summary>
    ValueTask SetTaskVariableAsync(TaskEntity task, string name, object? value, CancellationToken ct = default);

    /// <summary>读单个任务局部变量，未命中返回 <see langword="null"/>（不沿作用域链上溯到流程实例）。</summary>
    ValueTask<object?> GetTaskVariableAsync(string taskId, string name, CancellationToken ct = default);

    /// <summary>读任务的全部局部变量（不含流程实例作用域的变量）。</summary>
    ValueTask<IDictionary<string, object?>> GetTaskVariablesAsync(string taskId, CancellationToken ct = default);

    /// <summary>删除单个任务局部变量；未命中返回 <see langword="false"/>。</summary>
    ValueTask<bool> RemoveTaskVariableAsync(string taskId, string name, CancellationToken ct = default);
}
