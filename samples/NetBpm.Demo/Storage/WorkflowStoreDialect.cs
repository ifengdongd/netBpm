namespace NetBpm.Demo.Storage;

/// <summary>
/// 业务存储的方言层：SQL 常量 + 展示名。
/// </summary>
/// <remarks>
/// <para>
/// 四张流程表（instances / instance_current_task / deal_history / workflow_parameters）的表结构
/// 双方言完全一致，只有 SQL 写法不同：
/// </para>
/// <list type="bullet">
///   <item><description><b>schema</b>：双方言一致 —— 业务表与引擎 ACT_* 同库同 schema
///     （MySQL 无前缀 / SQL Server 统一 dbo），表名双方言逐字相同；</description></item>
///   <item><description><b>单行查询</b>：MySQL <c>limit 1</c>；SQL Server <c>top (1)</c>；</description></item>
///   <item><description><b>保留字</b>：<c>describe</c> MySQL 反引号 / SQL Server 方括号；</description></item>
///   <item><description><b>upsert</b>：MySQL <c>on duplicate key update</c>；SQL Server 先 update、
///     <c>@@rowcount = 0</c> 再 insert；</description></item>
///   <item><description><b>字符串字面量</b>：SQL Server 用 <c>N'…'</c> 前缀。</description></item>
/// </list>
/// 逻辑主体在 <see cref="WorkflowDataStoreBase"/>，方言差异全部收敛在本接口的两个实现里。
/// </remarks>
public interface IWorkflowStoreDialect
{
    /// <summary><see cref="NetBpm.Workflow.Data.IWorkflowStoreInspector.Store"/> 返回值。</summary>
    string Store { get; }

    /// <summary>落点展示名（Location 前缀）：MySQL / SQL Server。</summary>
    string DisplayName { get; }

    // ───────────────────────── instances ─────────────────────────

    /// <summary>实例列表（含最后一条审批流水联查）。</summary>
    string SelectInstancesSql { get; }

    /// <summary>按实例 id 查 apply_id（单行）。</summary>
    string SelectApplyIdSql { get; }

    /// <summary>按实例 id 查 apply_id + business_id（单行，审批落库前校验）。</summary>
    string SelectInstanceRefSql { get; }

    string InsertInstanceSql { get; }

    string UpdateInstanceSql { get; }

    /// <summary>实例办结（置状态 + 结束环节 + 最后审批时间）。</summary>
    string FinishInstanceSql { get; }

    /// <summary>刷新当前环节 / 当前处理人。</summary>
    string UpdateInstanceStateSql { get; }

    // ───────────────────────── workflow_parameters ─────────────────────────

    string SelectParametersSql { get; }

    /// <summary>逐键 upsert。</summary>
    string UpsertParameterSql { get; }

    /// <summary>参数表全量快照（演示页落点自述）。</summary>
    string SelectParameterSnapshotSql { get; }

    // ───────────────────────── deal_history ─────────────────────────

    string SelectDealHistorySql { get; }

    string InsertDealHistorySql { get; }

    // ───────────────────────── instance_current_task ─────────────────────────

    string SelectCurrentTasksSql { get; }

    string InsertCurrentTaskSql { get; }

    /// <summary>按实例清空当前待办（整体替换前调用）。</summary>
    string DeleteCurrentTasksSql { get; }

    // ───────────────────────── 快照 ─────────────────────────

    string SelectInstanceSnapshotSql { get; }

    string SelectCurrentTaskSnapshotSql { get; }

    string SelectDealHistorySnapshotSql { get; }
}

/// <summary>MySQL 方言（db/mysql 脚本，业务表无 schema 前缀）。</summary>
public sealed class MySqlWorkflowStoreDialect : IWorkflowStoreDialect
{
    /// <summary>单例（SQL 常量不可变）。</summary>
    public static readonly MySqlWorkflowStoreDialect Default = new();

    private MySqlWorkflowStoreDialect()
    {
    }

    /// <inheritdoc />
    public string Store => "MySql";

