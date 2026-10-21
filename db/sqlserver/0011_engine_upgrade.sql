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

--  增量升级脚本（0011）：补齐老库缺失的结构。
--
--  幂等：object_id / sys.columns / sys.indexes 判重，可重复执行。
--  内容：执行日志表、定时作业重试/死信列、死信查询索引。
--        （bpmn_xml / svg 在 SQL Server 已是 nvarchar(max)，无需扩容。）


-- ─────────────────────────── 脚本执行日志（增量迁移依据） ───────────────────────────

if object_id('ACT_GE_SCHEMA_LOG', 'U') is null
    create table ACT_GE_SCHEMA_LOG (
    SCRIPT_ nvarchar(255) not null,
    CHECKSUM_ nvarchar(64),
    APPLIED_AT_ datetime not null,
    primary key (SCRIPT_)
    )
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_SCHEMA_LOG_APPLIED' and object_id = object_id('ACT_GE_SCHEMA_LOG'))
create index ACT_IDX_SCHEMA_LOG_APPLIED on ACT_GE_SCHEMA_LOG (APPLIED_AT_);
go

-- ─────────────────────────── 模型大文本列扩容（MySQL text 64KB → mediumtext 16MB） ───────────────────────────

-- SQL Server 的 bpmn_xml / svg 已是 nvarchar(max)，无需扩容。

-- ─────────────────────────── 定时作业可靠性列（重试 / 死信 / 失败原因） ───────────────────────────

if not exists (select 1 from sys.columns where object_id = object_id('ACT_RU_TIMER_JOB') and name = 'RETRIES_')
    alter table ACT_RU_TIMER_JOB add RETRIES_ int default 3;
go

if not exists (select 1 from sys.columns where object_id = object_id('ACT_RU_TIMER_JOB') and name = 'DEAD_LETTER_')
    alter table ACT_RU_TIMER_JOB add DEAD_LETTER_ tinyint default 0;
go

if not exists (select 1 from sys.columns where object_id = object_id('ACT_RU_TIMER_JOB') and name = 'EXCEPTION_MSG_')
    alter table ACT_RU_TIMER_JOB add EXCEPTION_MSG_ nvarchar(1000);
go

if not exists (select 1 from sys.columns where object_id = object_id('ACT_RU_TIMER_JOB') and name = 'LAST_FAILURE_')
    alter table ACT_RU_TIMER_JOB add LAST_FAILURE_ datetime;
go


-- ─────────────────────────── 定时作业死信查询索引 ───────────────────────────

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TIMER_JOB_DEADLINE' and object_id = object_id('ACT_RU_TIMER_JOB'))
create index ACT_IDX_TIMER_JOB_DEADLINE on ACT_RU_TIMER_JOB (DEAD_LETTER_, DUEDATE_);
go

-- ─────────────────────────── 流程实例迁移审计查询索引 ───────────────────────────

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_MIGRATION_PROCINST_TIME' and object_id = object_id('ACT_HI_MIGRATION'))
create index ACT_IDX_MIGRATION_PROCINST_TIME on ACT_HI_MIGRATION (PROC_INST_ID_, MIGRATED_AT_);
go
