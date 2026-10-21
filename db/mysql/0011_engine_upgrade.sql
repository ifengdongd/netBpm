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

--  增量升级脚本（0011）：补齐老库缺失的结构。
--
--  背景：此前 migrate 判定「库已初始化」后一条脚本都不执行，导致用 0001 建过的库
--        永远拿不到 0004 索引、0006 定时作业表与后续扩展表。现在由 ACT_GE_SCHEMA_LOG
--        记录已执行脚本，migrate 会按文件名补跑未执行的脚本。
--
--  本脚本幂等：MySQL 用 information_schema 判重后动态执行；
--  内容：执行日志表、模型大文本列扩容、定时作业重试/死信列、死信查询索引。


-- ─────────────────────────── 脚本执行日志（增量迁移依据） ───────────────────────────

create table if not exists ACT_GE_SCHEMA_LOG (
    SCRIPT_ varchar(255) not null comment '脚本文件名（如 0004_perf_indexes.sql）',
    CHECKSUM_ varchar(64) comment '脚本内容的 SHA-256（十六进制）',
    APPLIED_AT_ datetime(3) not null comment '执行时间（UTC）',
    primary key (SCRIPT_),
    index ACT_IDX_SCHEMA_LOG_APPLIED (APPLIED_AT_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='数据库脚本执行日志：已应用脚本名 + 校验和（增量迁移的唯一依据）';


-- ─────────────────────────── 模型大文本列扩容（MySQL text 64KB → mediumtext 16MB） ───────────────────────────

alter table bpmn_model modify column bpmn_xml mediumtext not null;
alter table bpmn_model modify column svg mediumtext;
alter table bpmn_model_history modify column bpmn_xml mediumtext not null;
alter table bpmn_model_history modify column svg mediumtext;

-- ─────────────────────────── 定时作业可靠性列（重试 / 死信 / 失败原因） ───────────────────────────

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column RETRIES_ int default 3',
        'select 1')
    from information_schema.columns
    where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB'
      and column_name = 'RETRIES_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column DEAD_LETTER_ tinyint default 0',
        'select 1')
    from information_schema.columns
    where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB'
      and column_name = 'DEAD_LETTER_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column EXCEPTION_MSG_ varchar(1000)',
        'select 1')
    from information_schema.columns
    where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB'
      and column_name = 'EXCEPTION_MSG_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column LAST_FAILURE_ datetime(3)',
        'select 1')
    from information_schema.columns
    where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB'
      and column_name = 'LAST_FAILURE_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── 定时作业死信查询索引 ───────────────────────────

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TIMER_JOB_DEADLINE on ACT_RU_TIMER_JOB (DEAD_LETTER_, DUEDATE_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB'
      and index_name = 'ACT_IDX_TIMER_JOB_DEADLINE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- ─────────────────────────── 流程实例迁移审计查询索引 ───────────────────────────

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_MIGRATION_PROCINST_TIME on ACT_HI_MIGRATION (PROC_INST_ID_, MIGRATED_AT_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_HI_MIGRATION'
      and index_name = 'ACT_IDX_MIGRATION_PROCINST_TIME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;
