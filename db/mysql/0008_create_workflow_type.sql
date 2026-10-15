-- ============================================================================
--  NetBpm · MySQL 业务层建表脚本：流程类型表（1 张）
-- ============================================================================
--  来源：**本项目新增** —— 流程信息枚举（ProcessInfoEnum）的数据源：
--        流程的 Id（类型 id）/ Name（流程 key）/ Describe（描述）/
--        ViewUrl（业务单查看页）/ ApproveUrl（审批页）配置在表里，
--        系统启动时加载至 ProcessInfoEnum，新增流程 = 表里插一行。
--
--  与其它表的关系：
--    workflow_type.id = instances.workflow_type_id（实例登记记录的类型 id）
--
--  ⚠️ name 必须与 BPMN 里 process/@id（流程 key）一致；id 建议用设计器 modelId。
--  ⚠️ view_url / approve_url 里的 {businessId} / {taskId} / {processInstanceId}
--     是占位符，由前端按流程信息枚举渲染。
--
--  执行顺序：0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007 → 0008
-- ============================================================================

create table workflow_type (
    tenant_id       bigint        not null comment '多租户id',
    id              varchar(64)   not null comment '流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
    name            varchar(50)   not null comment '流程名称（流程 key，= BPMN process/@id，如 order_process）',
    `describe`      varchar(200)  not null comment '流程描述',
    view_url        varchar(500)  not null comment '业务单查看页地址（{businessId}/{processInstanceId} 占位符）',
    approve_url     varchar(500)  not null comment '流程审批页地址（{taskId}/{processInstanceId} 占位符）',
    sort_order      int           not null default 0 comment '展示顺序（小在前）',
    enabled         tinyint       not null default 1 comment '是否启用（0=停用，加载时跳过）',
    create_time     datetime      not null comment '创建时间',
    modify_time     datetime      null comment '最后修改时间',
    primary key (id),
    index ix_workflow_type_sort (tenant_id, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin
  COMMENT='流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）';

-- 种子数据：订单审批 / 请假审批两条流程
insert into workflow_type
    (tenant_id, id, name, `describe`, view_url, approve_url, sort_order, enabled, create_time)
values
    (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', '订单审批流程',
     '/bill.html?process=order_process&businessId={businessId}',
     '/approve.html?process=order_process&taskId={taskId}', 1, 1, utc_timestamp()),
    (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', '请假申请流程',
     '/bill.html?process=leave_process&businessId={businessId}',
     '/approve.html?process=leave_process&taskId={taskId}', 2, 1, utc_timestamp());
