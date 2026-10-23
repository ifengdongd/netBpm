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
    /// <param name="executionId">持有该变量的作用域执行 id。</param>
    /// <param name="processInstanceId">所属流程实例 id。</param>
    /// <param name="name">变量名。</param>
    /// <param name="value">变量值。</param>
    /// <param name="tenantId">
    /// 租户 id。变量表原先不带租户列（跨租户可见 —— 见 <c>SchemaCatalog</c> 0011 迁移说明），
    /// 这里显式传入以落 <c>TENANT_ID_</c>；<see langword="null"/> 由仓储层归一成 <c>''</c>。
    /// </param>
    VariableInstance Create(
        string executionId,
        string? processInstanceId,
        string name,
        object? value,
        string? tenantId = null);

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

    /// <summary>
    /// <b>批量</b>查多个作用域持有的全部变量（N+1 消除的关键入口）。
    /// <para>
    /// 一次命令里常要沿作用域链上溯（子执行 → 父执行 → 流程实例），
    /// 逐条 <see cref="FindByExecutionIdAsync"/> 会产生与链长等量的往返；
    /// 这里一次拿回全部作用域的变量行，由调用方按 <c>EXECUTION_ID_</c> 分组。
    /// </para>
    /// <para>
    /// 过滤口径必须与 <see cref="FindByExecutionIdAsync"/> 一致：
    /// <c>where EXECUTION_ID_ in (...) and TASK_ID_ is null</c>。
    /// </para>
    /// </summary>
    /// <param name="executionIds">作用域执行 id 集合；为空时返回空集（不查全表）。</param>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyList<VariableInstance>> FindByExecutionIdsAsync(
        IReadOnlyCollection<string> executionIds,
        CancellationToken ct = default);

    /// <summary>
    /// 按租户查该租户下的全部运行态变量行（<c>where TENANT_ID_ = @tenantId</c>）。
    /// <para>
    /// 这是「跨租户变量不可见」在存储层的断言入口：没有 <c>TENANT_ID_</c> 列时
    /// 这个查询根本无法表达（只能全表返回 ⇒ 泄漏）。
    /// </para>
    /// <para>
    /// 与其余引擎表的缺省口径一致：<c>null</c> 租户等价于 <c>''</c>。
    /// </para>
    /// </summary>
    /// <param name="tenantId">租户 id。</param>
    /// <param name="ct">取消标记。</param>
    ValueTask<IReadOnlyList<VariableInstance>> FindByTenantIdAsync(
        string tenantId,
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
