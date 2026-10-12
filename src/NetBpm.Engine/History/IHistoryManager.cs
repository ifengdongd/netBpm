using NetBpm.Engine.Execution;
using NetBpm.Engine.Tasks;

namespace NetBpm.Engine.History;

/// <summary>
/// 历史记录管理器。对应 Activiti 的 <c>HistoryManager</c>（实现 <c>DefaultHistoryManager</c>）。
/// <para>
/// 历史级别取 <c>AUDIT</c>（Activiti 默认，§7.2）：
/// <list type="bullet">
///   <item><description><b>ACTIVITY 及以上</b>：流程实例起止、活动起止、变量增改删、
///     任务办理人回填（<c>ACT_HI_ACTINST.ASSIGNEE_</c>）、任务 id 回填（<c>TASK_ID_</c>）；</description></item>
///   <item><description><b>AUDIT 及以上</b>：任务创建/结束、任务字段变更、身份关系；</description></item>
///   <item><description><b>仅 FULL</b>：<c>ACT_HI_DETAIL</c>（本子集裁掉了这张表，故永不写）。</description></item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>已登记的实现差异</b>：Activiti 的历史实体进 <c>DbSqlSession</c> 一级缓存、在 flush 时统一落库；
/// 本实现在<b>同一事务内即时写库</b>。提交后的可见结果相同，差别只在事务内的写入时刻。
/// </para>
/// </summary>
public interface IHistoryManager
{
    /// <summary>历史是否启用（对应 <c>!historyLevel.equals(NONE)</c>）。</summary>
    bool IsHistoryEnabled { get; }

    /// <summary>记录流程实例启动（<c>ACT_HI_PROCINST</c>）。</summary>
    /// <param name="startActivityId">开始事件 id，落到 <c>START_ACT_ID_</c>。</param>
    ValueTask RecordProcessInstanceStartAsync(
        IExecutionEntity processInstance,
        string? startActivityId,
        CancellationToken ct = default);

    /// <summary>记录流程实例结束（<c>END_TIME_</c> / <c>DURATION_</c> / <c>END_ACT_ID_</c> / <c>DELETE_REASON_</c>）。</summary>
    /// <param name="currentFlowElementId">
    /// 结束时所处元素 id，落到 <c>END_ACT_ID_</c>。
    /// <para>Activiti 传的是<b>操作自身的执行</b>的当前元素（而非流程实例执行）——
    /// 见 <c>EndExecutionOperation</c> 的注释「note the use of execution here vs processinstance execution」。</para>
    /// </param>
    ValueTask RecordProcessInstanceEndAsync(
        string processInstanceId,
        string? deleteReason,
        string? currentFlowElementId,
        CancellationToken ct = default);

    /// <summary>记录活动开始（<c>ACT_HI_ACTINST</c>，id 为新生成的 id）。</summary>
    ValueTask RecordActivityStartAsync(IExecutionEntity execution, CancellationToken ct = default);

