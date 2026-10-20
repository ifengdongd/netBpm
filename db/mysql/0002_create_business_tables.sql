-- ============================================================================
--  NetBpm · MySQL 数据库脚本（自动生成，请勿手改）
-- ============================================================================
--  本文件由单一数据源生成，两份方言（mysql / sqlserver）结构严格一一对应，
--  差异仅为方言语法（类型名 / 注释写法 / 批分隔 / 幂等守卫）。
--    · 单一数据源：tools/NetBpm.DbInit/Schema/SchemaCatalog.cs
--    · 方言渲染器：tools/NetBpm.DbInit/Schema/SqlGenerator.cs
--  重新生成：dotnet run --project tools/NetBpm.DbInit -- --generate-only
--  对齐决策清单：见 db/README.md「对齐决策清单」。
-- ============================================================================

--  来源：业务系统存量库（原 wf schema，本库统一落 dbo / 无前缀，表名逐字相同）：
--        instances              流程实例表
--        instance_current_task  当前审批任务记录
--        deal_history           审批历史记录
--
--  与引擎表（ACT_*，见 0001_create_engine_schema.sql）的关系：
--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_
--    instance_current_task.task_id   → ACT_RU_TASK.ID_
--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）
--
--  类型映射：nvarchar → varchar；datetime / bigint / smallint / tinyint 原样保留。
--  ⚠️ 主键均为业务侧生成的 BIGINT，不使用自增（与来源一致）。
--  ⚠️ 业务表用 utf8mb4（deal_comment / describe 含长中文备注，预留 emoji / 扩展汉字空间），
--     引擎表仍沿用 0001 的 utf8（对拍口径不变）。
--  ➕ 补充了查询索引（apply_id / task_id / workflow_instance_id / business_id / status_id /
--     deal_emp_id），不改变列结构与默认值。
--
--  流程类型表（workflow_type）：流程信息枚举（ProcessInfoEnum）的数据源，
--    流程的 Id / Name / Describe / ViewUrl / ApproveUrl 配置在表里，系统启动时加载。
--    workflow_type.id = instances.workflow_type_id（实例登记的类型 id）。
--    name 必须与 BPMN process/@id（流程 key）一致；id 建议用设计器 modelId。
--
--  流程模型表（bpmn_model）：管理端流程设计器的模型落点（BPMN XML 编辑源），
--    与部署结果（ACT_RE_PROCDEF）分离 —— 模型可反复编辑，部署才产生新版本。
--
--  执行顺序：0001_create_engine_schema.sql → 0002_create_business_tables.sql（本文件）
-- ============================================================================

-- ─────────────────────────── 流程实例表（3 张）───────────────────────────

