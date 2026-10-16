namespace NetBpm.Engine.Execution;

/// <summary>
/// 变量实例管理器。对应 Activiti 的 <c>VariableInstanceEntityManager</c>。
/// <para>
/// 引擎侧只在需要跨命令读回变量时才接上它：内存宿主（<c>VariableInstanceManager == null</c>）
/// 的变量只活在执行对象上，与 P5 之前的行为完全一致。
/// </para>
/// </summary>
public interface IVariableInstanceManager
{
    /// <summary>
    /// 创建尚未落库的变量实例（对应 <c>VariableInstanceDataManager.create(name, type, value)</c>）。
    /// <para>注意 <c>REV_</c> 从 0 开始：Activiti 的数据管理器显式置 0，INSERT 再写 1。</para>
    /// </summary>
    VariableInstance Create(string executionId, string? processInstanceId, string name, object? value);

    /// <summary>插入变量（调用方填入 id 与 <c>REV_ = 1</c>），并记录历史。</summary>
    ValueTask InsertAsync(VariableInstance variable, CancellationToken ct = default);

    /// <summary>更新变量值（含类型变化）并记录历史；乐观锁冲突抛 <see cref="OptimisticLockingException"/>。</summary>
    ValueTask UpdateAsync(VariableInstance variable, object? value, CancellationToken ct = default);

    /// <summary>
    /// 查某作用域持有的<b>全部</b>变量。对应 <c>selectVariablesByExecutionId</c>：
    /// <c>where EXECUTION_ID_ = ? and TASK_ID_ is null</c>。
    /// </summary>
    ValueTask<IReadOnlyList<VariableInstance>> FindByExecutionIdAsync(
        string executionId,
        CancellationToken ct = default);

    /// <summary>按 (作用域, 名称) 查单个变量，未命中返回 <see langword="null"/>。</summary>
    ValueTask<VariableInstance?> FindByNameAsync(
        string executionId,
        string name,
        CancellationToken ct = default);

    /// <summary>
    /// 删除变量行（B2.4）：longString 的字节行一并删除。
    /// <para>
    /// ⚠️ <b>历史语义（有意为之）</b>：<c>ACT_HI_VARINST</c> 行<b>保留</b>——历史账本只增不删
    /// （与历史身份关系 <c>RecordIdentityLinkCreatedAsync</c> 的注释同一策略），删除运行状态不抹审计痕迹。
    /// </para>
    /// </summary>
    ValueTask DeleteAsync(VariableInstance variable, CancellationToken ct = default);

    /// <summary>查某任务的<b>任务局部变量</b>（<c>where TASK_ID_ = ?</c>）。</summary>
    ValueTask<IReadOnlyList<VariableInstance>> FindByTaskIdAsync(
        string taskId,
        CancellationToken ct = default);

    /// <summary>按任务删除其全部局部变量（运行行 + 字节行；历史行保留）。</summary>
    ValueTask DeleteByTaskIdAsync(string taskId, CancellationToken ct = default);
}