    /// <inheritdoc />
    public string DisplayName => "MySQL";

    /// <inheritdoc />
    public string SelectInstancesSql => """
        select i.apply_id             as ApplyId,
               i.business_id          as BusinessId,
               i.business_dept_id     as BusinessDeptId,
               i.apply_emp_id         as ApplyEmpId,
               i.apply_dept_id        as ApplyDeptId,
               i.apply_time           as ApplyTime,
               i.workflow_type_id     as WorkflowTypeId,
               i.workflow_instance_id as WorkflowInstanceId,
               i.status_id            as StatusId,
               i.current_step         as CurrentStep,
               i.last_audited_time    as LastAuditedTime,
               h.deal_result          as LastResult,
               h.deal_comment         as LastComment
          from instances i
          left join deal_history h
            on h.deal_history_id = (
                 select h2.deal_history_id
                   from deal_history h2
                  where h2.apply_id = i.apply_id
                    and h2.deal_result is not null
                  order by h2.deal_time desc, h2.deal_history_id desc
                  limit 1)
        """;

    /// <inheritdoc />
    public string SelectApplyIdSql =>
        "select apply_id from instances where workflow_instance_id = @processInstanceId limit 1";

    /// <inheritdoc />
    public string SelectInstanceRefSql =>
        "select apply_id as ApplyId, business_id as BusinessId "
        + "from instances where workflow_instance_id = @processInstanceId limit 1";

    /// <inheritdoc />
    public string InsertInstanceSql => """
        insert into instances
            (tenant_id, apply_id, apply_time, workflow_type_id, workflow_version, `describe`,
             apply_emp_id, apply_dept_id, business_id, business_dept_id, status_id,
             workflow_instance_id, create_emp_id, create_time)
        values
            (@TenantId, @ApplyId, @ApplyTime, @WorkflowTypeId, @Version, @Describe,
             @ApplyEmpId, @ApplyDeptId, @BusinessId, @BusinessDeptId, @StatusId,
             @processInstanceId, @ApplyUser, @Now)
        """;

    /// <inheritdoc />
    public string UpdateInstanceSql => """
        update instances
           set workflow_version = @Version,
               `describe`       = @Describe,
               business_dept_id = @BusinessDeptId,
               modify_time      = @Now
         where apply_id = @ApplyId
        """;

    /// <inheritdoc />
    public string FinishInstanceSql => """
        update instances
           set status_id         = '已完成',
               current_step      = @CurrentStep,
               assign_emp        = null,
               last_audited_time = @CompletedAt,
               modify_emp_id     = @ModifyEmpId,
               modify_time       = @CompletedAt
         where workflow_instance_id = @ProcessInstanceId
        """;

    /// <inheritdoc />
    public string UpdateInstanceStateSql => """
        update instances
           set current_step  = @CurrentStep,
               assign_emp    = @AssignEmp,
               modify_time   = @Now
         where apply_id = @ApplyId
        """;

    /// <inheritdoc />
    public string SelectParametersSql => """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         where workflow_instance_id = @processInstanceId
        """;

    /// <inheritdoc />
    public string UpsertParameterSql => """
        insert into workflow_parameters
            (tenant_id, workflow_instance_id, param_name, param_value, create_time)
        values
            (@TenantId, @processInstanceId, @ParamName, @ParamValue, @Now)
        on duplicate key update
            param_value = values(param_value),
            modify_time = @Now
        """;

    /// <inheritdoc />
    public string SelectParameterSnapshotSql => """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         order by workflow_instance_id asc, param_name asc
        """;

    /// <inheritdoc />
    public string SelectDealHistorySql => """
        select h.deal_history_id as DealHistoryId,
               h.task_node       as TaskNode,
               h.deal_emp_id     as DealEmpId,
               h.deal_time       as DealTime,
               h.deal_result     as DealResult,
               h.deal_comment    as DealComment,
               h.form_key        as FormKey,
               i.status_id       as StatusId
          from deal_history h
          join instances i on i.apply_id = h.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by h.deal_time asc, h.deal_history_id asc
        """;

