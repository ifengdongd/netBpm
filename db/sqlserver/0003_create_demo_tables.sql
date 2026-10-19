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

--  来源：**本项目新增** —— 订单审批 + 请假审批两条 Demo 的业务数据表。
--
--  包含：
--    leave_bill          请假单（days / reason 真列 + ext_params JSON 扩展参数）
--    order_bill          订单单（status 随审批回写：1=审批中，-1=拒绝，100=通过）
--    workflow_parameters 流程参数键值表（「实例 × 参数名」；param_value 用 text / nvarchar(max)）
--    sys_*               组织架构表（部门 / 员工 / 审批组 / 组成员，幂等，含演示种子数据）
--
--  与流程实例表（0002_create_business_tables.sql）的关系：
--    instances.business_id          → leave_bill.business_id / order_bill.business_id
--    instances.workflow_instance_id → workflow_parameters.workflow_instance_id
--    instances.apply_emp_id         → sys_emp.domain_account（申请人在流程里的用户 ID）
--    instances.apply_dept_id        → sys_dept.dept_id
--
--  幂等性：sys_* 建表与种子可重复执行；其余三张（leave_bill / order_bill /
--          workflow_parameters）一次性建表。
--
--  执行顺序：0001 → 0002 → 0003_create_demo_tables.sql（本文件）
-- ============================================================================

-- ─────────────────────────── 业务表单（3 张）───────────────────────────

create table leave_bill (
    tenant_id bigint not null,
    business_id bigint not null,
    apply_user nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_dept_id nvarchar(50),
    days smallint,
    reason nvarchar(500),
    ext_params nvarchar(2000),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_leave_bill primary key clustered (business_id)
);

create index ix_leave_bill_apply_user on leave_bill (tenant_id, apply_user);
create table order_bill (
    tenant_id bigint not null,
    business_id bigint not null,
    product_name nvarchar(200),
    amount decimal(18,2),
    remark nvarchar(500),
    status smallint not null constraint df_order_bill_status default (1),
    approve_comment nvarchar(500),
    apply_user nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_dept_id nvarchar(50),
    process_instance_id nvarchar(64),
    create_time datetime not null,
    modify_time datetime,
    deleted_flag tinyint not null constraint df_order_bill_deleted_flag default (0),
    constraint pk_order_bill primary key clustered (business_id)
);

create index ix_order_bill_apply_user on order_bill (tenant_id, apply_user);
create index ix_order_bill_status on order_bill (tenant_id, status);
create table workflow_parameters (
    tenant_id bigint not null,
    workflow_instance_id nvarchar(64) not null,
    param_name nvarchar(100) not null,
    param_value nvarchar(max),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_workflow_parameters primary key clustered (workflow_instance_id, param_name)
);

create index ix_workflow_parameters_instance on workflow_parameters (workflow_instance_id);

-- ─────────────────────────── 组织架构表（sys_*，幂等可重复执行） ───────────────────────────

if object_id('sys_dept', 'U') is null
    create table sys_dept (
    dept_id bigint not null,
    tenant_id bigint not null,
    dept_name nvarchar(100) not null,
    parent_id bigint,
    sort_order int not null constraint df_sys_dept_sort_order default (0),
    create_time datetime not null,
    constraint pk_sys_dept primary key clustered (dept_id)
    )
go

if not exists (select 1 from sys.indexes where name = 'idx_sys_dept_tenant' and object_id = object_id('sys_dept'))
create index idx_sys_dept_tenant on sys_dept (tenant_id, parent_id);
go
if object_id('sys_emp', 'U') is null
    create table sys_emp (
    emp_id bigint not null,
    tenant_id bigint not null,
    emp_name nvarchar(50) not null,
    domain_account nvarchar(50) not null,
    dept_id bigint not null,
    create_time datetime not null,
    constraint pk_sys_emp primary key clustered (emp_id)
    )
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_account' and object_id = object_id('sys_emp'))
create unique index uk_sys_emp_account on sys_emp (tenant_id, domain_account);
go
if not exists (select 1 from sys.indexes where name = 'idx_sys_emp_dept' and object_id = object_id('sys_emp'))
create index idx_sys_emp_dept on sys_emp (dept_id);
go
if object_id('sys_emp_group', 'U') is null
    create table sys_emp_group (
    group_id nvarchar(50) not null,
    tenant_id bigint not null,
    group_name nvarchar(100) not null,
    sort_order int not null constraint df_sys_emp_group_sort_order default (0),
    create_time datetime not null,
    constraint pk_sys_emp_group primary key clustered (group_id)
    )
go

