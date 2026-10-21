using NetBpm.Bpmn.Model;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 执行树管理器。对应 Activiti 的 <c>ExecutionEntityManager</c>。
/// 实现落在 <c>NetBpm.Persistence</c>（Dapper + 一级缓存），接口留在引擎侧以避免循环依赖。
/// </summary>
public interface IExecutionManager
{
    /// <summary>
    /// 创建流程实例根执行（<c>IsScope = true</c>，<c>ParentId = null</c>）。
    /// <paramref name="tenantId"/> 为部署时指定的租户（B2.3，缺省无租户）。
    /// </summary>
    IExecutionEntity CreateProcessInstanceExecution(
        Process process,
        string processDefinitionId,
        string? businessKey,
        string? startUserId,
        string? tenantId = null);

    /// <summary>
    /// 创建子执行。与 Activiti 行级一致：<b>不</b>置 <c>IS_CONCURRENT_</c>
    /// （该标志在 Activiti 9 的分支创建路径上同样保持 0，见 <c>InMemoryExecutionManager</c> 类注释）。
    /// </summary>
    IExecutionEntity CreateChildExecution(IExecutionEntity parent);

    ValueTask InsertAsync(IExecutionEntity execution, CancellationToken ct = default);

    ValueTask UpdateAsync(IExecutionEntity execution, CancellationToken ct = default);

    ValueTask<IExecutionEntity?> FindByIdAsync(string executionId, CancellationToken ct = default);

    /// <summary>
    /// 查某流程实例下的<b>全部</b>执行（含根执行与并行/会签子执行，按 id 升序）。
    /// <para>
    /// 实例迁移必须看到整棵树：只查活动执行会漏掉停在并行网关 Join 上的非活动执行。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<IExecutionEntity>> FindAllByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 查「当前活动为指定节点、且处于非活动状态」的执行。
    /// 并行网关 Join 的判定基础（见 §5.4 之二）。
    /// </summary>
    ValueTask<IList<IExecutionEntity>> FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(
        string activityId,
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 统计流程实例下处于活动状态、且不是流程实例根执行的执行数。
    /// 对应 Activiti 的 <c>getNumberOfActiveChildExecutionsForProcessInstance</c> ——
    /// 值为 0 是「流程实例可以结束」的唯一判据（§5.5）。
    /// </summary>
    ValueTask<int> CountActiveChildExecutionsForProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 查流程实例下「处于活动状态」的子执行（不含流程实例执行自身）。
    /// 包含网关 Join 的判定基础：逐个判断「该执行是否还能到达网关」（对应
    /// Activiti 的 <c>findChildExecutionsByProcessInstanceId</c> + <c>isActive</c> 过滤）。
    /// </summary>
    ValueTask<IList<IExecutionEntity>> FindActiveChildExecutionsByProcessInstanceIdAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 统计某执行的子执行数。对应 Activiti 的 <c>getNumberOfActiveChildExecutionsForExecution</c>。
    /// <para>⚠️ 注意：Activiti 的实现是「按父执行 id 查全部子执行并计数」，并<b>不</b>过滤 <c>IS_ACTIVE_</c>，
    /// 本方法保持该行为以确保两端一致。</para>
    /// </summary>
    ValueTask<int> CountChildExecutionsForExecutionAsync(
        string parentExecutionId,
        CancellationToken ct = default);

    /// <summary>删除执行及其关联数据（任务 / 变量 / 候选关系）。对应 <c>deleteExecutionAndRelatedData</c>。</summary>
    ValueTask DeleteExecutionAndRelatedDataAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default);

    /// <summary>删除执行的全部子执行。对应 <c>deleteChildExecutions</c>。</summary>
    ValueTask DeleteChildExecutionsAsync(
        IExecutionEntity parentExecution,
        string? deleteReason,
        CancellationToken ct = default);

