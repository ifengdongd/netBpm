-- ============================================================================
--  NetBpm · SQL Server 业务层建表脚本：请假单（1 张）
-- ============================================================================
--  来源：**本项目新增**（不是来源库的存量表）—— 0003 的三张表是「流程」表，
--        而 days / reason 这类流程参数在真实系统里属于**业务表单**的列，
--        instances.business_id 正是指向它。这里补上这张业务单表，
--        让「流程参数表」在数据库里有个真实的落点。
--
--  与 0003 三张表的关系：
--    instances.business_id  → leave_bill.business_id          （一个业务单可多次申请）
--    leave_bill.days        ← 流程参数 days（请假天数）
--    leave_bill.reason      ← 流程参数 reason（请假事由）
--    leave_bill.ext_params  ← 流程参数里除 days / reason 外的其余键值对（JSON）
--
--  ⚠️ days / reason 立成真列而不是塞进 ext_params：它们本来就是请假单的列，
--     存成字典反而没法按天数建索引、也没法写业务查询。
--
--  执行顺序：0001 → 0002 → 0003 → 0004
-- ============================================================================

create table leave_bill (
    tenant_id          bigint         not null,
    business_id        bigint         not null,
    apply_user         nvarchar(50)   not null,
    apply_dept_id      nvarchar(50)   not null,
    business_dept_id   nvarchar(50)   null,
    days               smallint       null,
    reason             nvarchar(500)  null,
    ext_params         nvarchar(2000) null,
    create_time        datetime       not null,
    modify_time        datetime       null,
    constraint pk_leave_bill primary key (business_id)
);
go

if not exists (select * from sys.indexes where name = N'ix_leave_bill_apply_user')
    create index ix_leave_bill_apply_user on leave_bill (tenant_id, apply_user);
go
