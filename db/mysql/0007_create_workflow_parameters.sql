-- ============================================================================
--  NetBpm · MySQL 业务层建表脚本：流程参数表（1 张）
-- ============================================================================
--  来源：**本项目新增** —— 合并审批 Demo（订单 + 请假两条流程）共用一个
--        IWorkflowDataStore，流程参数要有个与具体业务单无关的落点：
--        订单参数（product_name / amount / remark）与请假参数（days / reason）
--        键集不同，leave_bill 的真列装不下，统一落这张「实例 × 参数名」键值表。
--
--  与 0003 三张表的关系：
--    workflow_parameters.workflow_instance_id → instances.workflow_instance_id
--
--  ⚠️ param_value 用 text：订单金额、长事由都可能超 varchar(200)。
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007
-- ============================================================================

create table workflow_parameters (
    tenant_id             bigint        not null comment '多租户id',
    workflow_instance_id  varchar(64)   not null comment '流程实例id（对应 instances.workflow_instance_id）',
    param_name            varchar(100)  not null comment '参数名（如 days / reason / product_name）',
    param_value           text          null comment '参数值（null = 显式置空）',
    create_time           datetime      not null comment '创建时间',
    modify_time           datetime      null comment '最后修改时间',
    primary key (workflow_instance_id, param_name),
    index ix_workflow_parameters_instance (workflow_instance_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）';
