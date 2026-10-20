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

--  流程实例迁移审计表。
--
--  背景：流程定义升级后，运行中的实例可以迁到同 key 的新版本。
--        历史表（ACT_HI_PROCINST）保持实例启动时的定义不变，
--        迁移动作用本表记录（源/目标定义、活动映射、原因、操作人）。
--
--  注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0007_create_migration_log.sql
--   3. 本脚本幂等，可重复执行。

create table if not exists ACT_HI_MIGRATION (
    ID_ varchar(64) not null,
    PROC_INST_ID_ varchar(64) not null,
    SOURCE_PROC_DEF_ID_ varchar(255) not null,
    TARGET_PROC_DEF_ID_ varchar(255) not null,
    ACTIVITY_MAPPINGS_ varchar(4000),
    REASON_ varchar(400),
    USER_ID_ varchar(255),
    MIGRATED_AT_ datetime(3) not null,
    primary key (ID_),
    index ACT_IDX_MIGRATION_PROCINST (PROC_INST_ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='流程实例迁移审计表：记录源/目标定义、活动映射、原因与操作人';