    /// <summary>
    /// 节点离开：收口历史活动行（<c>ACT_HI_ACTINST</c>）并产生 <c>ACTIVITY_COMPLETED</c> 语义。
    /// <para>
    /// ⚠️ 对应 Activiti 的 <c>TakeOutgoingSequenceFlowsOperation.handleActivityEnd</c> ——
    /// 在那里 <c>recordActivityEnd</c> 与 <c>ACTIVITY_COMPLETED</c> 事件是<b>同一处先后发生</b>的
    /// （排他网关的行为内 leave 也是"事件 + 收口"成对出现）。
    /// 因此本端口是"离开活动"的完整语义；<b>只收口历史行、不代表离开</b>的场景
    /// （并行网关每次到达、执行删除清理、沿顺序流前进）必须用
    /// <see cref="RecordActivityEndQuietlyAsync"/>。
    /// </para>
    /// </summary>
    ValueTask RecordActivityEndAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default);

    /// <summary>
    /// 只收口历史活动行，<b>不产生</b> <c>ACTIVITY_COMPLETED</c> 语义。
    /// <para>
    /// 对应 Activiti 里那些<b>只调 <c>recordActivityEnd</c>、不分发事件</b>的位置：
    /// 并行网关每次到达（源码注释："Is needed to set the endTime for all historic activity joins"）、
    /// 执行删除的级联清理、沿顺序流前进（此时收口的是源节点那一行，见 §15.4 H4）。
    /// </para>
    /// <para>
    /// 🔑 这是 2026-10-12 真值比对（§15.10 R5）抓到的口径差：此前"收口"与"离开事件"绑在一个端口上，
    /// 导致并行 Join 的<b>首次到达</b>就发出 <c>ACTIVITY_COMPLETED</c>，而 Activiti 要等最后一个令牌
    /// 放行 Join 时才有。
    /// </para>
    /// </summary>
    ValueTask RecordActivityEndQuietlyAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default);

    /// <summary>记录任务创建（<c>ACT_HI_TASKINST</c>，id 与 <c>ACT_RU_TASK.ID_</c> 相同）。</summary>
    ValueTask RecordTaskCreatedAsync(TaskEntity task, IExecutionEntity execution, CancellationToken ct = default);

    /// <summary>把任务 id 回填到 <c>ACT_HI_ACTINST.TASK_ID_</c>。</summary>
    ValueTask RecordTaskIdAsync(TaskEntity task, CancellationToken ct = default);

    /// <summary>把办理人回填到 <c>ACT_HI_ACTINST.ASSIGNEE_</c>。</summary>
    ValueTask RecordTaskAssignmentAsync(TaskEntity task, CancellationToken ct = default);

    /// <summary>
    /// 同步历史任务行的可变列（<c>ACT_HI_TASKINST</c> 的 ASSIGNEE_ / OWNER_ / CLAIM_TIME_ /
    /// DUE_DATE_ / PRIORITY_ / NAME_ / DESCRIPTION_ / CATEGORY_ / FORM_KEY_）。
    /// <para>
    /// 对应 Activiti 的 <c>recordTaskAssigneeChange</c> / <c>recordTaskOwnerChange</c> /
    /// <c>recordTaskClaim</c> / <c>recordTaskXxxChange</c> 一族方法 ——
    /// <b>本子集把它们收敛成一次同步</b>（行内容一致，少九个端口方法）。
    /// </para>
    /// </summary>
    ValueTask RecordTaskFieldsChangedAsync(TaskEntity task, CancellationToken ct = default);

    /// <summary>任务结束（收口 <c>ACT_HI_TASKINST</c>）。</summary>
    ValueTask RecordTaskEndAsync(string taskId, string? deleteReason, CancellationToken ct = default);

    /// <summary>记录历史变量行（<c>ACT_HI_VARINST</c>，id 与 <c>ACT_RU_VARIABLE.ID_</c> 相同）。</summary>
    ValueTask RecordVariableCreateAsync(VariableInstance variable, CancellationToken ct = default);

    /// <summary>更新历史变量行（同一行 UPDATE，<c>REV_</c> 自增，刷新 <c>LAST_UPDATED_TIME_</c>）。</summary>
    ValueTask RecordVariableUpdateAsync(VariableInstance variable, CancellationToken ct = default);

    /// <summary>
    /// 查某流程实例的历史活动（流程图着色数据源，§6.2 第 5 项）。
    /// <para>历史未启用时返回空集合。</para>
    /// </summary>
    ValueTask<IReadOnlyList<HistoricActivityInstance>> FindActivityInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>
    /// 查历史流程实例。<b>实例结束后仍可查到</b>（运行时的 <c>ACT_RU_EXECUTION</c> 行已删除）。
    /// </summary>
    ValueTask<HistoricProcessInstance?> FindProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>查某流程实例的历史任务（已办查询 / 审计）。</summary>
    ValueTask<IReadOnlyList<HistoricTaskInstance>> FindTaskInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default);

    /// <summary>记录历史身份关系（<c>ACT_HI_IDENTITYLINK</c>）。仅流程实例级 / 任务级会被记录。</summary>
    /// <remarks>
    /// 只增不删：Activiti 的 <c>deleteIdentityLinksByTaskId</c> 传的是
    /// <c>cascadeHistory = false</c>（见 <c>IdentityLinkEntityManagerImpl:341</c>），
    /// 因此任务完成/实例结束时<b>历史关系行会保留</b>。
    /// </remarks>
    ValueTask RecordIdentityLinkCreatedAsync(IdentityLinkRecord identityLink, CancellationToken ct = default);

    /// <summary>
    /// 执行沿顺序流前进。对应 Activiti 的 <c>SEQUENCEFLOW_TAKEN</c> 事件（维度③的数据源）。
    /// <para>
    /// 🔑 为什么需要这个端口：顺序流<b>不产生任何历史行</b>（§15.4 H4），因此它在维度②里完全不可见；
    /// 而"排他网关最终选了哪条流"恰恰是最有价值的调试信息 —— 只能靠事件序列承载。
    /// </para>
    /// <para>
    /// 调用时机：执行把 <c>CurrentFlowElement</c> 指向顺序流、并准备跳转到目标节点<b>之前</b>
    /// （对应 Activiti 在 <c>ContinueProcessOperation</c> 里分发该事件的时刻）。
    /// </para>
    /// </summary>
    ValueTask RecordSequenceFlowTakenAsync(IExecutionEntity execution, CancellationToken ct = default);
}

/// <summary>
/// 空实现：关闭历史时的策略（对应历史级别 <c>NONE</c>）。
/// <para>内存宿主也用不到真实历史 —— 历史启用要求数据库宿主（见 <c>NetBpmOptions</c> 的校验）。</para>
/// </summary>
public sealed class NullHistoryManager : IHistoryManager
{
    public bool IsHistoryEnabled => false;

    public ValueTask RecordProcessInstanceStartAsync(
        IExecutionEntity processInstance,
        string? startActivityId,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RecordProcessInstanceEndAsync(
        string processInstanceId,
        string? deleteReason,
        string? currentFlowElementId,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RecordActivityStartAsync(IExecutionEntity execution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordActivityEndAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RecordTaskCreatedAsync(
        TaskEntity task,
        IExecutionEntity execution,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RecordTaskIdAsync(TaskEntity task, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask RecordTaskAssignmentAsync(TaskEntity task, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordTaskFieldsChangedAsync(TaskEntity task, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordTaskEndAsync(string taskId, string? deleteReason, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordVariableCreateAsync(VariableInstance variable, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordVariableUpdateAsync(VariableInstance variable, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordIdentityLinkCreatedAsync(
        IdentityLinkRecord identityLink,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<HistoricActivityInstance>> FindActivityInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricActivityInstance>>([]);

    public ValueTask<HistoricProcessInstance?> FindProcessInstanceAsync(
        string processInstanceId,
        CancellationToken ct = default) => ValueTask.FromResult<HistoricProcessInstance?>(null);

    public ValueTask<IReadOnlyList<HistoricTaskInstance>> FindTaskInstancesAsync(
        string processInstanceId,
        CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HistoricTaskInstance>>([]);

    /// <summary>顺序流不产生任何历史行（§15.4 H4），因此空实现。</summary>
    public ValueTask RecordSequenceFlowTakenAsync(IExecutionEntity execution, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    /// <summary>空历史级别：无行可收口。</summary>
    public ValueTask RecordActivityEndQuietlyAsync(
        IExecutionEntity execution,
        string? deleteReason,
        CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
