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

--  管理治理（审计 + 历史归档）。
--
--  admin_audit_log：转办 / 跳转 / 变量直写等高风险管理动作的审计留痕
--    （操作人、租户、动作、目标、结果、时间；不记录变量值）。
--  admin_history_archive：已结束实例的 ACT_HI_* 行 JSON 快照；
--    归档后原历史行在同一事务内删除，使 ACT_HI_* 保持轻量。
--
--  注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0009_create_admin_governance.sql
--   3. 本脚本幂等，可重复执行。

create table if not exists admin_audit_log (
    id varchar(64) not null comment '审计记录 id',
    tenant_id varchar(255) comment '租户（与 ACT_* 的 TENANT_ID_ 同口径）',
    actor varchar(255) comment '操作人（JWT sub / X-User-Id）',
    auth_mode varchar(16) not null comment '鉴权方式：jwt / api_key / none',
    action varchar(64) not null comment '动作标识，如 task.reassign / instance.jump / variable.write',
    target_type varchar(32) not null comment '目标类型：task / process_instance / variable / history',
    target_id varchar(128) not null comment '目标 id',
    process_instance_id varchar(64) comment '关联流程实例 id（便于按实例追溯）',
    detail mediumtext comment '动作明细（JSON；不记录变量值）',
    success int not null comment '1=成功，0=失败',
    error varchar(500) comment '失败原因',
    created_at datetime(3) not null comment '写入时间（UTC）',
    primary key (id),
    index ix_admin_audit_tenant_time (tenant_id, created_at),
    index ix_admin_audit_actor (tenant_id, actor, created_at),
    index ix_admin_audit_action (action),
    index ix_admin_audit_target (target_type, target_id),
    index ix_admin_audit_instance (process_instance_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='管理动作审计：转办 / 跳转 / 变量直写等高风险动作的操作人、目标与结果';

create table if not exists admin_history_archive (
    id varchar(64) not null comment '归档行 id',
    process_instance_id varchar(64) not null comment '流程实例 id',
    tenant_id varchar(255) comment '租户',
    process_definition_id varchar(64) comment '流程定义 id（启动时版本）',
    business_key varchar(255) comment '业务主键',
    start_user_id varchar(255) comment '发起人',
    start_time datetime(3) comment '实例开始时间',
    end_time datetime(3) comment '实例结束时间',
    archived_at datetime(3) not null comment '归档时间（UTC）',
    archived_by varchar(255) comment '归档操作人',
    payload mediumtext not null comment '历史行快照（processInstance / activities / tasks / variables / identityLinks）',
    primary key (id),
    unique key ux_admin_history_archive_instance (process_instance_id),
    index ix_admin_history_archive_tenant (tenant_id, end_time),
    index ix_admin_history_archive_business (business_key),
    index ix_admin_history_archive_archived (archived_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='历史归档：已结束实例的 ACT_HI_* 行快照（JSON），原历史行在归档后删除';

