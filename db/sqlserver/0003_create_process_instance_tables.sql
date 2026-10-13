-- ============================================================================
--  NetBpm · SQL Server 业务层建表脚本：流程实例相关表（3 张）
-- ============================================================================
--  来源：业务系统存量库（wf schema）：
--        wf.instances              流程实例表
--        wf.instance_current_task  当前审批任务记录
--        wf.deal_history           审批历史记录
--
--  与引擎表（dbo.ACT_*，见 0001_create_schema.sql）的关系：
--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_
--    instance_current_task.task_id   → ACT_RU_TASK.ID_
--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）
--
--  ⚠️ 表名、列名与来源库逐字一致（含 [wf] schema），请勿改名。
--     主键均为业务侧生成的 BIGINT，不使用 IDENTITY（与来源一致）。
--
--  ➕ 在来源 DDL 基础上补充了 3 个查询索引（apply_id / workflow_instance_id / business_id），
--     不改变列结构与默认值；如需与来源完全一致可删掉索引一节。
--
--  执行顺序：0001_create_schema.sql → 0002_init_data.sql → 0003_create_process_instance_tables.sql
-- ============================================================================

-- ─────────────────────────── schema（来源库使用 wf）───────────────────────────

if not exists (select * from sys.schemas where name = N'wf')
    exec('create schema wf');
go

-- ─────────────────────────── 流程实例表 ───────────────────────────

create table wf.instances (
    tenant_id              bigint        not null,
    apply_id               bigint        not null,
    apply_time             datetime      not null,
    workflow_type_id       bigint        not null,
    workflow_version       nvarchar(50)  null,
    [describe]             nvarchar(200) not null,
    apply_emp_id           nvarchar(50)  not null,
    apply_dept_id          nvarchar(50)  not null,
    business_id            bigint        not null,
    business_dept_id       nvarchar(50)  null,
    status_id              nvarchar(50)  not null,
    newest_flag            smallint      not null constraint DF_instances_newest_flag default (1),
    workflow_instance_id   nvarchar(64)  null,
    last_audited_time      datetime      null,
    create_emp_id          nvarchar(50)  not null,
    create_time            datetime      not null,
    current_step           nvarchar(200) null,
    assign_emp             nvarchar(200) null,
    modify_emp_id          nvarchar(50)  null,
    modify_time            datetime      null,
    deleted_flag           tinyint       not null constraint DF_instances_deleted_flag default (0),
    original_business_id   bigint        null,
    flow_file_id           bigint        null,
    constraint PK_instances primary key clustered (apply_id)
);

-- ─────────────────────────── 当前审批任务记录 ───────────────────────────

create table wf.instance_current_task (
    tenant_id       bigint        not null,
    step_id         bigint        not null,
    apply_id        bigint        null,
    task_id         nvarchar(64)  null,
    task_node       nvarchar(50)  null,
    task_owner      nvarchar(200) null,
    assign_emp_id   nvarchar(200) null,
    form_key        nvarchar(100) null,
    create_time     datetime      not null,
    constraint pk__instance__3fb50f9471de71d4 primary key clustered (step_id)
);

-- ─────────────────────────── 审批历史记录 ───────────────────────────

create table wf.deal_history (
    tenant_id            bigint         not null,
    deal_history_id      bigint         not null,
    business_id          bigint         null,
    apply_id             bigint         null,
    task_node            nvarchar(50)   null,
    deal_emp_id          nvarchar(50)   null,
    deal_time            datetime       null,
    deal_result          nvarchar(50)   null,
    deal_comment         nvarchar(2000) null,
    form_key             nvarchar(200)  null,
    deal_emp_name        nvarchar(50)   null,
    deal_domain_account  nvarchar(50)   null,
    constraint pk__audit_histroy primary key clustered (deal_history_id)
);

-- ─────────────────────────── 索引（来源 DDL 之外的补充）───────────────────────────

create index ix_wf_instances_workflow_instance_id on wf.instances(workflow_instance_id);
create index ix_wf_instances_business_id          on wf.instances(business_id);
create index ix_wf_instances_status_id            on wf.instances(tenant_id, status_id);
create index ix_wf_instance_current_task_apply_id on wf.instance_current_task(apply_id);
create index ix_wf_instance_current_task_task_id  on wf.instance_current_task(task_id);
create index ix_wf_deal_history_apply_id          on wf.deal_history(apply_id);
create index ix_wf_deal_history_deal_emp_id       on wf.deal_history(tenant_id, deal_emp_id);

go

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description）───────────────────────────

-- wf.instances / 流程实例表
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例表',
    @level0type = N'SCHEMA', @level0name = N'wf',
    @level1type = N'TABLE', @level1name = N'instances';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型（对应cfg.Workflow_type）',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_type_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程版本',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_version';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例描述',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'describe';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人部门',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'apply_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应的业务id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'business_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例状态 审批中、已完成',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'status_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'是否是最新流程实例',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'newest_flag';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例id(对应于流程引擎中的id）',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'workflow_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后审批完成时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'last_audited_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建人',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'create_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'current_step';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前处理人',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'assign_emp';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改人',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'modify_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'modify_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'删除标志',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'deleted_flag';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'原始业务ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'original_business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图文件ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instances',
    @level2type = N'COLUMN', @level2name = N'flow_file_id';

-- wf.instance_current_task / 当前审批步骤及审批人
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批步骤及审批人',
    @level0type = N'SCHEMA', @level0name = N'wf',
    @level1type = N'TABLE', @level1name = N'instance_current_task';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'step_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_node';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'待办项归属人',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'task_owner';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批者',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'assign_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'form_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'instance_current_task',
    @level2type = N'COLUMN', @level2name = N'create_time';

-- wf.deal_history / 审批历史记录
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批历史记录',
    @level0type = N'SCHEMA', @level0name = N'wf',
    @level1type = N'TABLE', @level1name = N'deal_history';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_history_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应业务表单的ID',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'apply_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤名称',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'task_node';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批者员工id',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批时间',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批结果',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_result';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批说明',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_comment';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'form_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人姓名',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_emp_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人域帐号',
    @level0type = N'SCHEMA', @level0name = N'wf', @level1type = N'TABLE', @level1name = N'deal_history',
    @level2type = N'COLUMN', @level2name = N'deal_domain_account';

go