    /// <inheritdoc />
    public string InsertDealHistorySql => """
        insert into deal_history
            (tenant_id, deal_history_id, business_id, apply_id, task_node, deal_emp_id,
             deal_time, deal_result, deal_comment, form_key, deal_emp_name, deal_domain_account)
        values
            (@TenantId, @DealHistoryId, @BusinessId, @ApplyId, @TaskNode, @DealEmpId,
             @DealTime, @DealResult, @DealComment, @FormKey, @DealEmpName, @DealDomainAccount)
        """;

    /// <inheritdoc />
    public string SelectCurrentTasksSql => """
        select t.task_id       as TaskId,
               t.task_node     as TaskNode,
               t.task_owner    as TaskOwner,
               t.assign_emp_id as AssignEmpId,
               t.form_key      as FormKey,
               t.create_time   as CreateTime
          from instance_current_task t
          join instances i on i.apply_id = t.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by t.step_id asc
        """;

    /// <inheritdoc />
    public string InsertCurrentTaskSql => """
        insert into instance_current_task
            (tenant_id, step_id, apply_id, task_id, task_node, task_owner, assign_emp_id, form_key, create_time)
        values
            (@TenantId, @StepId, @ApplyId, @TaskId, @TaskNode, @TaskOwner, @AssignEmpId, @FormKey, @Now)
        """;

    /// <inheritdoc />
    public string DeleteCurrentTasksSql =>
        "delete from instance_current_task where apply_id = @applyId";

    /// <inheritdoc />
    public string SelectInstanceSnapshotSql => """
        select tenant_id             as TenantId,
               apply_id              as ApplyId,
               apply_time            as ApplyTime,
               workflow_type_id      as WorkflowTypeId,
               workflow_version      as WorkflowVersion,
               `describe`            as `Describe`,
               apply_emp_id          as ApplyEmpId,
               apply_dept_id         as ApplyDeptId,
               business_id           as BusinessId,
               business_dept_id      as BusinessDeptId,
               status_id             as StatusId,
               newest_flag           as NewestFlag,
               workflow_instance_id  as WorkflowInstanceId,
               last_audited_time     as LastAuditedTime,
               create_emp_id         as CreateEmpId,
               create_time           as CreateTime,
               current_step          as CurrentStep,
               assign_emp            as AssignEmp,
               modify_emp_id         as ModifyEmpId,
               modify_time           as ModifyTime,
               deleted_flag          as DeletedFlag,
               original_business_id  as OriginalBusinessId,
               flow_file_id          as FlowFileId
          from instances
         order by apply_id desc
        """;

    /// <inheritdoc />
    public string SelectCurrentTaskSnapshotSql => """
        select tenant_id     as TenantId,
               step_id       as StepId,
               apply_id      as ApplyId,
               task_id       as TaskId,
               task_node     as TaskNode,
               task_owner    as TaskOwner,
               assign_emp_id as AssignEmpId,
               form_key      as FormKey,
               create_time   as CreateTime
          from instance_current_task
         order by step_id desc
        """;

    /// <inheritdoc />
    public string SelectDealHistorySnapshotSql => """
        select tenant_id           as TenantId,
               deal_history_id     as DealHistoryId,
               business_id         as BusinessId,
               apply_id            as ApplyId,
               task_node           as TaskNode,
               deal_emp_id         as DealEmpId,
               deal_time           as DealTime,
               deal_result         as DealResult,
               deal_comment        as DealComment,
               form_key            as FormKey,
               deal_emp_name       as DealEmpName,
               deal_domain_account as DealDomainAccount
          from deal_history
         order by deal_history_id desc
        """;
}

/// <summary>SQL Server 方言（db/sqlserver 脚本，业务表统一 dbo，与 MySQL 表名逐字相同）。</summary>
public sealed class SqlServerWorkflowStoreDialect : IWorkflowStoreDialect
{
    /// <summary>单例（SQL 常量不可变）。</summary>
    public static readonly SqlServerWorkflowStoreDialect Default = new();

