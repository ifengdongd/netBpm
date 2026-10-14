-- ============================================================================
--  NetBpm · MySQL 业务层建表脚本：流程实例相关表（3 张）
-- ============================================================================
--  来源：业务系统存量库（SQL Server · wf schema，见 sqlserver/0003_create_process_instance_tables.sql）：
--        instances              流程实例表
--        instance_current_task  当前审批任务记录
--        deal_history           审批历史记录
--
--  与引擎表（ACT_*，见 0001_create_schema.sql）的关系：
--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_
--    instance_current_task.task_id   → ACT_RU_TASK.ID_
--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）
--
--  类型映射：nvarchar → varchar；datetime / bigint / smallint / tinyint 原样保留。
--  ⚠️ 主键均为业务侧生成的 BIGINT，不使用 AUTO_INCREMENT（与来源一致）。
--
--  ⚠️ 业务表改用 utf8mb4（deal_comment / describe 含长中文备注，预留 emoji / 扩展汉字空间）；
--     引擎表仍沿用 0001 的 utf8（对拍口径不变）。两套表之间不做跨库对拍，不受影响。
--
--  ➕ 在来源 DDL 基础上补充了 6 个查询索引（apply_id / task_id / workflow_instance_id /
--     business_id / status_id / deal_emp_id），不改变列结构与默认值。
--
--  执行顺序：0001_create_schema.sql → 0002_init_data.sql → 0003_create_process_instance_tables.sql
-- ============================================================================

-- ─────────────────────────── 流程实例表 ───────────────────────────

create table instances (
    tenant_id             bigint        not null comment '多租户id',
    apply_id              bigint        not null comment '申请id',
    apply_time            datetime      not null comment '申请时间',
    workflow_type_id      varchar(64)   not null comment '流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    workflow_version      varchar(50)   null comment '流程版本',
    `describe`            varchar(200)  not null comment '流程实例描述',
    apply_emp_id          varchar(50)   not null comment '申请人id',
    apply_dept_id         varchar(50)   not null comment '申请人部门',
    business_id           bigint        not null comment '对应的业务id',
    business_dept_id      varchar(50)   null comment '业务部门id',
    status_id             varchar(50)   not null comment '流程实例状态 审批中、已完成',
    newest_flag           smallint      not null default 1 comment '是否是最新流程实例',
    workflow_instance_id  varchar(64)   null comment '流程实例id(对应于流程引擎中的id）',
    last_audited_time     datetime      null comment '最后审批完成时间',
    create_emp_id         varchar(50)   not null comment '创建人',
    create_time           datetime      not null comment '创建时间',
    current_step          varchar(200)  null comment '当前步骤名称',
    assign_emp            varchar(200)  null comment '当前处理人',
    modify_emp_id         varchar(50)   null comment '修改人',
    modify_time           datetime      null comment '修改时间',
    deleted_flag          tinyint       not null default 0 comment '删除标志',
    original_business_id  bigint        null comment '原始业务ID',
    flow_file_id          bigint        null comment '流程图文件ID',
    primary key (apply_id),
    index ix_instances_workflow_instance_id (workflow_instance_id),
    index ix_instances_business_id (business_id),
    index ix_instances_status_id (tenant_id, status_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';

-- ─────────────────────────── 当前审批任务记录 ───────────────────────────

create table instance_current_task (
    tenant_id      bigint        not null comment '多租户ID',
    step_id        bigint        not null comment '数据行id',
    apply_id       bigint        null comment '申请id',
    task_id        varchar(64)   null comment '步骤ID',
    task_node      varchar(50)   null comment '当前步骤名称',
    task_owner     varchar(200)  null comment '待办项归属人',
    assign_emp_id  varchar(200)  null comment '当前审批者',
    form_key       varchar(100)  null comment '流程步骤对应的环节配置',
    create_time    datetime      not null comment '创建时间',
    primary key (step_id),
    index ix_instance_current_task_apply_id (apply_id),
    index ix_instance_current_task_task_id (task_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='当前审批步骤及审批人（订单审批/请假审批流程共用）';

-- ─────────────────────────── 审批历史记录 ───────────────────────────

create table deal_history (
    tenant_id            bigint         not null comment '多租户ID',
    deal_history_id      bigint         not null comment '数据行id',
    business_id          bigint         null comment '对应业务表单的ID',
    apply_id             bigint         null comment '申请id',
    task_node            varchar(50)    null comment '步骤名称',
    deal_emp_id          varchar(50)    null comment '审批者员工id',
    deal_time            datetime       null comment '审批时间',
    deal_result          varchar(50)    null comment '审批结果',
    deal_comment         varchar(2000)  null comment '审批说明',
    form_key             varchar(200)   null comment '流程步骤对应的环节配置',
    deal_emp_name        varchar(50)    null comment '审批人姓名',
    deal_domain_account  varchar(50)    null comment '审批人域帐号',
    primary key (deal_history_id),
    index ix_deal_history_apply_id (apply_id),
    index ix_deal_history_deal_emp_id (tenant_id, deal_emp_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='审批历史记录（订单审批/请假审批流程共用）';
