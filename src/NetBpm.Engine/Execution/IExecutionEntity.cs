using NetBpm.Bpmn.Model;

namespace NetBpm.Engine.Execution;

/// <summary>
/// 执行树节点（持久化到 <c>ACT_RU_EXECUTION</c>）。
/// 对应 Activiti 的 <c>ExecutionEntity</c> —— 引擎唯一的运行态。
/// </summary>
public interface IExecutionEntity : IDelegateExecution
{
    /// <summary>乐观锁版本（<c>REV_</c>）。</summary>
    int Revision { get; set; }

    /// <summary>
    /// 当前执行的流程定义 id（<c>PROC_DEF_ID_</c>）。
    /// <para>
    /// 基接口只读；实例迁移需要把运行中的执行整棵树切到目标版本，因此在派生接口里开放写入。
    /// </para>
    /// </summary>
    new string? ProcessDefinitionId { get; set; }

    new bool IsActive { get; set; }

    new bool IsConcurrent { get; set; }

    new bool IsScope { get; set; }

    new bool IsEnded { get; set; }

    /// <summary>是否为事件作用域（子集内恒为 false）。</summary>
    bool IsEventScope { get; set; }

    /// <summary>业务主键（<c>BUSINESS_KEY_</c>）。</summary>
    string? BusinessKey { get; set; }

    /// <summary>流程实例名（<c>NAME_</c>）。本子集不设置，恒为 <see langword="null"/>。</summary>
    string? Name { get; set; }

    /// <summary>
    /// 租户（<c>TENANT_ID_</c>，B2.3）。随部署指定的租户逐层传递：
    /// 定义 → 流程实例 → 子执行 → 任务；无租户时为 <see langword="null"/>（落库写 <c>''</c>）。
    /// </summary>
    string? TenantId { get; set; }

    /// <summary>
    /// 根流程实例 id（<c>ROOT_PROC_INST_ID_</c>）。无调用活动时等于流程实例 id。
    /// </summary>
    string? RootProcessInstanceId { get; set; }

    /// <summary>父执行的执行 id（<c>SUPER_EXEC_</c>）。</summary>
    string? SuperExecutionId { get; set; }

    /// <summary>挂起状态（<c>SUSPENSION_STATE_</c>），1=激活，2=挂起。</summary>
    int SuspensionState { get; set; }

    /// <summary>开始时间（<c>START_TIME_</c>）。</summary>
    DateTime? StartTime { get; set; }

    /// <summary>发起人（<c>START_USER_ID_</c>）。</summary>
    // new：IVariableScope 现在也声明了只读的 StartUserId（隐式对象 initiator 的取值来源），
    // 执行实体需要可写（启动时由命令写入），因此在派生接口里显式扩充
    new string? StartUserId { get; set; }

    /// <summary>锁定的首个父作用域（并行 Join 判定用，见 §5.4 之二）。</summary>
    IExecutionEntity? Parent { get; set; }

    /// <summary>子执行集合。</summary>
    IList<IExecutionEntity> Children { get; }

    /// <summary>
    /// 变量持久化端口（P5 持久化钩子）。
    /// <para>为 <see langword="null"/> ⇒ 变量只活在执行对象上（内存宿主，与 P5 之前行为一致）。</para>
    /// </summary>
    IVariableInstanceManager? VariableInstanceManager { get; set; }

    /// <summary>
    /// 父执行的懒加载端口（P5 持久化钩子，通常就是执行树仓储本身）。
    /// <para>
    /// 为什么必须有它：跨命令从库里读回来的执行只带 <c>PARENT_ID_</c>，而流程变量都落在
    /// <b>流程实例执行</b>上，变量作用域上溯必须能把父执行加载回来，否则「完成任务后判网关条件」
    /// 这类跨命令读变量会得到 <see langword="null"/>。对应 Activiti
    /// <c>ExecutionEntityImpl.getParent()</c> 的懒加载。
    /// </para>
    /// </summary>
    IExecutionManager? ParentLoader { get; set; }
}
