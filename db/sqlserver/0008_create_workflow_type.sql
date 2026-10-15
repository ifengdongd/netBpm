-- ============================================================================
--  NetBpm · SQL Server 业务层建表脚本：流程类型表（1 张）
-- ============================================================================
--  来源：**本项目新增** —— 流程信息枚举（ProcessInfoEnum）的数据源：
--        流程的 Id（类型 id）/ Name（流程 key）/ Describe（描述）/
--        ViewUrl（业务单查看页）/ ApproveUrl（审批页）配置在表里，
--        系统启动时加载至 ProcessInfoEnum，新增流程 = 表里插一行。
--
--  与其它表的关系：
--    workflow_type.id = wf.instances.workflow_type_id（实例登记记录的类型 id）
--
--  ⚠️ name 必须与 BPMN 里 process/@id（流程 key）一致；id 建议用设计器 modelId。
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007 → 0008
-- ============================================================================

create table wf.workflow_type (
    tenant_id       bigint         not null,
    id              nvarchar(64)   not null,
    name            nvarchar(50)   not null,
    [describe]      nvarchar(200)  not null,
    view_url        nvarchar(500)  not null,
    approve_url     nvarchar(500)  not null,
    sort_order      int            not null,
    enabled         tinyint        not null,
    create_time     datetime       not null,
    modify_time     datetime       null,
    constraint pk_workflow_type primary key (id)
);
go

if not exists (select * from sys.indexes where name = N'ix_workflow_type_sort')
    create index ix_workflow_type_sort on wf.workflow_type (tenant_id, sort_order);
go

-- 种子数据：订单审批 / 请假审批两条流程
insert into wf.workflow_type
    (tenant_id, id, name, [describe], view_url, approve_url, sort_order, enabled, create_time)
values
    (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', N'订单审批流程',
     '/bill.html?process=order_process&businessId={businessId}',
     '/approve.html?process=order_process&taskId={taskId}', 1, 1, getutcdate()),
    (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', N'请假申请流程',
     '/bill.html?process=leave_process&businessId={businessId}',
     '/approve.html?process=leave_process&taskId={taskId}', 2, 1, getutcdate());
go
