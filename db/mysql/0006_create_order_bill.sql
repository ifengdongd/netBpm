-- ============================================================================
--  NetBpm · MySQL 业务层建表脚本：订单表（1 张）
-- ============================================================================
--  来源：**本项目新增** —— 与 0004 的 leave_bill 同一套约定：
--        0003 的三张表（instances / instance_current_task / deal_history）是「流程」表，
--        订单这类业务单据立在业务表里，instances.business_id 正是指向它。
--
--  与 0003 三张表的关系：
--    instances.business_id  → order_bill.business_id          （一张订单对应一次审批流程）
--    order_bill.product_name ← 流程参数 product_name（订购产品）
--    order_bill.amount       ← 流程参数 amount（订单金额）
--    order_bill.remark       ← 流程参数 remark（备注）
--
--  状态约定（status 列，订单审批 Demo 的核心字段）：
--      1    审批中   —— 提交申请（发起流程）时写入
--     -1    审批拒绝 —— 流程走到「拒绝结束」服务任务（order.RefuseComplete）时写回
--    100    审批通过 —— 流程走到「审批通过」服务任务（order.ApproveComplete）时写回
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006
-- ============================================================================

create table order_bill (
    tenant_id          bigint         not null comment '多租户id',
    business_id        bigint         not null comment '业务id（订单单号，由业务侧生成）',
    product_name       varchar(200)   null comment '订购产品（对应流程参数 product_name）',
    amount             decimal(18,2)  null comment '订单金额（对应流程参数 amount）',
    remark             varchar(500)   null comment '备注（对应流程参数 remark）',
    status             smallint       not null default 1 comment '状态：1=审批中，-1=审批拒绝，100=审批通过',
    approve_comment    varchar(500)   null comment '最终审批意见（审批结束时随状态一起写回）',
    apply_user         varchar(50)    not null comment '申请人',
    apply_dept_id      varchar(50)    not null comment '申请部门',
    business_dept_id   varchar(50)    null comment '业务部门',
    process_instance_id varchar(64)   null comment '流程实例 id（提交申请后回填，用于反查审批进度）',
    create_time        datetime       not null comment '创建时间',
    modify_time        datetime       null comment '最后修改时间',
    primary key (business_id),
    index ix_order_bill_apply_user (tenant_id, apply_user),
    index ix_order_bill_status (tenant_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）';