if object_id('sys_emp_group_member', 'U') is null
    create table sys_emp_group_member (
    member_id bigint not null,
    tenant_id bigint not null,
    group_id nvarchar(50) not null,
    emp_id bigint not null,
    create_time datetime not null,
    constraint pk_sys_emp_group_member primary key clustered (member_id)
    )
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_group_member' and object_id = object_id('sys_emp_group_member'))
create unique index uk_sys_emp_group_member on sys_emp_group_member (group_id, emp_id);
go
if not exists (select 1 from sys.indexes where name = 'idx_sys_emp_group_member_emp' and object_id = object_id('sys_emp_group_member'))
create index idx_sys_emp_group_member_emp on sys_emp_group_member (emp_id);
go

-- ─────────────────────────── 种子数据（演示用，可重复执行） ───────────────────────────

if not exists (select 1 from sys_dept where dept_id = 101)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (101, 1, N'信息技术部', null, 1, getutcdate());
go
if not exists (select 1 from sys_dept where dept_id = 102)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (102, 1, N'成本管理部', null, 2, getutcdate());
go
if not exists (select 1 from sys_dept where dept_id = 103)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (103, 1, N'综合管理部', null, 3, getutcdate());
go

if not exists (select 1 from sys_emp where emp_id = 1001)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1001, 1, N'王小爱', 'alice', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1002)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1002, 1, N'李小波', 'bob', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1003)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1003, 1, N'赵志强', 'leader1', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1004)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1004, 1, N'钱有为', 'leader2', 102, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1005)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1005, 1, N'孙建国', 'manager1', 103, getutcdate());
go

if not exists (select 1 from sys_emp_group where group_id = 'leaders')
    insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
    values ('leaders', 1, N'组长', 1, getutcdate());
go
if not exists (select 1 from sys_emp_group where group_id = 'managers')
    insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
    values ('managers', 1, N'经理', 2, getutcdate());
go

if not exists (select 1 from sys_emp_group_member where group_id = 'leaders' and emp_id = 1003)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (1, 1, 'leaders', 1003, getutcdate());
go
if not exists (select 1 from sys_emp_group_member where group_id = 'leaders' and emp_id = 1004)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (2, 1, 'leaders', 1004, getutcdate());
go
if not exists (select 1 from sys_emp_group_member where group_id = 'managers' and emp_id = 1005)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (3, 1, 'managers', 1005, getutcdate());
go


-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'leave_bill';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务id（请假单号，由业务侧生成）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'apply_user';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请部门',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'apply_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'business_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假天数（对应流程参数 days）；NULL = 还没填',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'days';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假事由（对应流程参数 reason）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'reason';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'扩展流程参数（JSON 对象）：除 days / reason 外的键值对',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'ext_params';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
    @level2type = N'COLUMN', @level2name = N'modify_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'order_bill';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务id（订单单号，由业务侧生成）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'business_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'订购产品（对应流程参数 product_name）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'product_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'订单金额（对应流程参数 amount）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'amount';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'备注（对应流程参数 remark）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'remark';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'状态：1=审批中，-1=审批拒绝，100=审批通过',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'status';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最终审批意见（审批结束时随状态一起写回）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'approve_comment';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'apply_user';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请部门',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'apply_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'business_dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id（提交申请后回填，用于反查审批进度）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'process_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'modify_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'伪删除标志（0 正常 / 1 已删除；公共数据操作接口 DeleteAsync 置位，列表与明细自动过滤）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
    @level2type = N'COLUMN', @level2name = N'deleted_flag';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'workflow_parameters';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例id（对应 instances.workflow_instance_id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'workflow_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'参数名（如 days / reason / product_name）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'param_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'参数值（null = 显式置空）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'param_value';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'create_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
    @level2type = N'COLUMN', @level2name = N'modify_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门信息表',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'sys_dept';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门名称',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'dept_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'上级部门ID（NULL=顶级）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'parent_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'排序号',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'sort_order';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
    @level2type = N'COLUMN', @level2name = N'create_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工信息表',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'sys_emp';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工姓名',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'emp_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'域账号（审批人/申请人在流程里的用户ID）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'domain_account';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'所属部门ID（sys_dept.dept_id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'dept_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
    @level2type = N'COLUMN', @level2name = N'create_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'用户组表（审批组）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'sys_emp_group';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'组标识（流程定义 candidateGroups 引用的就是它）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
    @level2type = N'COLUMN', @level2name = N'group_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'组名称（如：组长 / 经理）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
    @level2type = N'COLUMN', @level2name = N'group_name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'排序号',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
    @level2type = N'COLUMN', @level2name = N'sort_order';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
    @level2type = N'COLUMN', @level2name = N'create_time';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'用户组成员表',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'sys_emp_group_member';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'成员行ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
    @level2type = N'COLUMN', @level2name = N'member_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'组标识（sys_emp_group.group_id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
    @level2type = N'COLUMN', @level2name = N'group_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工ID（sys_emp.emp_id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
    @level2type = N'COLUMN', @level2name = N'emp_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
    @level2type = N'COLUMN', @level2name = N'create_time';

go
