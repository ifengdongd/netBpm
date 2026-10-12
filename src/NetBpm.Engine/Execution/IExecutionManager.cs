using NetBpm.Bpmn.Model;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 执行树管理器。对应 Activiti 的 <c>ExecutionEntityManager</c>。
/// 实现落在 <c>NetBpm.Persistence</c>（Dapper + 一级缓存），接口留在引擎侧以避免循环依赖。
/// </summary>
public interface IExecutionManager
{
    /// <summary>创建流程实例根执行（<c>IsScope = true</c>，<c>ParentId = null</c>）。</summary>
    IExecutionEntity CreateProcessInstanceExecution(
        Process process,
        string processDefinitionId,
        string? businessKey,
        string? startUserId);

    /// <summary>创建并发子执行（<c>IsConcurrent = true</c>）。</summary>
    IExecutionEntity CreateChildExecution(IExecutionEntity parent);

    ValueTask InsertAsync(IExecutionEntity execution, CancellationToken ct = default);

    ValueTask UpdateAsync(IExecutionEntity execution, CancellationToken ct = default);

    ValueTask<IExecutionEntity?> FindByIdAsync(string executionId, CancellationToken ct = default);

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
}
