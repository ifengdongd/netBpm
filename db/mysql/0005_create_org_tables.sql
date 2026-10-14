-- ============================================================================
-- 0005 · 组织架构表（Demo 的「sample」人员与部门信息）
-- ============================================================================
-- 请假审批 Demo 的「真实环境」四件套中，前三件（流程实例、当前待办、审批流水）
-- 由 0003 覆盖；本脚本补最后一块 —— 组织架构：
--
--   sys_dept             部门（instances.apply_dept_id / business_dept_id 指向 dept_id）
--   sys_emp              员工（instances.apply_emp_id 存的是 domain_account，唯一键）
--   sys_emp_group        用户组（对应流程定义里的候选组 leaders / managers）
--   sys_emp_group_member 组成员（「流程走到组长审批时审批人是谁」由它 + 组展开得出）
--
-- 幂等性：建表用 create table if not exists，种子用 insert ... on duplicate key
-- update —— 可重复执行（配合 NetBpm.DbMigrate 的 apply 命令按文件执行）。
--
-- 与引擎表的关系：
--   流程定义里 activiti:candidateGroups 写的是 sys_emp_group.group_id；
--   IUserGroupManager（Demo 的 MySqlOrgDirectory）在两者之间做展开：
--     FindUserIdsByGroupAsync(组)  → 成员的 domain_account（候选审批人）
--     FindGroupIdsByUserAsync(账号) → 所属组（按候选人查待办）
-- ============================================================================

create table if not exists sys_dept
(
    dept_id     bigint       not null comment '部门ID',
    tenant_id   bigint       not null comment '多租户ID',
    dept_name   varchar(100) not null comment '部门名称',
    parent_id   bigint       null comment '上级部门ID（NULL=顶级）',
    sort_order  int          not null default 0 comment '排序号',
    create_time datetime     not null comment '创建时间',
    primary key (dept_id),
    key idx_sys_dept_tenant (tenant_id, parent_id)
) engine = InnoDB
  default charset = utf8mb4 comment = '部门信息表';

create table if not exists sys_emp
(
    emp_id         bigint      not null comment '员工ID',
    tenant_id      bigint      not null comment '多租户ID',
    emp_name       varchar(50) not null comment '员工姓名',
    domain_account varchar(50) not null comment '域账号（审批人/申请人在流程里的用户ID）',
    dept_id        bigint      not null comment '所属部门ID（sys_dept.dept_id）',
    create_time    datetime    not null comment '创建时间',
    primary key (emp_id),
    unique key uk_sys_emp_account (tenant_id, domain_account),
    key idx_sys_emp_dept (dept_id)
) engine = InnoDB
  default charset = utf8mb4 comment = '员工信息表';

create table if not exists sys_emp_group
(
    group_id    varchar(50)  not null comment '组标识（流程定义 candidateGroups 引用的就是它）',
    tenant_id   bigint       not null comment '多租户ID',
    group_name  varchar(100) not null comment '组名称（如：组长 / 经理）',
    sort_order  int          not null default 0 comment '排序号',
    create_time datetime     not null comment '创建时间',
    primary key (group_id)
) engine = InnoDB
  default charset = utf8mb4 comment = '用户组表（审批组）';

create table if not exists sys_emp_group_member
(
    member_id   bigint      not null comment '成员行ID',
    tenant_id   bigint      not null comment '多租户ID',
    group_id    varchar(50) not null comment '组标识（sys_emp_group.group_id）',
    emp_id      bigint      not null comment '员工ID（sys_emp.emp_id）',
    create_time datetime    not null comment '创建时间',
    primary key (member_id),
    unique key uk_sys_emp_group_member (group_id, emp_id),
    key idx_sys_emp_group_member_emp (emp_id)
) engine = InnoDB
  default charset = utf8mb4 comment = '用户组成员表';

-- ─────────────────────────── 种子数据（演示用，可重复执行） ───────────────────────────

insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
values (101, 1, '信息技术部', null, 1, utc_timestamp()),
       (102, 1, '成本管理部', null, 2, utc_timestamp()),
       (103, 1, '综合管理部', null, 3, utc_timestamp())
on duplicate key update dept_name  = values(dept_name),
                        parent_id  = values(parent_id),
                        sort_order = values(sort_order);

insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
values (1001, 1, '王小爱', 'alice',   101, utc_timestamp()),
       (1002, 1, '李小波', 'bob',     101, utc_timestamp()),
       (1003, 1, '赵志强', 'leader1', 101, utc_timestamp()),
       (1004, 1, '钱有为', 'leader2', 102, utc_timestamp()),
       (1005, 1, '孙建国', 'manager1', 103, utc_timestamp())
on duplicate key update emp_name  = values(emp_name),
                        dept_id   = values(dept_id);

insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
values ('leaders', 1, '组长', 1, utc_timestamp()),
       ('managers', 1, '经理', 2, utc_timestamp())
on duplicate key update group_name = values(group_name),
                        sort_order = values(sort_order);

insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
values (1, 1, 'leaders', 1003, utc_timestamp()),
       (2, 1, 'leaders', 1004, utc_timestamp()),
       (3, 1, 'managers', 1005, utc_timestamp())
on duplicate key update create_time = create_time;