create table instances (
    tenant_id bigint not null comment '多租户id',
    apply_id bigint not null comment '申请id',
    apply_time datetime not null comment '申请时间',
    workflow_type_id varchar(64) not null comment '流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    workflow_version varchar(50) comment '流程版本',
    `describe` varchar(200) not null comment '流程实例描述',
    apply_emp_id varchar(50) not null comment '申请人id',
    apply_dept_id varchar(50) not null comment '申请人部门',
    business_id bigint not null comment '对应的业务id',
    business_dept_id varchar(50) comment '业务部门id',
    status_id varchar(50) not null comment '流程实例状态 审批中、已完成',
    newest_flag smallint not null default 1 comment '是否是最新流程实例',
    workflow_instance_id varchar(64) comment '流程实例id(对应于流程引擎中的id）',
    last_audited_time datetime comment '最后审批完成时间',
    create_emp_id varchar(50) not null comment '创建人',
    create_time datetime not null comment '创建时间',
    current_step varchar(200) comment '当前步骤名称',
    assign_emp varchar(200) comment '当前处理人',
    modify_emp_id varchar(50) comment '修改人',
    modify_time datetime comment '修改时间',
    deleted_flag tinyint not null default 0 comment '删除标志',
    original_business_id bigint comment '原始业务ID',
    flow_file_id bigint comment '流程图文件ID',
    primary key (apply_id),
    index ix_instances_workflow_instance_id (workflow_instance_id),
    index ix_instances_business_id (business_id),
    index ix_instances_status_id (tenant_id, status_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';

create table instance_current_task (
    tenant_id bigint not null comment '多租户ID',
    step_id bigint not null comment '数据行id',
    apply_id bigint comment '申请id',
    task_id varchar(64) comment '步骤ID',
    task_node varchar(50) comment '当前步骤名称',
    task_owner varchar(200) comment '待办项归属人',
    assign_emp_id varchar(200) comment '当前审批者',
    form_key varchar(100) comment '流程步骤对应的环节配置',
    create_time datetime not null comment '创建时间',
    primary key (step_id),
    index ix_instance_current_task_apply_id (apply_id),
    index ix_instance_current_task_task_id (task_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='当前审批步骤及审批人（订单审批/请假审批流程共用）';

create table deal_history (
    tenant_id bigint not null comment '多租户ID',
    deal_history_id bigint not null comment '数据行id',
    business_id bigint comment '对应业务表单的ID',
    apply_id bigint comment '申请id',
    task_node varchar(50) comment '步骤名称',
    deal_emp_id varchar(50) comment '审批者员工id',
    deal_time datetime comment '审批时间',
    deal_result varchar(50) comment '审批结果',
    deal_comment varchar(2000) comment '审批说明',
    form_key varchar(200) comment '流程步骤对应的环节配置',
    deal_emp_name varchar(50) comment '审批人姓名',
    deal_domain_account varchar(50) comment '审批人域帐号',
    primary key (deal_history_id),
    index ix_deal_history_apply_id (apply_id),
    index ix_deal_history_deal_emp_id (tenant_id, deal_emp_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='审批历史记录（订单审批/请假审批流程共用）';


-- ─────────────────────────── 流程类型表 ───────────────────────────

create table workflow_type (
    tenant_id bigint not null comment '多租户id',
    id varchar(64) not null comment '流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
    name varchar(50) not null comment '流程名称（流程 key，= BPMN process/@id，如 order_process）',
    `describe` varchar(200) not null comment '流程描述',
    view_url varchar(500) not null comment '业务单查看页地址（{businessId}/{processInstanceId} 占位符）',
    approve_url varchar(500) not null comment '流程审批页地址（{taskId}/{processInstanceId} 占位符）',
    sort_order int not null default 0 comment '展示顺序（小在前）',
    enabled tinyint not null default 1 comment '是否启用（0=停用，加载时跳过）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (id),
    index ix_workflow_type_sort (tenant_id, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）';


-- ─────────────────────────── 种子数据：订单审批 / 请假审批两条流程 ───────────────────────────

insert into workflow_type (tenant_id, id, name, `describe`, view_url, approve_url, sort_order, enabled, create_time)
values (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', '订单审批流程', '/bill.html?process=order_process&businessId={businessId}', '/approve.html?process=order_process&taskId={taskId}', 1, 1, utc_timestamp()),
       (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', '请假申请流程', '/bill.html?process=leave_process&businessId={businessId}', '/approve.html?process=leave_process&taskId={taskId}', 2, 1, utc_timestamp());


-- ─────────────────────────── 流程模型表（管理端设计器） ───────────────────────────

create table bpmn_model (
    id varchar(64) not null comment '模型 ID（设计器 modelId，GUID 字符串）',
    tenant_id bigint not null comment '多租户 id',
    name varchar(200) not null comment '流程名称（BPMN process/@name）',
    process_key varchar(50) not null comment '流程 key（= BPMN process/@id，如 order_process）',
    description varchar(500) comment '流程描述',
    bpmn_xml text not null comment 'BPMN 2.0 XML 内容',
    svg text comment '流程图 SVG 预览（前端导出，可选）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (id),
    index ix_bpmn_model_key (tenant_id, process_key),
    index ix_bpmn_model_modify (tenant_id, modify_time)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程模型表（管理端流程设计器的模型落点；BPMN XML 的编辑源，部署后产生 ACT_RE_PROCDEF 版本）';

