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

--  定时作业表（边界定时器）。
--
--  背景：引擎支持「用户任务 + 定时边界事件」，任务创建时写一条作业，到点由宿主轮询触发：
--        取消任务 → 沿边界事件出流继续。
--
--  注意：
--   1. 本表是 Activiti ACT_RU_TIMER_JOB 的裁剪版（只保留一次性定时所需列）；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0006_create_timer_job.sql
--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。

if object_id('ACT_RU_TIMER_JOB', 'U') is null
    create table ACT_RU_TIMER_JOB (
    ID_ nvarchar(64) not null,
    REV_ int default 0,
    TYPE_ nvarchar(255) not null,
    EXECUTION_ID_ nvarchar(64) not null,
    PROCESS_INSTANCE_ID_ nvarchar(64) not null,
    PROC_DEF_ID_ nvarchar(64),
    ACTIVITY_ID_ nvarchar(255),
    BOUNDARY_EVENT_ID_ nvarchar(255),
    DUEDATE_ datetime not null,
    TENANT_ID_ nvarchar(255) default '',
    CREATE_TIME_ datetime not null,
    RETRIES_ int default 3,
    DEAD_LETTER_ tinyint default 0,
    EXCEPTION_MSG_ nvarchar(1000),
    LAST_FAILURE_ datetime,
    primary key (ID_)
    )
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TIMER_JOB_DUEDATE' and object_id = object_id('ACT_RU_TIMER_JOB'))
create index ACT_IDX_TIMER_JOB_DUEDATE on ACT_RU_TIMER_JOB (DUEDATE_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TIMER_JOB_EXECUTION' and object_id = object_id('ACT_RU_TIMER_JOB'))
create index ACT_IDX_TIMER_JOB_EXECUTION on ACT_RU_TIMER_JOB (EXECUTION_ID_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TIMER_JOB_PROCINST' and object_id = object_id('ACT_RU_TIMER_JOB'))
create index ACT_IDX_TIMER_JOB_PROCINST on ACT_RU_TIMER_JOB (PROCESS_INSTANCE_ID_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TIMER_JOB_DEADLINE' and object_id = object_id('ACT_RU_TIMER_JOB'))
create index ACT_IDX_TIMER_JOB_DEADLINE on ACT_RU_TIMER_JOB (DEAD_LETTER_, DUEDATE_);
go

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'定时作业表：边界定时器（一次性）到点后触发',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'剩余重试次数；0 表示已进死信',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
    @level2type = N'COLUMN', @level2name = N'RETRIES_';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=已进死信，不再参与到期轮询',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
    @level2type = N'COLUMN', @level2name = N'DEAD_LETTER_';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败原因',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
    @level2type = N'COLUMN', @level2name = N'EXCEPTION_MSG_';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败时间（UTC）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
    @level2type = N'COLUMN', @level2name = N'LAST_FAILURE_';

go