    private SqlServerWorkflowStoreDialect()
    {
    }

    /// <inheritdoc />
    public string Store => "SqlServer";

    /// <inheritdoc />
    public string DisplayName => "SQL Server";

    /// <inheritdoc />
    public string SelectInstancesSql => """
        select i.apply_id             as ApplyId,
               i.business_id          as BusinessId,
               i.business_dept_id     as BusinessDeptId,
               i.apply_emp_id         as ApplyEmpId,
               i.apply_dept_id        as ApplyDeptId,
               i.apply_time           as ApplyTime,
               i.workflow_type_id     as WorkflowTypeId,
               i.workflow_instance_id as WorkflowInstanceId,
               i.status_id            as StatusId,
               i.current_step         as CurrentStep,
               i.last_audited_time    as LastAuditedTime,
               h.deal_result          as LastResult,
               h.deal_comment         as LastComment
          from instances i
          left join deal_history h
            on h.deal_history_id = (
                 select top (1) h2.deal_history_id
                   from deal_history h2
                  where h2.apply_id = i.apply_id
                    and h2.deal_result is not null
                  order by h2.deal_time desc, h2.deal_history_id desc)
        """;

    /// <inheritdoc />
    public string SelectApplyIdSql =>
        "select top (1) apply_id from instances where workflow_instance_id = @processInstanceId";

    /// <inheritdoc />
    public string SelectInstanceRefSql =>
        "select top (1) apply_id as ApplyId, business_id as BusinessId "
        + "from instances where workflow_instance_id = @processInstanceId";

    /// <inheritdoc />
    public string InsertInstanceSql => """
        insert into instances
            (tenant_id, apply_id, apply_time, workflow_type_id, workflow_version, [describe],
             apply_emp_id, apply_dept_id, business_id, business_dept_id, status_id,
             workflow_instance_id, create_emp_id, create_time)
        values
            (@TenantId, @ApplyId, @ApplyTime, @WorkflowTypeId, @Version, @Describe,
             @ApplyEmpId, @ApplyDeptId, @BusinessId, @BusinessDeptId, @StatusId,
             @processInstanceId, @ApplyUser, @Now)
        """;

    /// <inheritdoc />
    public string UpdateInstanceSql => """
        update instances
           set workflow_version = @Version,
               [describe]       = @Describe,
               business_dept_id = @BusinessDeptId,
               modify_time      = @Now
         where apply_id = @ApplyId
        """;

    /// <inheritdoc />
    public string FinishInstanceSql => """
        update instances
           set status_id         = N'已完成',
               current_step      = @CurrentStep,
               assign_emp        = null,
               last_audited_time = @CompletedAt,
               modify_emp_id     = @ModifyEmpId,
               modify_time       = @CompletedAt
         where workflow_instance_id = @ProcessInstanceId
        """;

    /// <inheritdoc />
    public string UpdateInstanceStateSql => """
        update instances
           set current_step  = @CurrentStep,
               assign_emp    = @AssignEmp,
               modify_time   = @Now
         where apply_id = @ApplyId
        """;

    /// <inheritdoc />
    public string SelectParametersSql => """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         where workflow_instance_id = @processInstanceId
        """;

    /// <inheritdoc />
    public string UpsertParameterSql => """
        update workflow_parameters
           set param_value = @ParamValue,
               modify_time = @Now
         where workflow_instance_id = @processInstanceId
           and param_name = @ParamName;

        if @@rowcount = 0
            insert into workflow_parameters
                (tenant_id, workflow_instance_id, param_name, param_value, create_time)
            values
                (@TenantId, @processInstanceId, @ParamName, @ParamValue, @Now);
        """;

    /// <inheritdoc />
    public string SelectParameterSnapshotSql => """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         order by workflow_instance_id asc, param_name asc
        """;

