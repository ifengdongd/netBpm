-- ============================================================================
--  NetBpm · SQL Server 数据库脚本（自动生成，请勿手改）
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
    tenant_id bigint not null,
    apply_id bigint not null,
    apply_time datetime not null,
    workflow_type_id nvarchar(64) not null,
    workflow_version nvarchar(50),
    [describe] nvarchar(200) not null,
    apply_emp_id nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_id bigint not null,
    business_dept_id nvarchar(50),
    status_id nvarchar(50) not null,
    newest_flag smallint not null constraint df_instances_newest_flag default (1),
    workflow_instance_id nvarchar(64),
    last_audited_time datetime,
    create_emp_id nvarchar(50) not null,
    create_time datetime not null,
    current_step nvarchar(200),
    assign_emp nvarchar(200),
    modify_emp_id nvarchar(50),
    modify_time datetime,
    deleted_flag tinyint not null constraint df_instances_deleted_flag default (0),
    original_business_id bigint,
    flow_file_id bigint,
    constraint pk_instances primary key clustered (apply_id)
);

create index ix_instances_workflow_instance_id on instances (workflow_instance_id);
create index ix_instances_business_id on instances (business_id);
create index ix_instances_status_id on instances (tenant_id, status_id);
create table instance_current_task (
    tenant_id bigint not null,
    step_id bigint not null,
    apply_id bigint,
    task_id nvarchar(64),
    task_node nvarchar(50),
    task_owner nvarchar(200),
    assign_emp_id nvarchar(200),
    form_key nvarchar(100),
    create_time datetime not null,
    constraint pk_instance_current_task primary key clustered (step_id)
);

create index ix_instance_current_task_apply_id on instance_current_task (apply_id);
create index ix_instance_current_task_task_id on instance_current_task (task_id);
create table deal_history (
    tenant_id bigint not null,
    deal_history_id bigint not null,
    business_id bigint,
    apply_id bigint,
    task_node nvarchar(50),
    deal_emp_id nvarchar(50),
    deal_time datetime,
    deal_result nvarchar(50),
    deal_comment nvarchar(2000),
    form_key nvarchar(200),
    deal_emp_name nvarchar(50),
    deal_domain_account nvarchar(50),
    constraint pk_deal_history primary key clustered (deal_history_id)
);

create index ix_deal_history_apply_id on deal_history (apply_id);
create index ix_deal_history_deal_emp_id on deal_history (tenant_id, deal_emp_id);

-- ─────────────────────────── 流程类型表 ───────────────────────────

create table workflow_type (
    tenant_id bigint not null,
    id nvarchar(64) not null,
    name nvarchar(50) not null,
    [describe] nvarchar(200) not null,
    view_url nvarchar(500) not null,
    approve_url nvarchar(500) not null,
    sort_order int not null constraint df_workflow_type_sort_order default (0),
    enabled tinyint not null constraint df_workflow_type_enabled default (1),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_workflow_type primary key clustered (id)
);

create index ix_workflow_type_sort on workflow_type (tenant_id, sort_order);

-- ─────────────────────────── 种子数据：订单审批 / 请假审批两条流程 ───────────────────────────

if not exists (select 1 from workflow_type where id = '02830358-46ef-40e5-b3b9-4d5c4eb23f9f')
    insert into workflow_type (tenant_id, id, name, [describe], view_url, approve_url, sort_order, enabled, create_time)
    values (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', N'订单审批流程', '/bill.html?process=order_process&businessId={businessId}', '/approve.html?process=order_process&taskId={taskId}', 1, 1, getutcdate());
go
if not exists (select 1 from workflow_type where id = '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7')
    insert into workflow_type (tenant_id, id, name, [describe], view_url, approve_url, sort_order, enabled, create_time)
    values (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', N'请假申请流程', '/bill.html?process=leave_process&businessId={businessId}', '/approve.html?process=leave_process&taskId={taskId}', 2, 1, getutcdate());
go


-- ─────────────────────────── 流程模型表（管理端设计器） ───────────────────────────

create table bpmn_model (
    id nvarchar(64) not null,
    tenant_id bigint not null,
    name nvarchar(200) not null,
    process_key nvarchar(50) not null,
    description nvarchar(500),
    bpmn_xml nvarchar(max) not null,
    svg nvarchar(max),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_bpmn_model primary key clustered (id)
);

create index ix_bpmn_model_key on bpmn_model (tenant_id, process_key);
create index ix_bpmn_model_modify on bpmn_model (tenant_id, modify_time);

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'instances';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_type_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程版本',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_version';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例描述',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'describe';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人部门',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应的业务id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'business_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例状态 审批中、已完成',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'status_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'是否是最新流程实例',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'newest_flag';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例id(对应于流程引擎中的id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后审批完成时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'last_audited_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'create_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'current_step';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前处理人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'assign_emp';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'modify_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'modify_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'删除标志',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'deleted_flag';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'原始业务ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'original_business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图文件ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'flow_file_id';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批步骤及审批人（订单审批/请假审批流程共用）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'instance_current_task';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'step_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_node';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'待办项归属人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_owner';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批者',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'assign_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'form_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'create_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批历史记录（订单审批/请假审批流程共用）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'deal_history';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_history_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应业务表单的ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤名称',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'task_node';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批者员工id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批结果',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_result';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批说明',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_comment';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'form_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人姓名',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_emp_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人域帐号',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_domain_account';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'workflow_type';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程名称（流程 key，= BPMN process/@id，如 order_process）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程描述',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'describe';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务单查看页地址（{businessId}/{processInstanceId} 占位符）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'view_url';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程审批页地址（{taskId}/{processInstanceId} 占位符）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'approve_url';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'展示顺序（小在前）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'sort_order';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'是否启用（0=停用，加载时跳过）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'enabled';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
    @level2type = N'COLUMN', @level2name = N'modify_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程模型表（管理端流程设计器的模型落点；BPMN XML 的编辑源，部署后产生 ACT_RE_PROCDEF 版本）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'bpmn_model';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'模型 ID（设计器 modelId，GUID 字符串）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程名称（BPMN process/@name）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程 key（= BPMN process/@id，如 order_process）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'process_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程描述',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'description';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'BPMN 2.0 XML 内容',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'bpmn_xml';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图 SVG 预览（前端导出，可选）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'svg';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
    @level2type = N'COLUMN', @level2name = N'modify_time';

go
