-- ============================================================================
--  NetBpm · SQL Server 业务层建表脚本：订单表（1 张）
-- ============================================================================
--  与 db/mysql/0006_create_order_bill.sql 同一套约定，方言换成 SQL Server。
--
--  状态约定（status 列，订单审批 Demo 的核心字段）：
--      1    审批中   —— 提交申请（发起流程）时写入
--     -1    审批拒绝 —— 流程走到「拒绝结束」服务任务（order.RefuseComplete）时写回
--    100    审批通过 —— 流程走到「审批通过」服务任务（order.ApproveComplete）时写回
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006
-- ============================================================================

create table order_bill (
    tenant_id           bigint          not null,
    business_id         bigint          not null,
    product_name        nvarchar(200)   null,
    amount              decimal(18,2)   null,
    remark              nvarchar(500)   null,
    status              smallint        not null default 1,
    approve_comment     nvarchar(500)   null,
    apply_user          nvarchar(50)    not null,
    apply_dept_id       nvarchar(50)    not null,
    business_dept_id    nvarchar(50)    null,
    process_instance_id nvarchar(64)    null,
    create_time         datetime        not null,
    modify_time         datetime        null,
    deleted_flag        tinyint         not null constraint df_order_bill_deleted default (0),
    constraint pk_order_bill primary key (business_id)
);
go

if not exists (select * from sys.indexes where name = N'ix_order_bill_apply_user')
    create index ix_order_bill_apply_user on order_bill (tenant_id, apply_user);
go

if not exists (select * from sys.indexes where name = N'ix_order_bill_status')
    create index ix_order_bill_status on order_bill (tenant_id, status);
go