    /// <summary>
    /// 删除整个流程实例的执行树。对应 <c>deleteProcessInstanceExecutionEntity</c>。
    /// <para>
    /// ⚠️ 第 2 个参数是 <c>currentFlowElementId</c>（落到 <c>ACT_HI_PROCINST.END_ACT_ID_</c>），
    /// 不是删除原因 —— Activiti 的签名为
    /// <c>(processInstanceId, currentFlowElementId, deleteReason, cascade, cancel)</c>，
    /// 且 <c>EndExecutionOperation</c> 传的是<b>操作自身执行</b>的当前元素（注释原文：
    /// "note the use of execution here vs processinstance execution for getting the flowelement"），
    /// 正常结束路径的 <paramref name="deleteReason"/> 为 <see langword="null"/>。
    /// </para>
    /// </summary>
    ValueTask DeleteProcessInstanceExecutionEntityAsync(
        string processInstanceId,
        string? currentFlowElementId = null,
        string? deleteReason = null,
        bool cancel = false,
        CancellationToken ct = default);

    // ─────────────────────────── 挂起 / 激活 ───────────────────────────

    /// <summary>
    /// 挂起 / 激活单个流程实例的执行树（对应 Activiti 的
    /// <c>SuspendProcessInstanceCmd</c> → <c>updateProcessInstanceSuspensionStateByProcessInstanceId</c>）。
    /// <para>
    /// 只写 <c>ACT_RU_EXECUTION.SUSPENSION_STATE_</c>；任务行由
    /// <see cref="Tasks.ITaskManager.UpdateSuspensionStateByProcessInstanceIdAsync"/> 一起更新 ——
    /// 只挂执行不挂任务会出现「实例已挂起、待办却能完成」的漏洞
    /// （<c>CompleteTaskCommand</c> 的挂起检查读的正是任务行）。
    /// </para>
    /// <para>
    /// ⚠️ 与 Activiti 一致，<c>IS_ACTIVE_</c> <b>不</b>改动 —— 挂起是「暂停受理」，
    /// 不是「节点失效」，流程图着色与并行 Join 判定都不受影响。
    /// </para>
    /// </summary>
    /// <returns>受影响的流程实例数（0 = 实例不存在）。</returns>
    ValueTask<int> UpdateSuspensionStateByProcessInstanceIdAsync(
        string processInstanceId,
        bool suspended,
        CancellationToken ct = default);

    /// <summary>
    /// 挂起 / 激活某流程定义下的<b>全部运行实例</b>（只改执行行，任务行同理由任务管理器更新）。
    /// 对应 Activiti <c>suspendProcessDefinitionById(id, suspendProcessInstances=true)</c> 里
    /// 「按流程定义批量更新」的那一步。
    /// </summary>
    /// <returns>受影响的流程实例数（按根执行计数）。</returns>
    ValueTask<int> UpdateSuspensionStateByProcessDefinitionIdAsync(
        string processDefinitionId,
        bool suspended,
        CancellationToken ct = default);

    /// <summary>
    /// 查某流程定义下<b>运行中</b>的流程实例 id（根执行，按活跃顺序无保证）。
    /// <para>
    /// 部署级联删除用它先收口运行态；<c>PARENT_ID_ is null</c> 已排除子执行。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<string>> FindProcessInstanceIdsByProcessDefinitionIdAsync(
        string processDefinitionId,
        CancellationToken ct = default);

    /// <summary>
    /// 查<b>全部运行中的流程实例</b>（根执行，<c>PARENT_ID_ is null</c>）。
    /// <para>
    /// 条件过滤与分页由门面在内存完成（与待办查询同一口径）。返回的执行只带根行字段，
    /// <c>Children</c> 不为实例列表预加载 —— 需要执行树请按 id 查 <see cref="FindByIdAsync"/>。
    /// </para>
    /// </summary>
    ValueTask<IReadOnlyList<IExecutionEntity>> FindProcessInstancesAsync(
        CancellationToken ct = default);
}
