using NetBpm.Engine.Execution;

namespace NetBpm.Engine.Tasks;

/// <summary>
/// 任务委派状态。对应 Activiti 的 <c>DelegationState</c>（<c>ACT_RU_TASK.DELEGATION_</c>）。
/// <para>本子集不实现委派（delegate），仅保留字段以保证列语义完整。</para>
/// </summary>
public enum DelegationState
{
    /// <summary><c>DELEGATION_ = 'PENDING'</c>。</summary>
    Pending,

    /// <summary><c>DELEGATION_ = 'RESOLVED'</c>。</summary>
    Resolved,
}

/// <summary>
/// 任务实体。对应 Activiti 的 <c>TaskEntity</c> / <c>ACT_RU_TASK</c> 一行。
/// <para>
/// 🔑 列与属性一一对应；<c>REV_</c> 同样参与乐观锁（沿用执行树的做法）。
/// </para>
/// </summary>
public sealed class TaskEntity
{
    /// <summary>
    /// 新建任务的默认优先级（<c>PRIORITY_</c>）。
    /// <para>
    /// ⚠️ <b>已核实</b>：Activiti 9 的 <c>Task.DEFAULT_PRIORITY</c> 是 <b>0</b>
    /// （<c>TaskEntityImpl.priority = DEFAULT_PRIORITY</c> 解析到这个接口常量）。
    /// 老版本 Activiti 5 用的是 50 —— 这里以源码为准，取 0，否则对拍会在该列上全线偏移。
    /// </para>
    /// </summary>
    public const int DefaultPriority = 0;

    /// <summary>激活状态（<c>SUSPENSION_STATE_</c>）。</summary>
    public const int ActiveSuspensionState = 1;

    /// <summary><c>ID_</c>。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary><c>REV_</c>（0 表示尚未 INSERT，与执行树同一约定）。</summary>
    public int Revision { get; set; }

    /// <summary><c>NAME_</c>。</summary>
    public string? Name { get; set; }

    /// <summary><c>DESCRIPTION_</c>。</summary>
    public string? Description { get; set; }

    /// <summary><c>BUSINESS_KEY_</c>（取自流程实例）。</summary>
    public string? BusinessKey { get; set; }

    /// <summary><c>PARENT_TASK_ID_</c>（子任务用，本子集不产生）。</summary>
    public string? ParentTaskId { get; set; }

    /// <summary><c>PRIORITY_</c>，默认 <see cref="DefaultPriority"/>（0）。</summary>
    public int Priority { get; set; } = DefaultPriority;

    /// <summary><c>CREATE_TIME_</c>。</summary>
    public DateTime? CreateTime { get; set; }

    /// <summary><c>OWNER_</c>。</summary>
    public string? Owner { get; set; }

    /// <summary><c>ASSIGNEE_</c>。</summary>
    public string? Assignee { get; set; }

    /// <summary><c>DELEGATION_</c>。</summary>
    public DelegationState? DelegationState { get; set; }

    /// <summary><c>EXECUTION_ID_</c>。</summary>
    public string? ExecutionId { get; set; }

    /// <summary><c>PROC_INST_ID_</c>。</summary>
    public string? ProcessInstanceId { get; set; }

    /// <summary><c>PROC_DEF_ID_</c>。</summary>
    public string? ProcessDefinitionId { get; set; }

    /// <summary><c>TASK_DEF_KEY_</c>（BPMN 中 <c>userTask</c> 的 id）。</summary>
    public string? TaskDefinitionKey { get; set; }

    /// <summary><c>DUE_DATE_</c>。</summary>
    public DateTime? DueDate { get; set; }

    /// <summary><c>CATEGORY_</c>。</summary>
    public string? Category { get; set; }

    /// <summary><c>SUSPENSION_STATE_</c>。</summary>
    public int SuspensionState { get; set; } = ActiveSuspensionState;

    /// <summary><c>TENANT_ID_</c>。</summary>
    public string? TenantId { get; set; }

    /// <summary><c>FORM_KEY_</c>。</summary>
    public string? FormKey { get; set; }

    /// <summary><c>CLAIM_TIME_</c>。</summary>
    public DateTime? ClaimTime { get; set; }

    /// <summary>
    /// 关联的执行。<b>不参与持久化</b>，仅用于「完成任务后继续流程」。
    /// </summary>
    public IExecutionEntity? Execution { get; set; }

    /// <summary>
    /// 任务局部变量（B2.4）。<b>仅内存宿主使用</b>（<c>InMemoryTaskManager</c> 的载体）；
    /// 数据库宿主的真身在 <c>ACT_RU_VARIABLE.TASK_ID_</c> 行，本字典不参与持久化。
    /// </summary>
    public IDictionary<string, object?> LocalVariables { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>写入 <c>DELEGATION_</c> 的字符串形式。</summary>
    public string? DelegationStateText => DelegationState switch
    {
        NetBpm.Engine.Tasks.DelegationState.Pending => "PENDING",
        NetBpm.Engine.Tasks.DelegationState.Resolved => "RESOLVED",
        _ => null,
    };

    public override string ToString() =>
        $"{Id} (key={TaskDefinitionKey}, assignee={Assignee}, name={Name})";
}

/// <summary>
/// 身份关系记录。对应 <c>ACT_RU_IDENTITYLINK</c> 一行。
/// <para>
/// 🔑 <b>已核实的 Activiti 语义</b>（决定行内容，必须照搬）：
/// <list type="bullet">
///   <item><description>候选用户 → <b>任务级</b> <c>TYPE_ = 'candidate'</c> 行
///     <b>外加</b>流程实例级 <c>TYPE_ = 'participant'</c> 行；</description></item>
///   <item><description>候选组 → 仅任务级 <c>TYPE_ = 'candidate'</c> 行；</description></item>
///   <item><description>办理人 / 所有者 → <b>只有</b>流程实例级 <c>TYPE_ = 'participant'</c> 行
///     （<c>addAssigneeIdentityLinks</c> 调的是 <c>involveUser(processInstance, …)</c>），
///     创建时<b>不会</b>产生任务级 <c>TYPE_ = 'assignee'</c> 行；</description></item>
///   <item><description><c>'participant'</c> 行按用户去重（<c>involveUser</c> 先查后插）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record IdentityLinkRecord
{
    /// <summary><c>ID_</c>。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary><c>TYPE_</c>（见 <see cref="NetBpm.Identity.IdentityLinkType"/>）。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary><c>USER_ID_</c>。</summary>
    public string? UserId { get; init; }

    /// <summary><c>GROUP_ID_</c>。</summary>
    public string? GroupId { get; init; }

    /// <summary><c>TASK_ID_</c>。</summary>
    public string? TaskId { get; init; }

    /// <summary><c>PROC_INST_ID_</c>。</summary>
    public string? ProcessInstanceId { get; init; }

    /// <summary><c>PROC_DEF_ID_</c>。</summary>
    public string? ProcessDefinitionId { get; init; }
}
