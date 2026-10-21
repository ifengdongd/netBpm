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

--  定时作业表（边界定时器）。
--
--  背景：引擎支持「用户任务 + 定时边界事件」，任务创建时写一条作业，到点由宿主轮询触发：
--        取消任务 → 沿边界事件出流继续。
--
--  注意：
--   1. 本表是 Activiti ACT_RU_TIMER_JOB 的裁剪版（只保留一次性定时所需列）；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0006_create_timer_job.sql
--   3. 本脚本幂等，可重复执行。

create table if not exists ACT_RU_TIMER_JOB (
    ID_ varchar(64) not null,
    REV_ int default 0,
    TYPE_ varchar(255) not null,
    EXECUTION_ID_ varchar(64) not null,
    PROCESS_INSTANCE_ID_ varchar(64) not null,
    PROC_DEF_ID_ varchar(64),
    ACTIVITY_ID_ varchar(255),
    BOUNDARY_EVENT_ID_ varchar(255),
    DUEDATE_ datetime(3) not null,
    TENANT_ID_ varchar(255) default '',
    CREATE_TIME_ datetime(3) not null,
    RETRIES_ int default 3 comment '剩余重试次数；0 表示已进死信',
    DEAD_LETTER_ tinyint default 0 comment '1=已进死信，不再参与到期轮询',
    EXCEPTION_MSG_ varchar(1000) comment '最后一次失败原因',
    LAST_FAILURE_ datetime(3) comment '最后一次失败时间（UTC）',
    primary key (ID_),
    index ACT_IDX_TIMER_JOB_DUEDATE (DUEDATE_),
    index ACT_IDX_TIMER_JOB_EXECUTION (EXECUTION_ID_),
    index ACT_IDX_TIMER_JOB_PROCINST (PROCESS_INSTANCE_ID_),
    index ACT_IDX_TIMER_JOB_DEADLINE (DEAD_LETTER_, DUEDATE_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='定时作业表：边界定时器（一次性）到点后触发';