    /// <inheritdoc />
    public string SelectDealHistorySql => """
        select h.deal_history_id as DealHistoryId,
               h.task_node       as TaskNode,
               h.deal_emp_id     as DealEmpId,
               h.deal_time       as DealTime,
               h.deal_result     as DealResult,
               h.deal_comment    as DealComment,
               h.form_key        as FormKey,
               i.status_id       as StatusId
          from deal_history h
          join instances i on i.apply_id = h.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by h.deal_time asc, h.deal_history_id asc
        """;

    /// <inheritdoc />
    public string InsertDealHistorySql => """
        insert into deal_history
            (tenant_id, deal_history_id, business_id, apply_id, task_node, deal_emp_id,
             deal_time, deal_result, deal_comment, form_key, deal_emp_name, deal_domain_account)
        values
            (@TenantId, @DealHistoryId, @BusinessId, @ApplyId, @TaskNode, @DealEmpId,
             @DealTime, @DealResult, @DealComment, @FormKey, @DealEmpName, @DealDomainAccount)
        """;

    /// <inheritdoc />
    public string SelectCurrentTasksSql => """
        select t.task_id       as TaskId,
               t.task_node     as TaskNode,
               t.task_owner    as TaskOwner,
               t.assign_emp_id as AssignEmpId,
               t.form_key      as FormKey,
               t.create_time   as CreateTime
          from instance_current_task t
          join instances i on i.apply_id = t.apply_id
         where i.workflow_instance_id = @processInstanceId
         order by t.step_id asc
        """;

    /// <inheritdoc />
    public string InsertCurrentTaskSql => """
        insert into instance_current_task
            (tenant_id, step_id, apply_id, task_id, task_node, task_owner, assign_emp_id, form_key, create_time)
        values
            (@TenantId, @StepId, @ApplyId, @TaskId, @TaskNode, @TaskOwner, @AssignEmpId, @FormKey, @Now)
        """;

    /// <inheritdoc />
    public string DeleteCurrentTasksSql =>
        "delete from instance_current_task where apply_id = @applyId";

    /// <inheritdoc />
    public string SelectInstanceSnapshotSql => """
        select tenant_id             as TenantId,
               apply_id              as ApplyId,
               apply_time            as ApplyTime,
               workflow_type_id      as WorkflowTypeId,
               workflow_version      as WorkflowVersion,
               [describe]            as [Describe],
               apply_emp_id          as ApplyEmpId,
               apply_dept_id         as ApplyDeptId,
               business_id           as BusinessId,
               business_dept_id      as BusinessDeptId,
               status_id             as StatusId,
               newest_flag           as NewestFlag,
               workflow_instance_id  as WorkflowInstanceId,
               last_audited_time     as LastAuditedTime,
               create_emp_id         as CreateEmpId,
               create_time           as CreateTime,
               current_step          as CurrentStep,
               assign_emp            as AssignEmp,
               modify_emp_id         as ModifyEmpId,
               modify_time           as ModifyTime,
               deleted_flag          as DeletedFlag,
               original_business_id  as OriginalBusinessId,
               flow_file_id          as FlowFileId
          from instances
         order by apply_id desc
        """;

    /// <inheritdoc />
    public string SelectCurrentTaskSnapshotSql => """
        select tenant_id     as TenantId,
               step_id       as StepId,
               apply_id      as ApplyId,
               task_id       as TaskId,
               task_node     as TaskNode,
               task_owner    as TaskOwner,
               assign_emp_id as AssignEmpId,
               form_key      as FormKey,
               create_time   as CreateTime
          from instance_current_task
         order by step_id desc
        """;

    /// <inheritdoc />
    public string SelectDealHistorySnapshotSql => """
        select tenant_id           as TenantId,
               deal_history_id     as DealHistoryId,
               business_id         as BusinessId,
               apply_id            as ApplyId,
               task_node           as TaskNode,
               deal_emp_id         as DealEmpId,
               deal_time           as DealTime,
               deal_result         as DealResult,
               deal_comment        as DealComment,
               form_key            as FormKey,
               deal_emp_name       as DealEmpName,
               deal_domain_account as DealDomainAccount
          from deal_history
         order by deal_history_id desc
        """;
}
