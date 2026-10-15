using NetBpm.Demo.Common;

namespace NetBpm.Demo.Storage;

/// <summary>MySqlWorkflowDataStore 的 SQL 常量（显式列名 + 别名，见 SnapshotAsync 的 remarks）。</summary>
public sealed partial class MySqlWorkflowDataStore
{
    // ───────────────────────── SQL ─────────────────────────

    private const string SelectInstancesSql = """
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

    private const string InsertInstanceSql = """
        insert into instances
            (tenant_id, apply_id, apply_time, workflow_type_id, workflow_version, `describe`,
             apply_emp_id, apply_dept_id, business_id, business_dept_id, status_id,
             workflow_instance_id, create_emp_id, create_time)
        values
            (@TenantId, @ApplyId, @ApplyTime, @WorkflowTypeId, @Version, @Describe,
             @ApplyEmpId, @ApplyDeptId, @BusinessId, @BusinessDeptId, @StatusId,
             @processInstanceId, @ApplyUser, @Now)
        """;

    private const string UpdateInstanceSql = """
        update instances
           set workflow_version = @Version,
               `describe`       = @Describe,
               business_dept_id = @BusinessDeptId,
               modify_time      = @Now
         where apply_id = @ApplyId
        """;

    private const string FinishInstanceSql = """
        update instances
           set status_id         = '已完成',
               current_step      = @CurrentStep,
               assign_emp        = null,
               last_audited_time = @CompletedAt,
               modify_emp_id     = @ModifyEmpId,
               modify_time       = @CompletedAt
         where workflow_instance_id = @ProcessInstanceId
        """;

    private const string UpdateInstanceStateSql = """
        update instances
           set current_step  = @CurrentStep,
               assign_emp    = @AssignEmp,
               modify_time   = @Now
         where apply_id = @ApplyId
        """;

    private const string SelectParametersSql = """
        select workflow_instance_id as WorkflowInstanceId,
               param_name           as ParamName,
               param_value          as ParamValue
          from workflow_parameters
         where workflow_instance_id = @processInstanceId
        """;

    private const string UpsertParameterSql = """
        insert into workflow_parameters
            (tenant_id, workflow_instance_id, param_name, param_value, create_time)
        values
            (@TenantId, @processInstanceId, @ParamName, @ParamValue, @Now)
        on duplicate key update
            param_value = values(param_value),
            modify_time = @Now
        """;

    private const string SelectDealHistorySql = """
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

    private const string InsertDealHistorySql = """
        insert into deal_history
            (tenant_id, deal_history_id, business_id, apply_id, task_node, deal_emp_id,
             deal_time, deal_result, deal_comment, form_key, deal_emp_name, deal_domain_account)
        values
            (@TenantId, @DealHistoryId, @BusinessId, @ApplyId, @TaskNode, @DealEmpId,
             @DealTime, @DealResult, @DealComment, @FormKey, @DealEmpName, @DealDomainAccount)
        """;

    private const string SelectCurrentTasksSql = """
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

    private const string InsertCurrentTaskSql = """
        insert into instance_current_task
            (tenant_id, step_id, apply_id, task_id, task_node, task_owner, assign_emp_id, form_key, create_time)
        values
            (@TenantId, @StepId, @ApplyId, @TaskId, @TaskNode, @TaskOwner, @AssignEmpId, @FormKey, @Now)
        """;

    private const string SelectInstanceSnapshotSql = """
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

    private const string SelectCurrentTaskSnapshotSql = """
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

    private const string SelectDealHistorySnapshotSql = """
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
