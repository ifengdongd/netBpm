-- ============================================================================
-- 0005 · 组织架构表（Demo 的「sample」人员与部门信息）—— SQL Server 版
-- ============================================================================
-- 与 db/mysql/0005_create_org_tables.sql 同构，幂等可重复执行。
-- 与 0003 一致：业务表统一落 dbo（与引擎 ACT_* 同 schema），主键由业务侧生成 bigint。
-- ============================================================================

if object_id('sys_dept', 'U') is null
    create table sys_dept
    (
        dept_id     bigint        not null,
        tenant_id   bigint        not null,
        dept_name   nvarchar(100) not null,
        parent_id   bigint        null,
        sort_order  int           not null constraint df_sys_dept_sort default (0),
        create_time datetime      not null,
        constraint pk_sys_dept primary key clustered (dept_id)
    );
go

if object_id('sys_emp', 'U') is null
    create table sys_emp
    (
        emp_id         bigint       not null,
        tenant_id      bigint       not null,
        emp_name       nvarchar(50) not null,
        domain_account nvarchar(50) not null,
        dept_id        bigint       not null,
        create_time    datetime     not null,
        constraint pk_sys_emp primary key clustered (emp_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_account' and object_id = object_id('sys_emp'))
    create unique index uk_sys_emp_account on sys_emp (tenant_id, domain_account);
go

if object_id('sys_emp_group', 'U') is null
    create table sys_emp_group
    (
        group_id    nvarchar(50)   not null,
        tenant_id   bigint         not null,
        group_name  nvarchar(100)  not null,
        sort_order  int            not null constraint df_sys_emp_group_sort default (0),
        create_time datetime       not null,
        constraint pk_sys_emp_group primary key clustered (group_id)
    );
go

if object_id('sys_emp_group_member', 'U') is null
    create table sys_emp_group_member
    (
        member_id   bigint       not null,
        tenant_id   bigint       not null,
        group_id    nvarchar(50) not null,
        emp_id      bigint       not null,
        create_time datetime     not null,
        constraint pk_sys_emp_group_member primary key clustered (member_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_group_member' and object_id = object_id('sys_emp_group_member'))
    create unique index uk_sys_emp_group_member on sys_emp_group_member (group_id, emp_id);
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
