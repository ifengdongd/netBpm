-- ============================================================================
--  NetBpm · SQL Server 数据库脚本（自动生成，请勿手改）
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
--      NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0009_create_admin_governance.sql
--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。

if object_id('admin_audit_log', 'U') is null
    create table admin_audit_log (
    id nvarchar(64) not null,
    tenant_id nvarchar(255),
    actor nvarchar(255),
    auth_mode nvarchar(16) not null,
    action nvarchar(64) not null,
    target_type nvarchar(32) not null,
    target_id nvarchar(128) not null,
    process_instance_id nvarchar(64),
    detail nvarchar(max),
    success int not null,
    error nvarchar(500),
    created_at datetime not null,
    constraint pk_admin_audit_log primary key clustered (id)
    )
go

if not exists (select 1 from sys.indexes where name = 'ix_admin_audit_tenant_time' and object_id = object_id('admin_audit_log'))
create index ix_admin_audit_tenant_time on admin_audit_log (tenant_id, created_at);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_audit_actor' and object_id = object_id('admin_audit_log'))
create index ix_admin_audit_actor on admin_audit_log (tenant_id, actor, created_at);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_audit_action' and object_id = object_id('admin_audit_log'))
create index ix_admin_audit_action on admin_audit_log (action);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_audit_target' and object_id = object_id('admin_audit_log'))
create index ix_admin_audit_target on admin_audit_log (target_type, target_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_audit_instance' and object_id = object_id('admin_audit_log'))
create index ix_admin_audit_instance on admin_audit_log (process_instance_id);
go
if object_id('admin_history_archive', 'U') is null
    create table admin_history_archive (
    id nvarchar(64) not null,
    process_instance_id nvarchar(64) not null,
    tenant_id nvarchar(255),
    process_definition_id nvarchar(64),
    business_key nvarchar(255),
    start_user_id nvarchar(255),
    start_time datetime,
    end_time datetime,
    archived_at datetime not null,
    archived_by nvarchar(255),
    payload nvarchar(max) not null,
    constraint pk_admin_history_archive primary key clustered (id)
    )
go

if not exists (select 1 from sys.indexes where name = 'ux_admin_history_archive_instance' and object_id = object_id('admin_history_archive'))
create unique index ux_admin_history_archive_instance on admin_history_archive (process_instance_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_history_archive_tenant' and object_id = object_id('admin_history_archive'))
create index ix_admin_history_archive_tenant on admin_history_archive (tenant_id, end_time);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_history_archive_business' and object_id = object_id('admin_history_archive'))
create index ix_admin_history_archive_business on admin_history_archive (business_key);
go
if not exists (select 1 from sys.indexes where name = 'ix_admin_history_archive_archived' and object_id = object_id('admin_history_archive'))
create index ix_admin_history_archive_archived on admin_history_archive (archived_at);
go

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'管理动作审计：转办 / 跳转 / 变量直写等高风险动作的操作人、目标与结果',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'admin_audit_log';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'审计记录 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户（与 ACT_* 的 TENANT_ID_ 同口径）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'操作人（JWT sub / X-User-Id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'actor';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'鉴权方式：jwt / api_key / none',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'auth_mode';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作标识，如 task.reassign / instance.jump / variable.write',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'action';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'目标类型：task / process_instance / variable / history',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'target_type';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'目标 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'target_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'关联流程实例 id（便于按实例追溯）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'process_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作明细（JSON；不记录变量值）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'detail';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=成功，0=失败',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'success';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'失败原因',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'error';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'写入时间（UTC）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
    @level2type = N'COLUMN', @level2name = N'created_at';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史归档：已结束实例的 ACT_HI_* 行快照（JSON），原历史行在归档后删除',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'admin_history_archive';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档行 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'process_instance_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义 id（启动时版本）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'process_definition_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务主键',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'business_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'发起人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'start_user_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'实例开始时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'start_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'实例结束时间',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'end_time';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档时间（UTC）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'archived_at';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档操作人',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'archived_by';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史行快照（processInstance / activities / tasks / variables / identityLinks）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
    @level2type = N'COLUMN', @level2name = N'payload';

go
