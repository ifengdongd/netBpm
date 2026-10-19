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
    tenant_id bigint not null comment '多租户id',
    business_id bigint not null comment '业务id（请假单号，由业务侧生成）',
    apply_user varchar(50) not null comment '申请人',
    apply_dept_id varchar(50) not null comment '申请部门',
    business_dept_id varchar(50) comment '业务部门',
    days smallint comment '请假天数（对应流程参数 days）；NULL = 还没填',
    reason varchar(500) comment '请假事由（对应流程参数 reason）',
    ext_params varchar(2000) comment '扩展流程参数（JSON 对象）：除 days / reason 外的键值对',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (business_id),
    index ix_leave_bill_apply_user (tenant_id, apply_user)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';

create table order_bill (
    tenant_id bigint not null comment '多租户id',
    business_id bigint not null comment '业务id（订单单号，由业务侧生成）',
    product_name varchar(200) comment '订购产品（对应流程参数 product_name）',
    amount decimal(18,2) comment '订单金额（对应流程参数 amount）',
    remark varchar(500) comment '备注（对应流程参数 remark）',
    status smallint not null default 1 comment '状态：1=审批中，-1=审批拒绝，100=审批通过',
    approve_comment varchar(500) comment '最终审批意见（审批结束时随状态一起写回）',
    apply_user varchar(50) not null comment '申请人',
    apply_dept_id varchar(50) not null comment '申请部门',
    business_dept_id varchar(50) comment '业务部门',
    process_instance_id varchar(64) comment '流程实例 id（提交申请后回填，用于反查审批进度）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    deleted_flag tinyint not null default 0 comment '伪删除标志（0 正常 / 1 已删除；公共数据操作接口 DeleteAsync 置位，列表与明细自动过滤）',
    primary key (business_id),
    index ix_order_bill_apply_user (tenant_id, apply_user),
    index ix_order_bill_status (tenant_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）';

create table workflow_parameters (
    tenant_id bigint not null comment '多租户id',
    workflow_instance_id varchar(64) not null comment '流程实例id（对应 instances.workflow_instance_id）',
    param_name varchar(100) not null comment '参数名（如 days / reason / product_name）',
    param_value text comment '参数值（null = 显式置空）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (workflow_instance_id, param_name),
    index ix_workflow_parameters_instance (workflow_instance_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）';


-- ─────────────────────────── 组织架构表（sys_*，幂等可重复执行） ───────────────────────────

create table if not exists sys_dept (
    dept_id bigint not null comment '部门ID',
    tenant_id bigint not null comment '多租户ID',
    dept_name varchar(100) not null comment '部门名称',
    parent_id bigint comment '上级部门ID（NULL=顶级）',
    sort_order int not null default 0 comment '排序号',
    create_time datetime not null comment '创建时间',
    primary key (dept_id),
    index idx_sys_dept_tenant (tenant_id, parent_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='部门信息表';

create table if not exists sys_emp (
    emp_id bigint not null comment '员工ID',
    tenant_id bigint not null comment '多租户ID',
    emp_name varchar(50) not null comment '员工姓名',
    domain_account varchar(50) not null comment '域账号（审批人/申请人在流程里的用户ID）',
    dept_id bigint not null comment '所属部门ID（sys_dept.dept_id）',
    create_time datetime not null comment '创建时间',
    primary key (emp_id),
    unique key uk_sys_emp_account (tenant_id, domain_account),
    index idx_sys_emp_dept (dept_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='员工信息表';

create table if not exists sys_emp_group (
    group_id varchar(50) not null comment '组标识（流程定义 candidateGroups 引用的就是它）',
    tenant_id bigint not null comment '多租户ID',
    group_name varchar(100) not null comment '组名称（如：组长 / 经理）',
    sort_order int not null default 0 comment '排序号',
    create_time datetime not null comment '创建时间',
    primary key (group_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='用户组表（审批组）';

create table if not exists sys_emp_group_member (
    member_id bigint not null comment '成员行ID',
    tenant_id bigint not null comment '多租户ID',
    group_id varchar(50) not null comment '组标识（sys_emp_group.group_id）',
    emp_id bigint not null comment '员工ID（sys_emp.emp_id）',
    create_time datetime not null comment '创建时间',
    primary key (member_id),
    unique key uk_sys_emp_group_member (group_id, emp_id),
    index idx_sys_emp_group_member_emp (emp_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='用户组成员表';


-- ─────────────────────────── 种子数据（演示用，可重复执行） ───────────────────────────

insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
values (101, 1, '信息技术部', null, 1, utc_timestamp()),
       (102, 1, '成本管理部', null, 2, utc_timestamp()),
       (103, 1, '综合管理部', null, 3, utc_timestamp()) on duplicate key update dept_name = values(dept_name), parent_id = values(parent_id), sort_order = values(sort_order);

insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
values (1001, 1, '王小爱', 'alice', 101, utc_timestamp()),
       (1002, 1, '李小波', 'bob', 101, utc_timestamp()),
       (1003, 1, '赵志强', 'leader1', 101, utc_timestamp()),
       (1004, 1, '钱有为', 'leader2', 102, utc_timestamp()),
       (1005, 1, '孙建国', 'manager1', 103, utc_timestamp()) on duplicate key update emp_name = values(emp_name), dept_id = values(dept_id);

insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
values ('leaders', 1, '组长', 1, utc_timestamp()),
       ('managers', 1, '经理', 2, utc_timestamp()) on duplicate key update group_name = values(group_name), sort_order = values(sort_order);

insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
values (1, 1, 'leaders', 1003, utc_timestamp()),
       (2, 1, 'leaders', 1004, utc_timestamp()),
       (3, 1, 'managers', 1005, utc_timestamp()) on duplicate key update create_time = values(create_time);

