-- ============================================================================
--  NetBpm · MySQL 业务层建表脚本：请假单（1 张）
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
    tenant_id          bigint        not null comment '多租户id',
    business_id        bigint        not null comment '业务id（请假单号，由业务侧生成）',
    apply_user         varchar(50)   not null comment '申请人',
    apply_dept_id      varchar(50)   not null comment '申请部门',
    business_dept_id   varchar(50)   null comment '业务部门',
    days               smallint      null comment '请假天数（对应流程参数 days）；NULL = 还没填',
    reason             varchar(500)  null comment '请假事由（对应流程参数 reason）',
    ext_params         varchar(2000) null comment '扩展流程参数（JSON 对象）：除 days / reason 外的键值对',
    create_time        datetime      not null comment '创建时间',
    modify_time        datetime      null comment '最后修改时间',
    primary key (business_id),
    index ix_leave_bill_apply_user (tenant_id, apply_user)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';
