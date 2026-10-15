-- ============================================================================
--  NetBpm · SQL Server 业务层建表脚本：流程参数表（1 张）
-- ============================================================================
--  来源：**本项目新增** —— 合并审批 Demo（订单 + 请假两条流程）共用一个
--        IWorkflowDataStore，流程参数要有个与具体业务单无关的落点：
--        订单参数（product_name / amount / remark）与请假参数（days / reason）
--        键集不同，统一落这张「实例 × 参数名」键值表。
--
--  与 0003 三张表的关系：
--    workflow_parameters.workflow_instance_id → wf.instances.workflow_instance_id
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007
-- ============================================================================

create table wf.workflow_parameters (
    tenant_id             bigint         not null,
    workflow_instance_id  nvarchar(64)   not null,
    param_name            nvarchar(100)  not null,
    param_value           nvarchar(max)  null,
    create_time           datetime       not null,
    modify_time           datetime       null,
    constraint pk_workflow_parameters primary key (workflow_instance_id, param_name)
);
go

if not exists (select * from sys.indexes where name = N'ix_workflow_parameters_instance')
    create index ix_workflow_parameters_instance on wf.workflow_parameters (workflow_instance_id);
go
