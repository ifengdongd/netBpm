-- ============================================================================
--  NetBpm · MySQL 数据库初始化脚本（自动生成，请勿手改）
-- ============================================================================
--  本文件由单一数据源生成，两份方言（mysql / sqlserver）结构严格一一对应，
--  差异仅为方言语法（类型名 / 注释写法 / 批分隔 / 幂等守卫）。
--    · 单一数据源：tools/NetBpm.DbInit/Schema/SchemaCatalog.cs
--    · 方言渲染器：tools/NetBpm.DbInit/Schema/SqlGenerator.cs
--  重新生成：dotnet run --project tools/NetBpm.DbInit -- --generate-only
--  对齐决策清单：见 db/README.md「对齐决策清单」。
-- ============================================================================

--  用途：测试环境 / 生产环境通用的数据库初始化脚本 —— 建库 + 建表 + 索引外键 + 初值数据，
--        **可重复执行**（每一段都带方言幂等守卫）。新库一次建成；老库重复执行只补缺失部分。
--
--  内容：
--    ⓪ 建库 / 选库（仅手工执行时生效）
--    ① 引擎基础表 13 张 ACT_*（Activiti 子集）+ 索引 / 唯一约束 / 外键 + ACT_GE_PROPERTY 初值
--    ② 流程实例业务表（instances / instance_current_task / deal_history）+ 流程类型 + 流程模型
--    ③ Demo 业务表（leave_bill / order_bill / workflow_parameters）+ 组织架构 + 演示种子数据
--    ④ 运行期查询索引（性能增强）
--    ⑤ 应用层扩展表：幂等键 / 定时作业 / 迁移审计 / 模型历史 / 管理审计与归档 / 异步作业 / outbox
--    ⑥ 老库兼容补齐（大文本扩容、定时作业可靠性列）
--
--  执行方式：
--    · 工具（推荐，一步建库 + 建表 + 校验结构版本）：
--        dotnet run --project tools/NetBpm.DbInit -- \
--          --provider mysql --connection "Server=127.0.0.1;Port=3306;Database=netbpm;\
--             User ID=root;Password=;Allow User Variables=true;" --create-database
--    · 手工：MySQL  mysql -u<user> -p < db/mysql/netbpm_init.sql
--             SQL Server  用 sqlcmd / SSMS 连接 master 执行 db/sqlserver/netbpm_init.sql
--    · 宿主启动：NetBpm:AutoMigrate=true 时执行本脚本（库需已存在，或先跑一次上面的工具命令）
--
--  ⚠️ 初值只在缺失时插入：重复执行不会覆盖已有值（尤其不会重置 next.dbid 造成 id 重用）。
--  ⚠️ MySQL 连接串需要 Allow User Variables=true（索引 / 约束的幂等守卫用 @ddl 动态执行）。
--  ⚠️ 表名、列名、索引名、外键名与 Activiti 逐字一致，请勿改名或「顺手优化」列类型。
-- ============================================================================


-- ─────────────────── ⓪ 建库 / 选库（仅手工执行时生效）───────────────────
--
--  库名默认 netbpm：测试环境 / 生产环境用不同库名时，改下面两行即可。
--  工具执行（NetBpm.DbInit / DbMigrate / 宿主 AutoMigrate）会跳过本段：
--  库名取自连接串，首次建库用 DbInit / DbMigrate 的 --create-database。

-- @@bootstrap-begin
create database if not exists `netbpm` character set utf8 collate utf8_bin;
use `netbpm`;
-- @@bootstrap-end

-- ─────────────────────────── ① 引擎基础表（13 张 ACT_*，与 Activiti 逐字一致） ───────────────────────────

--  来源：Activiti 9.0.0
--        activiti-core/activiti-engine/src/main/resources/org/activiti/db/create/
--          activiti.<mysql|mssql>.create.engine.sql
--          activiti.<mysql|mssql>.create.history.sql
--
--  裁剪依据：开发计划 §7.2（25 张表 → 13 张表）
--    保留 · 运行时 8 张：ACT_GE_PROPERTY / ACT_GE_BYTEARRAY / ACT_RE_DEPLOYMENT /
--                        ACT_RE_PROCDEF / ACT_RU_EXECUTION / ACT_RU_TASK /
--                        ACT_RU_IDENTITYLINK / ACT_RU_VARIABLE
--    保留 · 历史   5 张：ACT_HI_PROCINST / ACT_HI_ACTINST / ACT_HI_TASKINST /
--                        ACT_HI_VARINST / ACT_HI_IDENTITYLINK
--    裁剪        12 张：ACT_RE_MODEL / ACT_RU_JOB / ACT_RU_TIMER_JOB /
--                        ACT_RU_SUSPENDED_JOB / ACT_RU_DEADLETTER_JOB /
--                        ACT_RU_EVENT_SUBSCR / ACT_EVT_LOG / ACT_PROCDEF_INFO /
--                        ACT_RU_INTEGRATION / ACT_HI_DETAIL / ACT_HI_COMMENT /
--                        ACT_HI_ATTACHMENT
--    （其中 ACT_RU_TIMER_JOB、ACT_RU_JOB、ACT_EVT_OUTBOX 以引擎扩展表形式在 ⑤ 补回）
--
--  ⚠️ ACT_IDC_EXEC_ROOT 的 IDC 为上游 Activiti 的原样拼写（非笔误），刻意保留。
--  ⚠️ 裁掉 ACT_HI_DETAIL 的前提是历史级别取 AUDIT（Activiti 默认，只记活动/任务/变量实例）。
--
--  引擎初值必须逐字一致（开发计划 §7.3）：
--     · schema.version  —— 引擎启动时校验（DatabaseSchemaChecker）
--     · schema.history  —— 记录建库时的版本
--     · next.dbid       —— DbIdGenerator 的区块游标；区块大小默认 2500

-- ─────────────────────────── 运行时表（8 张）───────────────────────────

create table if not exists ACT_GE_PROPERTY (
    NAME_ varchar(64) not null,
    VALUE_ varchar(300),
    REV_ int,
    primary key (NAME_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='通用属性表：存放 schema 版本、schema 历史与 ID 区块游标等全局键值对';

create table if not exists ACT_GE_BYTEARRAY (
    ID_ varchar(64) not null,
    REV_ int,
    NAME_ varchar(255),
    DEPLOYMENT_ID_ varchar(64),
    BYTES_ LONGBLOB,
    GENERATED_ tinyint,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='二进制数据表：存放部署的流程定义资源（BPMN XML）等字节内容';

create table if not exists ACT_RE_DEPLOYMENT (
    ID_ varchar(64) not null,
    NAME_ varchar(255),
    CATEGORY_ varchar(255),
    KEY_ varchar(255),
    TENANT_ID_ varchar(255) default '',
    DEPLOY_TIME_ timestamp(3),
    ENGINE_VERSION_ varchar(255),
    VERSION_ int default 1,
    PROJECT_RELEASE_VERSION_ varchar(255),
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='部署信息表：每次流程部署产生一条记录';

create table if not exists ACT_RE_PROCDEF (
    ID_ varchar(64) not null,
    REV_ int,
    CATEGORY_ varchar(255),
    NAME_ varchar(255),
    KEY_ varchar(255) not null,
    VERSION_ int not null,
    DEPLOYMENT_ID_ varchar(64),
    RESOURCE_NAME_ varchar(4000),
    DGRM_RESOURCE_NAME_ varchar(4000),
    DESCRIPTION_ varchar(4000),
    HAS_START_FORM_KEY_ tinyint,
    HAS_GRAPHICAL_NOTATION_ tinyint,
    SUSPENSION_STATE_ int,
    TENANT_ID_ varchar(255) default '',
    ENGINE_VERSION_ varchar(255),
    APP_VERSION_ int,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='流程定义表：部署解析后的流程定义（KEY_ + VERSION_ + TENANT_ID_ 唯一）';

create table if not exists ACT_RU_EXECUTION (
    ID_ varchar(64) not null,
    REV_ int,
    PROC_INST_ID_ varchar(64),
    BUSINESS_KEY_ varchar(255),
    PARENT_ID_ varchar(64),
    PROC_DEF_ID_ varchar(64),
    SUPER_EXEC_ varchar(64),
    ROOT_PROC_INST_ID_ varchar(64),
    ACT_ID_ varchar(255),
    IS_ACTIVE_ tinyint,
    IS_CONCURRENT_ tinyint,
    IS_SCOPE_ tinyint,
    IS_EVENT_SCOPE_ tinyint,
    IS_MI_ROOT_ tinyint,
    SUSPENSION_STATE_ int,
    CACHED_ENT_STATE_ int,
    TENANT_ID_ varchar(255) default '',
    NAME_ varchar(255),
    START_TIME_ datetime(3),
    START_USER_ID_ varchar(255),
    LOCK_TIME_ timestamp(3),
    IS_COUNT_ENABLED_ tinyint,
    EVT_SUBSCR_COUNT_ int,
    TASK_COUNT_ int,
    JOB_COUNT_ int,
    TIMER_JOB_COUNT_ int,
    SUSP_JOB_COUNT_ int,
    DEADLETTER_JOB_COUNT_ int,
    VAR_COUNT_ int,
    ID_LINK_COUNT_ int,
    APP_VERSION_ int,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='运行时执行实例表：执行树，引擎唯一的运行态（流程实例 / 并发分支 / 作用域）';

create table if not exists ACT_RU_TASK (
    ID_ varchar(64) not null,
    REV_ int,
    EXECUTION_ID_ varchar(64),
    PROC_INST_ID_ varchar(64),
    PROC_DEF_ID_ varchar(64),
    NAME_ varchar(255),
    BUSINESS_KEY_ varchar(255),
    PARENT_TASK_ID_ varchar(64),
    DESCRIPTION_ varchar(4000),
    TASK_DEF_KEY_ varchar(255),
    OWNER_ varchar(255),
    ASSIGNEE_ varchar(255),
    DELEGATION_ varchar(64),
    PRIORITY_ int,
    CREATE_TIME_ timestamp(3),
    DUE_DATE_ datetime(3),
    CATEGORY_ varchar(255),
    SUSPENSION_STATE_ int,
    TENANT_ID_ varchar(255) default '',
    FORM_KEY_ varchar(255),
    CLAIM_TIME_ datetime(3),
    APP_VERSION_ int,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='运行时任务表：当前待办的用户任务';

create table if not exists ACT_RU_IDENTITYLINK (
    ID_ varchar(64) not null,
    REV_ int,
    GROUP_ID_ varchar(255),
    TYPE_ varchar(255),
    USER_ID_ varchar(255),
    TASK_ID_ varchar(64),
    PROC_INST_ID_ varchar(64),
    PROC_DEF_ID_ varchar(64),
    DETAILS_ LONGBLOB,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='运行时身份关联表：任务的用户 / 组关联（办理人、候选人与候选组）';

create table if not exists ACT_RU_VARIABLE (
    ID_ varchar(64) not null,
    REV_ int,
    TYPE_ varchar(255) not null,
    NAME_ varchar(255) not null,
    EXECUTION_ID_ varchar(64),
    PROC_INST_ID_ varchar(64),
    TASK_ID_ varchar(64),
    BYTEARRAY_ID_ varchar(64),
    DOUBLE_ double,
    LONG_ bigint,
    TEXT_ varchar(4000),
    TEXT2_ varchar(4000),
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='运行时变量表：流程实例、执行与任务级变量';

create table if not exists ACT_HI_PROCINST (
    ID_ varchar(64) not null,
    PROC_INST_ID_ varchar(64) not null,
    BUSINESS_KEY_ varchar(255),
    PROC_DEF_ID_ varchar(64) not null,
    START_TIME_ datetime(3) not null,
    END_TIME_ datetime(3),
    DURATION_ bigint,
    START_USER_ID_ varchar(255),
    START_ACT_ID_ varchar(255),
    END_ACT_ID_ varchar(255),
    SUPER_PROCESS_INSTANCE_ID_ varchar(64),
    DELETE_REASON_ varchar(4000),
    TENANT_ID_ varchar(255) default '',
    NAME_ varchar(255),
    primary key (ID_),
    unique (PROC_INST_ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史流程实例表：记录已启动过的流程实例';

create table if not exists ACT_HI_ACTINST (
    ID_ varchar(64) not null,
    PROC_DEF_ID_ varchar(64) not null,
    PROC_INST_ID_ varchar(64) not null,
    EXECUTION_ID_ varchar(64) not null,
    ACT_ID_ varchar(255) not null,
    TASK_ID_ varchar(64),
    CALL_PROC_INST_ID_ varchar(64),
    ACT_NAME_ varchar(255),
    ACT_TYPE_ varchar(255) not null,
    ASSIGNEE_ varchar(255),
    START_TIME_ datetime(3) not null,
    END_TIME_ datetime(3),
    DURATION_ bigint,
    DELETE_REASON_ varchar(4000),
    TENANT_ID_ varchar(255) default '',
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史活动实例表：流程图着色数据源';

create table if not exists ACT_HI_TASKINST (
    ID_ varchar(64) not null,
    PROC_DEF_ID_ varchar(64),
    TASK_DEF_KEY_ varchar(255),
    PROC_INST_ID_ varchar(64),
    EXECUTION_ID_ varchar(64),
    NAME_ varchar(255),
    PARENT_TASK_ID_ varchar(64),
    DESCRIPTION_ varchar(4000),
    OWNER_ varchar(255),
    ASSIGNEE_ varchar(255),
    START_TIME_ datetime(3) not null,
    CLAIM_TIME_ datetime(3),
    END_TIME_ datetime(3),
    DURATION_ bigint,
    DELETE_REASON_ varchar(4000),
    PRIORITY_ int,
    DUE_DATE_ datetime(3),
    FORM_KEY_ varchar(255),
    CATEGORY_ varchar(255),
    TENANT_ID_ varchar(255) default '',
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史任务实例表：记录已产生过的用户任务';

create table if not exists ACT_HI_VARINST (
    ID_ varchar(64) not null,
    PROC_INST_ID_ varchar(64),
    EXECUTION_ID_ varchar(64),
    TASK_ID_ varchar(64),
    NAME_ varchar(255) not null,
    VAR_TYPE_ varchar(100),
    REV_ int,
    BYTEARRAY_ID_ varchar(64),
    DOUBLE_ double,
    LONG_ bigint,
    TEXT_ varchar(4000),
    TEXT2_ varchar(4000),
    CREATE_TIME_ datetime(3),
    LAST_UPDATED_TIME_ datetime(3),
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史变量实例表：变量在流程结束时的最终值';

create table if not exists ACT_HI_IDENTITYLINK (
    ID_ varchar(64) not null,
    GROUP_ID_ varchar(255),
    TYPE_ varchar(255),
    USER_ID_ varchar(255),
    TASK_ID_ varchar(64),
    PROC_INST_ID_ varchar(64),
    DETAILS_ LONGBLOB,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史身份关联表：历史任务的用户 / 组关联';


-- ─────────────────────────── ①-1 引擎表索引 ───────────────────────────

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_BUSKEY on ACT_RU_EXECUTION (BUSINESS_KEY_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXEC_BUSKEY');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDC_EXEC_ROOT on ACT_RU_EXECUTION (ROOT_PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDC_EXEC_ROOT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_CREATE on ACT_RU_TASK (CREATE_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_CREATE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_USER on ACT_RU_IDENTITYLINK (USER_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and index_name = 'ACT_IDX_IDENT_LNK_USER');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_GROUP on ACT_RU_IDENTITYLINK (GROUP_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and index_name = 'ACT_IDX_IDENT_LNK_GROUP');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_TASK on ACT_RU_IDENTITYLINK (TASK_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and index_name = 'ACT_IDX_IDENT_LNK_TASK');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_TASK_ID on ACT_RU_VARIABLE (TASK_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_VARIABLE' and index_name = 'ACT_IDX_VARIABLE_TASK_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_ATHRZ_PROCEDEF on ACT_RU_IDENTITYLINK (PROC_DEF_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and index_name = 'ACT_IDX_ATHRZ_PROCEDEF');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID on ACT_RE_PROCDEF (DEPLOYMENT_ID_, ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RE_PROCDEF' and index_name = 'ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PRO_INST_END on ACT_HI_PROCINST (END_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_PROCINST' and index_name = 'ACT_IDX_HI_PRO_INST_END');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PRO_I_BUSKEY on ACT_HI_PROCINST (BUSINESS_KEY_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_PROCINST' and index_name = 'ACT_IDX_HI_PRO_I_BUSKEY');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_ACT_INST_START on ACT_HI_ACTINST (START_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_ACTINST' and index_name = 'ACT_IDX_HI_ACT_INST_START');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_ACT_INST_END on ACT_HI_ACTINST (END_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_ACTINST' and index_name = 'ACT_IDX_HI_ACT_INST_END');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_ACT_INST_PROCINST on ACT_HI_ACTINST (PROC_INST_ID_, ACT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_ACTINST' and index_name = 'ACT_IDX_HI_ACT_INST_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_ACT_INST_EXEC on ACT_HI_ACTINST (EXECUTION_ID_, ACT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_ACTINST' and index_name = 'ACT_IDX_HI_ACT_INST_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PROCVAR_PROC_INST on ACT_HI_VARINST (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_VARINST' and index_name = 'ACT_IDX_HI_PROCVAR_PROC_INST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PROCVAR_NAME_TYPE on ACT_HI_VARINST (NAME_, VAR_TYPE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_VARINST' and index_name = 'ACT_IDX_HI_PROCVAR_NAME_TYPE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PROCVAR_TASK_ID on ACT_HI_VARINST (TASK_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_VARINST' and index_name = 'ACT_IDX_HI_PROCVAR_TASK_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_IDENT_LNK_USER on ACT_HI_IDENTITYLINK (USER_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_IDENTITYLINK' and index_name = 'ACT_IDX_HI_IDENT_LNK_USER');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_IDENT_LNK_TASK on ACT_HI_IDENTITYLINK (TASK_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_IDENTITYLINK' and index_name = 'ACT_IDX_HI_IDENT_LNK_TASK');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_IDENT_LNK_PROCINST on ACT_HI_IDENTITYLINK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_IDENTITYLINK' and index_name = 'ACT_IDX_HI_IDENT_LNK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_TASK_INST_PROCINST on ACT_HI_TASKINST (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_TASKINST' and index_name = 'ACT_IDX_HI_TASK_INST_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;



-- ─────────────────────────── ①-2 引擎表唯一约束与外键 ───────────────────────────

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RE_PROCDEF add constraint ACT_UNIQ_PROCDEF unique (KEY_, VERSION_, TENANT_ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RE_PROCDEF' and constraint_name = 'ACT_UNIQ_PROCDEF');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_GE_BYTEARRAY add constraint ACT_FK_BYTEARR_DEPL foreign key (DEPLOYMENT_ID_) references ACT_RE_DEPLOYMENT (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_GE_BYTEARRAY' and constraint_name = 'ACT_FK_BYTEARR_DEPL');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_EXECUTION add constraint ACT_FK_EXE_PROCINST foreign key (PROC_INST_ID_) references ACT_RU_EXECUTION (ID_) on delete cascade on update cascade',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_EXECUTION' and constraint_name = 'ACT_FK_EXE_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_EXECUTION add constraint ACT_FK_EXE_PARENT foreign key (PARENT_ID_) references ACT_RU_EXECUTION (ID_) on delete cascade',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_EXECUTION' and constraint_name = 'ACT_FK_EXE_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_EXECUTION add constraint ACT_FK_EXE_SUPER foreign key (SUPER_EXEC_) references ACT_RU_EXECUTION (ID_) on delete cascade',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_EXECUTION' and constraint_name = 'ACT_FK_EXE_SUPER');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_EXECUTION add constraint ACT_FK_EXE_PROCDEF foreign key (PROC_DEF_ID_) references ACT_RE_PROCDEF (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_EXECUTION' and constraint_name = 'ACT_FK_EXE_PROCDEF');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_IDENTITYLINK add constraint ACT_FK_TSKASS_TASK foreign key (TASK_ID_) references ACT_RU_TASK (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and constraint_name = 'ACT_FK_TSKASS_TASK');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_IDENTITYLINK add constraint ACT_FK_ATHRZ_PROCEDEF foreign key (PROC_DEF_ID_) references ACT_RE_PROCDEF (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and constraint_name = 'ACT_FK_ATHRZ_PROCEDEF');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_IDENTITYLINK add constraint ACT_FK_IDL_PROCINST foreign key (PROC_INST_ID_) references ACT_RU_EXECUTION (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and constraint_name = 'ACT_FK_IDL_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TASK add constraint ACT_FK_TASK_EXE foreign key (EXECUTION_ID_) references ACT_RU_EXECUTION (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_TASK' and constraint_name = 'ACT_FK_TASK_EXE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TASK add constraint ACT_FK_TASK_PROCINST foreign key (PROC_INST_ID_) references ACT_RU_EXECUTION (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_TASK' and constraint_name = 'ACT_FK_TASK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TASK add constraint ACT_FK_TASK_PROCDEF foreign key (PROC_DEF_ID_) references ACT_RE_PROCDEF (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_TASK' and constraint_name = 'ACT_FK_TASK_PROCDEF');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_VARIABLE add constraint ACT_FK_VAR_EXE foreign key (EXECUTION_ID_) references ACT_RU_EXECUTION (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_VARIABLE' and constraint_name = 'ACT_FK_VAR_EXE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_VARIABLE add constraint ACT_FK_VAR_PROCINST foreign key (PROC_INST_ID_) references ACT_RU_EXECUTION (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_VARIABLE' and constraint_name = 'ACT_FK_VAR_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_VARIABLE add constraint ACT_FK_VAR_BYTEARRAY foreign key (BYTEARRAY_ID_) references ACT_GE_BYTEARRAY (ID_)',
        'select 1')
    from information_schema.table_constraints where constraint_schema = database() and table_name = 'ACT_RU_VARIABLE' and constraint_name = 'ACT_FK_VAR_BYTEARRAY');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── ①-3 引擎初值（ACT_GE_PROPERTY：仅在缺失时插入） ───────────────────────────

insert ignore into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('schema.version', '8.1.4', '1');
insert ignore into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('schema.history', 'create(8.1.4)', '1');
insert ignore into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('next.dbid', '1', '1');


-- ─────────────────────────── ② 流程实例业务表 / 流程类型 / 流程模型 ───────────────────────────

--  来源：业务系统存量库（原 wf schema，本库统一落 dbo / 无前缀，表名逐字相同）：
--        instances              流程实例表
--        instance_current_task  当前审批任务记录
--        deal_history           审批历史记录
--
--  与引擎表（ACT_*）的关系：
--    instances.workflow_instance_id  → ACT_RU_EXECUTION.ID_ / ACT_HI_PROCINST.PROC_INST_ID_
--    instance_current_task.task_id   → ACT_RU_TASK.ID_
--    deal_history.task_node          → 用户任务节点名（ACT_RU_TASK.NAME_）
--
--  业务表用 utf8mb4（deal_comment / describe 含长中文备注，预留 emoji / 扩展汉字空间），
--  引擎表仍用 utf8（与上游 Activiti 一致，对拍口径不变）。
--
--  流程类型表（workflow_type）：流程信息枚举（ProcessInfoEnum）的数据源，
--    系统启动时加载；种子数据在本节末尾（不新增 / 不覆盖 create_time）。
--  流程模型表（bpmn_model）：管理端设计器的模型落点（BPMN XML 编辑源），
--    与部署结果（ACT_RE_PROCDEF）分离 —— 模型可反复编辑，部署才产生新版本。
--
--  ⚠️ 主键均为业务侧生成的 BIGINT，不使用自增（与来源一致）。

create table if not exists instances (
    tenant_id bigint not null comment '多租户id',
    apply_id bigint not null comment '申请id',
    apply_time datetime not null comment '申请时间',
    workflow_type_id varchar(64) not null comment '流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
    workflow_version varchar(50) comment '流程版本',
    `describe` varchar(200) not null comment '流程实例描述',
    apply_emp_id varchar(50) not null comment '申请人id',
    apply_dept_id varchar(50) not null comment '申请人部门',
    business_id bigint not null comment '对应的业务id',
    business_dept_id varchar(50) comment '业务部门id',
    status_id varchar(50) not null comment '流程实例状态 审批中、已完成',
    newest_flag smallint not null default 1 comment '是否是最新流程实例',
    workflow_instance_id varchar(64) comment '流程实例id(对应于流程引擎中的id）',
    last_audited_time datetime comment '最后审批完成时间',
    create_emp_id varchar(50) not null comment '创建人',
    create_time datetime not null comment '创建时间',
    current_step varchar(200) comment '当前步骤名称',
    assign_emp varchar(200) comment '当前处理人',
    modify_emp_id varchar(50) comment '修改人',
    modify_time datetime comment '修改时间',
    deleted_flag tinyint not null default 0 comment '删除标志',
    original_business_id bigint comment '原始业务ID',
    flow_file_id bigint comment '流程图文件ID',
    primary key (apply_id),
    index ix_instances_workflow_instance_id (workflow_instance_id),
    index ix_instances_business_id (business_id),
    index ix_instances_status_id (tenant_id, status_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';

set @ddl = (select if(count(*) = 0,
        'create index ix_instances_workflow_instance_id on instances (workflow_instance_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'instances' and index_name = 'ix_instances_workflow_instance_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_instances_business_id on instances (business_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'instances' and index_name = 'ix_instances_business_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_instances_status_id on instances (tenant_id, status_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'instances' and index_name = 'ix_instances_status_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists instance_current_task (
    tenant_id bigint not null comment '多租户ID',
    step_id bigint not null comment '数据行id',
    apply_id bigint comment '申请id',
    task_id varchar(64) comment '步骤ID',
    task_node varchar(50) comment '当前步骤名称',
    task_owner varchar(200) comment '待办项归属人',
    assign_emp_id varchar(200) comment '当前审批者',
    form_key varchar(100) comment '流程步骤对应的环节配置',
    create_time datetime not null comment '创建时间',
    primary key (step_id),
    index ix_instance_current_task_apply_id (apply_id),
    index ix_instance_current_task_task_id (task_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='当前审批步骤及审批人（订单审批/请假审批流程共用）';

set @ddl = (select if(count(*) = 0,
        'create index ix_instance_current_task_apply_id on instance_current_task (apply_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'instance_current_task' and index_name = 'ix_instance_current_task_apply_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_instance_current_task_task_id on instance_current_task (task_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'instance_current_task' and index_name = 'ix_instance_current_task_task_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists deal_history (
    tenant_id bigint not null comment '多租户ID',
    deal_history_id bigint not null comment '数据行id',
    business_id bigint comment '对应业务表单的ID',
    apply_id bigint comment '申请id',
    task_node varchar(50) comment '步骤名称',
    deal_emp_id varchar(50) comment '审批者员工id',
    deal_time datetime comment '审批时间',
    deal_result varchar(50) comment '审批结果',
    deal_comment varchar(2000) comment '审批说明',
    form_key varchar(200) comment '流程步骤对应的环节配置',
    deal_emp_name varchar(50) comment '审批人姓名',
    deal_domain_account varchar(50) comment '审批人域帐号',
    primary key (deal_history_id),
    index ix_deal_history_apply_id (apply_id),
    index ix_deal_history_deal_emp_id (tenant_id, deal_emp_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='审批历史记录（订单审批/请假审批流程共用）';

set @ddl = (select if(count(*) = 0,
        'create index ix_deal_history_apply_id on deal_history (apply_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'deal_history' and index_name = 'ix_deal_history_apply_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_deal_history_deal_emp_id on deal_history (tenant_id, deal_emp_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'deal_history' and index_name = 'ix_deal_history_deal_emp_id');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists workflow_type (
    tenant_id bigint not null comment '多租户id',
    id varchar(64) not null comment '流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
    name varchar(50) not null comment '流程名称（流程 key，= BPMN process/@id，如 order_process）',
    `describe` varchar(200) not null comment '流程描述',
    view_url varchar(500) not null comment '业务单查看页地址（{businessId}/{processInstanceId} 占位符）',
    approve_url varchar(500) not null comment '流程审批页地址（{taskId}/{processInstanceId} 占位符）',
    sort_order int not null default 0 comment '展示顺序（小在前）',
    enabled tinyint not null default 1 comment '是否启用（0=停用，加载时跳过）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (id),
    index ix_workflow_type_sort (tenant_id, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）';

set @ddl = (select if(count(*) = 0,
        'create index ix_workflow_type_sort on workflow_type (tenant_id, sort_order)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'workflow_type' and index_name = 'ix_workflow_type_sort');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

insert into workflow_type (tenant_id, id, name, `describe`, view_url, approve_url, sort_order, enabled, create_time)
values (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', '订单审批流程', '/bill.html?process=order_process&businessId={businessId}', '/approve.html?process=order_process&taskId={taskId}', 1, 1, utc_timestamp()),
       (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', '请假申请流程', '/bill.html?process=leave_process&businessId={businessId}', '/approve.html?process=leave_process&taskId={taskId}', 2, 1, utc_timestamp()) on duplicate key update name = values(name), `describe` = values(`describe`), view_url = values(view_url), approve_url = values(approve_url), sort_order = values(sort_order), enabled = values(enabled), modify_time = values(modify_time);

create table if not exists bpmn_model (
    id varchar(64) not null comment '模型 ID（设计器 modelId，GUID 字符串）',
    tenant_id bigint not null comment '多租户 id',
    name varchar(200) not null comment '流程名称（BPMN process/@name）',
    process_key varchar(50) not null comment '流程 key（= BPMN process/@id，如 order_process）',
    description varchar(500) comment '流程描述',
    bpmn_xml mediumtext not null comment 'BPMN 2.0 XML 内容',
    svg mediumtext comment '流程图 SVG 预览（前端导出，可选）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (id),
    unique key ux_bpmn_model_key (tenant_id, process_key),
    index ix_bpmn_model_modify (tenant_id, modify_time)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程模型表（管理端流程设计器的模型落点；BPMN XML 的编辑源，部署后产生 ACT_RE_PROCDEF 版本）';

set @ddl = (select if(count(*) = 0,
        'create unique index ux_bpmn_model_key on bpmn_model (tenant_id, process_key)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'bpmn_model' and index_name = 'ux_bpmn_model_key');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_bpmn_model_modify on bpmn_model (tenant_id, modify_time)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'bpmn_model' and index_name = 'ix_bpmn_model_modify');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── ③ Demo 业务表与组织架构（含演示种子数据） ───────────────────────────

--  来源：**本项目新增** —— 订单审批 + 请假审批两条 Demo 的业务数据表。
--
--    leave_bill          请假单（days / reason 真列 + ext_params JSON 扩展参数）
--    order_bill          订单单（status 随审批回写：1=审批中，-1=拒绝，100=通过）
--    workflow_parameters 流程参数键值表（「实例 × 参数名」；param_value 用 text / nvarchar(max)）
--    sys_*               组织架构表（部门 / 员工 / 审批组 / 组成员）+ 演示种子数据
--
--  与流程实例表的关系：
--    instances.business_id          → leave_bill.business_id / order_bill.business_id
--    instances.workflow_instance_id → workflow_parameters.workflow_instance_id
--    instances.apply_emp_id         → sys_emp.domain_account（申请人在流程里的用户 ID）
--    instances.apply_dept_id        → sys_dept.dept_id
--
--  ⚠️ 这一组是 Demo / 示例业务表：只接引擎、不要业务表的环境可按需忽略本节的表，
--     但脚本整体执行（含本节的表）对引擎功能无影响。

create table if not exists leave_bill (
    tenant_id bigint not null comment '多租户id',
    business_id bigint not null comment '业务id（请假单号，由业务侧生成）',
    apply_user varchar(50) not null comment '申请人',
    apply_dept_id varchar(50) not null comment '申请部门',
    business_dept_id varchar(50) comment '业务部门',
    days smallint comment '请假天数（对应流程参数 days）；NULL = 还没填',
    reason varchar(500) comment '请假事由（对应流程参数 reason）',
    ext_params varchar(2000) comment '扩展流程参数（JSON 对象）：除 days / reason 外的键值对',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (business_id),
    index ix_leave_bill_apply_user (tenant_id, apply_user)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）';

set @ddl = (select if(count(*) = 0,
        'create index ix_leave_bill_apply_user on leave_bill (tenant_id, apply_user)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'leave_bill' and index_name = 'ix_leave_bill_apply_user');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists order_bill (
    tenant_id bigint not null comment '多租户id',
    business_id bigint not null comment '业务id（订单单号，由业务侧生成）',
    product_name varchar(200) comment '订购产品（对应流程参数 product_name）',
    amount decimal(18,2) comment '订单金额（对应流程参数 amount）',
    remark varchar(500) comment '备注（对应流程参数 remark）',
    status smallint not null default 1 comment '状态：1=审批中，-1=审批拒绝，100=审批通过',
    approve_comment varchar(500) comment '最终审批意见（审批结束时随状态一起写回）',
    apply_user varchar(50) not null comment '申请人',
    apply_dept_id varchar(50) not null comment '申请部门',
    business_dept_id varchar(50) comment '业务部门',
    process_instance_id varchar(64) comment '流程实例 id（提交申请后回填，用于反查审批进度）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    deleted_flag tinyint not null default 0 comment '伪删除标志（0 正常 / 1 已删除；公共数据操作接口 DeleteAsync 置位，列表与明细自动过滤）',
    primary key (business_id),
    index ix_order_bill_apply_user (tenant_id, apply_user),
    index ix_order_bill_status (tenant_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）';

set @ddl = (select if(count(*) = 0,
        'create index ix_order_bill_apply_user on order_bill (tenant_id, apply_user)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'order_bill' and index_name = 'ix_order_bill_apply_user');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_order_bill_status on order_bill (tenant_id, status)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'order_bill' and index_name = 'ix_order_bill_status');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists workflow_parameters (
    tenant_id bigint not null comment '多租户id',
    workflow_instance_id varchar(64) not null comment '流程实例id（对应 instances.workflow_instance_id）',
    param_name varchar(100) not null comment '参数名（如 days / reason / product_name）',
    param_value mediumtext comment '参数值（null = 显式置空）',
    create_time datetime not null comment '创建时间',
    modify_time datetime comment '最后修改时间',
    primary key (workflow_instance_id, param_name),
    index ix_workflow_parameters_instance (workflow_instance_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）';

set @ddl = (select if(count(*) = 0,
        'create index ix_workflow_parameters_instance on workflow_parameters (workflow_instance_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'workflow_parameters' and index_name = 'ix_workflow_parameters_instance');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists sys_dept (
    dept_id bigint not null comment '部门ID',
    tenant_id bigint not null comment '多租户ID',
    dept_name varchar(100) not null comment '部门名称',
    parent_id bigint comment '上级部门ID（NULL=顶级）',
    sort_order int not null default 0 comment '排序号',
    create_time datetime not null comment '创建时间',
    primary key (dept_id),
    index idx_sys_dept_tenant (tenant_id, parent_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='部门信息表';

set @ddl = (select if(count(*) = 0,
        'create index idx_sys_dept_tenant on sys_dept (tenant_id, parent_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'sys_dept' and index_name = 'idx_sys_dept_tenant');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists sys_emp (
    emp_id bigint not null comment '员工ID',
    tenant_id bigint not null comment '多租户ID',
    emp_name varchar(50) not null comment '员工姓名',
    domain_account varchar(50) not null comment '域账号（审批人/申请人在流程里的用户ID）',
    dept_id bigint not null comment '所属部门ID（sys_dept.dept_id）',
    create_time datetime not null comment '创建时间',
    primary key (emp_id),
    unique key uk_sys_emp_account (tenant_id, domain_account),
    index idx_sys_emp_dept (dept_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='员工信息表';

set @ddl = (select if(count(*) = 0,
        'create unique index uk_sys_emp_account on sys_emp (tenant_id, domain_account)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'sys_emp' and index_name = 'uk_sys_emp_account');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index idx_sys_emp_dept on sys_emp (dept_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'sys_emp' and index_name = 'idx_sys_emp_dept');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists sys_emp_group (
    group_id varchar(50) not null comment '组标识（流程定义 candidateGroups 引用的就是它）',
    tenant_id bigint not null comment '多租户ID',
    group_name varchar(100) not null comment '组名称（如：组长 / 经理）',
    sort_order int not null default 0 comment '排序号',
    create_time datetime not null comment '创建时间',
    primary key (group_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='用户组表（审批组）';

create table if not exists sys_emp_group_member (
    member_id bigint not null comment '成员行ID',
    tenant_id bigint not null comment '多租户ID',
    group_id varchar(50) not null comment '组标识（sys_emp_group.group_id）',
    emp_id bigint not null comment '员工ID（sys_emp.emp_id）',
    create_time datetime not null comment '创建时间',
    primary key (member_id),
    unique key uk_sys_emp_group_member (group_id, emp_id),
    index idx_sys_emp_group_member_emp (emp_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='用户组成员表';

set @ddl = (select if(count(*) = 0,
        'create unique index uk_sys_emp_group_member on sys_emp_group_member (group_id, emp_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'sys_emp_group_member' and index_name = 'uk_sys_emp_group_member');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index idx_sys_emp_group_member_emp on sys_emp_group_member (emp_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'sys_emp_group_member' and index_name = 'idx_sys_emp_group_member_emp');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── ③-1 组织架构种子数据 ───────────────────────────

insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
values (101, 1, '信息技术部', null, 1, utc_timestamp()),
       (102, 1, '成本管理部', null, 2, utc_timestamp()),
       (103, 1, '综合管理部', null, 3, utc_timestamp()) on duplicate key update dept_name = values(dept_name), parent_id = values(parent_id), sort_order = values(sort_order);

insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
values (1001, 1, '王小爱', 'alice', 101, utc_timestamp()),
       (1002, 1, '李小波', 'bob', 101, utc_timestamp()),
       (1003, 1, '赵志强', 'leader1', 101, utc_timestamp()),
       (1004, 1, '钱有为', 'leader2', 102, utc_timestamp()),
       (1005, 1, '孙建国', 'manager1', 103, utc_timestamp()) on duplicate key update emp_name = values(emp_name), dept_id = values(dept_id);

insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
values ('leaders', 1, '组长', 1, utc_timestamp()),
       ('managers', 1, '经理', 2, utc_timestamp()) on duplicate key update group_name = values(group_name), sort_order = values(sort_order);

insert ignore into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
values (1, 1, 'leaders', 1003, utc_timestamp()),
       (2, 1, 'leaders', 1004, utc_timestamp()),
       (3, 1, 'managers', 1005, utc_timestamp());


-- ─────────────────────────── ④ 运行期查询索引（性能增强，不影响行内容） ───────────────────────────

--  背景：上游 Activiti 的索引清单里，运行期（ACT_RU_*）只有少数几个；
--        本引擎高频执行的查询（执行树按流程实例/父执行、任务按办理人/流程实例、
--        变量按执行、关系按流程实例）在 MySQL 上会全表扫描。
--
--  幂等：MySQL 用 information_schema 判重后动态执行，SQL Server 用 sys.indexes 判重。

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PROC_INST_PARENT on ACT_RU_EXECUTION (PROC_INST_ID_, PARENT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXEC_PROC_INST_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PARENT on ACT_RU_EXECUTION (PARENT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXEC_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_ACT_PROCINST_ACTIVE on ACT_RU_EXECUTION (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXEC_ACT_PROCINST_ACTIVE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PROC_INST_ID on ACT_RU_EXECUTION (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXEC_PROC_INST_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_PROC on ACT_RU_EXECUTION (PROC_DEF_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXECUTION_PROC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_SUPER on ACT_RU_EXECUTION (SUPER_EXEC_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXECUTION_SUPER');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_IDANDREV on ACT_RU_EXECUTION (ID_, REV_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_EXECUTION' and index_name = 'ACT_IDX_EXECUTION_IDANDREV');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROCINST on ACT_RU_TASK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_EXEC on ACT_RU_TASK (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROC_DEF_ID on ACT_RU_TASK (PROC_DEF_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_PROC_DEF_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_ASSIGNEE on ACT_RU_TASK (ASSIGNEE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_ASSIGNEE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_EXEC_NAME on ACT_RU_VARIABLE (EXECUTION_ID_, NAME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_VARIABLE' and index_name = 'ACT_IDX_VARIABLE_EXEC_NAME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_EXEC on ACT_RU_VARIABLE (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_VARIABLE' and index_name = 'ACT_IDX_VARIABLE_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_PROCINST on ACT_RU_VARIABLE (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_VARIABLE' and index_name = 'ACT_IDX_VARIABLE_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_BA on ACT_RU_VARIABLE (BYTEARRAY_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_VARIABLE' and index_name = 'ACT_IDX_VARIABLE_BA');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_PROCINST on ACT_RU_IDENTITYLINK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK' and index_name = 'ACT_IDX_IDENT_LNK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_ASSIGNEE_CREATE on ACT_RU_TASK (ASSIGNEE_, CREATE_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_ASSIGNEE_CREATE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROCINST_DEFKEY on ACT_RU_TASK (PROC_INST_ID_, TASK_DEF_KEY_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_PROCINST_DEFKEY');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_TENANT on ACT_RU_TASK (TENANT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_TENANT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_DUEDATE on ACT_RU_TASK (DUE_DATE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TASK' and index_name = 'ACT_IDX_TASK_DUEDATE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_TASK_ASSIGNEE_END on ACT_HI_TASKINST (ASSIGNEE_, END_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_TASKINST' and index_name = 'ACT_IDX_HI_TASK_ASSIGNEE_END');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_TASK_PROCINST_END on ACT_HI_TASKINST (PROC_INST_ID_, END_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_TASKINST' and index_name = 'ACT_IDX_HI_TASK_PROCINST_END');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_TASK_DEFKEY on ACT_HI_TASKINST (TASK_DEF_KEY_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_TASKINST' and index_name = 'ACT_IDX_HI_TASK_DEFKEY');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_HI_PROC_TENANT_END on ACT_HI_PROCINST (TENANT_ID_, END_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_PROCINST' and index_name = 'ACT_IDX_HI_PROC_TENANT_END');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_BYTEARRAY_DEPLOYMENT on ACT_GE_BYTEARRAY (DEPLOYMENT_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_GE_BYTEARRAY' and index_name = 'ACT_IDX_BYTEARRAY_DEPLOYMENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_DEPLOYMENT_TIME on ACT_RE_DEPLOYMENT (DEPLOY_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RE_DEPLOYMENT' and index_name = 'ACT_IDX_DEPLOYMENT_TIME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;



-- ─────────────────────────── ⑤ 应用层扩展表（幂等键 / 定时作业 / 迁移审计 / 模型历史 / 治理 / 异步作业） ───────────────────────────

--  ACT_IDEMPOTENCY      HTTP 幂等键（POST 重试回放，不属于 Activiti 引擎表）
--  ACT_RU_TIMER_JOB     边界定时器作业（任务创建时写一条，到点触发取消分支）
--  ACT_HI_MIGRATION     流程实例迁移审计（源/目标定义、活动映射、原因、操作人）
--  bpmn_model_history   流程模型版本历史（每次保存/回滚一版）
--  admin_audit_log      管理动作审计（转办 / 跳转 / 变量直写）
--  admin_history_archive 历史归档快照（归档后原 ACT_HI_* 行删除）
--  ACT_RU_JOB           asyncBefore / asyncAfter 异步作业（含重试与死信）
--  ACT_EVT_OUTBOX       事务性事件外发队列（至少一次投递，含发布重试/死信）
--
--  全部幂等：表已存在时跳过，缺索引/缺列由后续守卫补齐。

create table if not exists ACT_IDEMPOTENCY (
    IDEMPOTENCY_KEY_ varchar(120) not null,
    REQUEST_HASH_ varchar(64) not null,
    STATUS_CODE_ int,
    CONTENT_TYPE_ varchar(200),
    RESPONSE_BODY_ mediumtext,
    CREATED_AT_ datetime(3) not null,
    EXPIRES_AT_ datetime(3) not null,
    primary key (IDEMPOTENCY_KEY_),
    index ACT_IDX_IDEMP_EXPIRES (EXPIRES_AT_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDEMP_EXPIRES on ACT_IDEMPOTENCY (EXPIRES_AT_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_IDEMPOTENCY' and index_name = 'ACT_IDX_IDEMP_EXPIRES');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

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

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TIMER_JOB_DUEDATE on ACT_RU_TIMER_JOB (DUEDATE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and index_name = 'ACT_IDX_TIMER_JOB_DUEDATE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TIMER_JOB_EXECUTION on ACT_RU_TIMER_JOB (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and index_name = 'ACT_IDX_TIMER_JOB_EXECUTION');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TIMER_JOB_PROCINST on ACT_RU_TIMER_JOB (PROCESS_INSTANCE_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and index_name = 'ACT_IDX_TIMER_JOB_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TIMER_JOB_DEADLINE on ACT_RU_TIMER_JOB (DEAD_LETTER_, DUEDATE_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and index_name = 'ACT_IDX_TIMER_JOB_DEADLINE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

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
    index ACT_IDX_MIGRATION_PROCINST (PROC_INST_ID_),
    index ACT_IDX_MIGRATION_PROCINST_TIME (PROC_INST_ID_, MIGRATED_AT_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='流程实例迁移审计表：记录源/目标定义、活动映射、原因与操作人';

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_MIGRATION_PROCINST on ACT_HI_MIGRATION (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_MIGRATION' and index_name = 'ACT_IDX_MIGRATION_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_MIGRATION_PROCINST_TIME on ACT_HI_MIGRATION (PROC_INST_ID_, MIGRATED_AT_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_HI_MIGRATION' and index_name = 'ACT_IDX_MIGRATION_PROCINST_TIME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists bpmn_model_history (
    id varchar(64) not null comment '历史版本 id',
    tenant_id bigint not null comment '多租户 id',
    model_id varchar(64) not null comment '模型 id（bpmn_model.id）',
    revision int not null comment '版本号（同一模型内递增，从 1 开始）',
    name varchar(200) not null comment '保存时的流程名称',
    process_key varchar(50) not null comment '保存时的流程 key',
    description varchar(500) comment '保存时的流程描述',
    bpmn_xml mediumtext not null comment 'BPMN XML 快照',
    svg mediumtext comment '流程图 SVG 快照',
    action varchar(32) not null comment '动作：save / rollback',
    operator varchar(255) comment '操作人（请求身份）',
    created_at datetime(3) not null comment '写入时间（UTC）',
    primary key (id),
    unique key ux_bpmn_model_history_rev (model_id, revision),
    index ix_bpmn_model_history_tenant (tenant_id, model_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程模型版本历史：每次保存/回滚追加一版，支持回滚与变更追溯';

set @ddl = (select if(count(*) = 0,
        'create unique index ux_bpmn_model_history_rev on bpmn_model_history (model_id, revision)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'bpmn_model_history' and index_name = 'ux_bpmn_model_history_rev');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_bpmn_model_history_tenant on bpmn_model_history (tenant_id, model_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'bpmn_model_history' and index_name = 'ix_bpmn_model_history_tenant');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

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

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_audit_tenant_time on admin_audit_log (tenant_id, created_at)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_audit_log' and index_name = 'ix_admin_audit_tenant_time');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_audit_actor on admin_audit_log (tenant_id, actor, created_at)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_audit_log' and index_name = 'ix_admin_audit_actor');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_audit_action on admin_audit_log (action)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_audit_log' and index_name = 'ix_admin_audit_action');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_audit_target on admin_audit_log (target_type, target_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_audit_log' and index_name = 'ix_admin_audit_target');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_audit_instance on admin_audit_log (process_instance_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_audit_log' and index_name = 'ix_admin_audit_instance');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

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

set @ddl = (select if(count(*) = 0,
        'create unique index ux_admin_history_archive_instance on admin_history_archive (process_instance_id)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_history_archive' and index_name = 'ux_admin_history_archive_instance');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_history_archive_tenant on admin_history_archive (tenant_id, end_time)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_history_archive' and index_name = 'ix_admin_history_archive_tenant');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_history_archive_business on admin_history_archive (business_key)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_history_archive' and index_name = 'ix_admin_history_archive_business');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ix_admin_history_archive_archived on admin_history_archive (archived_at)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'admin_history_archive' and index_name = 'ix_admin_history_archive_archived');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists ACT_RU_JOB (
    ID_ varchar(64) not null comment '作业 id（IIdGenerator 分配）',
    REV_ int not null default 1 comment '乐观锁版本',
    EXECUTION_ID_ varchar(64) not null comment '执行 id',
    PROCESS_INSTANCE_ID_ varchar(64) not null comment '流程实例 id',
    PROC_DEF_ID_ varchar(255) comment '流程定义 id',
    ACTIVITY_ID_ varchar(255) not null comment '活动 id',
    PHASE_ varchar(16) not null comment 'before / after',
    DUEDATE_ datetime(3) not null comment '到期时间（UTC）',
    TENANT_ID_ varchar(64) not null default '' comment '租户',
    CREATE_TIME_ datetime(3) not null comment '创建时间（UTC）',
    RETRIES_ int not null default 3 comment '剩余重试次数',
    DEAD_LETTER_ tinyint not null default 0 comment '1=已进死信',
    EXCEPTION_MSG_ varchar(1000) comment '最后一次失败原因',
    LAST_FAILURE_ datetime(3) comment '最后一次失败时间（UTC）',
    primary key (ID_),
    index ACT_IDX_JOB_DUE (DUEDATE_, DEAD_LETTER_),
    index ACT_IDX_JOB_EXECUTION (EXECUTION_ID_),
    index ACT_IDX_JOB_PROC_INST (PROCESS_INSTANCE_ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='异步作业表：asyncBefore / asyncAfter 作业（独占，一个作业 = 一个命令）';

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_JOB_DUE on ACT_RU_JOB (DUEDATE_, DEAD_LETTER_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_JOB' and index_name = 'ACT_IDX_JOB_DUE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_JOB_EXECUTION on ACT_RU_JOB (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_JOB' and index_name = 'ACT_IDX_JOB_EXECUTION');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_JOB_PROC_INST on ACT_RU_JOB (PROCESS_INSTANCE_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_RU_JOB' and index_name = 'ACT_IDX_JOB_PROC_INST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

create table if not exists ACT_EVT_OUTBOX (
    ID_ varchar(64) not null comment '事件 id（订阅方幂等键）',
    EVENT_TYPE_ varchar(64) not null comment '事件类型（PROCESS_COMPLETED / TASK_CREATED ...）',
    ENTITY_ID_ varchar(64) comment '实体 id（任务 / 活动 / 实例）',
    PROCESS_INSTANCE_ID_ varchar(64) comment '流程实例 id',
    PROC_DEF_ID_ varchar(255) comment '流程定义 id',
    EXECUTION_ID_ varchar(64) comment '执行 id',
    ACTIVITY_ID_ varchar(255) comment '活动 id',
    TENANT_ID_ varchar(64) not null default '' comment '租户',
    PAYLOAD_ mediumtext comment '事件载荷（JSON）',
    CREATE_TIME_ datetime(3) not null comment '创建时间（UTC）',
    PUBLISHED_ tinyint not null default 0 comment '1=已发布',
    PUBLISH_ATTEMPTS_ int not null default 0 comment '发布尝试次数',
    DEAD_LETTER_ tinyint not null default 0 comment '1=投递失败进死信',
    LAST_ERROR_ varchar(1000) comment '最后一次发布失败原因',
    NEXT_ATTEMPT_ datetime(3) comment '下次可发布时间（UTC）',
    PUBLISHED_TIME_ datetime(3) comment '发布时间（UTC）',
    primary key (ID_),
    index ACT_IDX_OUTBOX_PENDING (PUBLISHED_, DEAD_LETTER_, CREATE_TIME_),
    index ACT_IDX_OUTBOX_PROC_INST (PROCESS_INSTANCE_ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='事务性事件 outbox：引擎事件与业务写入同事务落库，由发布器至少一次投递';

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_OUTBOX_PENDING on ACT_EVT_OUTBOX (PUBLISHED_, DEAD_LETTER_, CREATE_TIME_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_EVT_OUTBOX' and index_name = 'ACT_IDX_OUTBOX_PENDING');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_OUTBOX_PROC_INST on ACT_EVT_OUTBOX (PROCESS_INSTANCE_ID_)',
        'select 1')
    from information_schema.statistics where table_schema = database() and table_name = 'ACT_EVT_OUTBOX' and index_name = 'ACT_IDX_OUTBOX_PROC_INST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── ⑥ 老库兼容补齐（幂等；新库执行为空操作） ───────────────────────────

--  这一节只为「用旧版脚本建过库」的环境准备：
--    · 模型大文本列仍是 text（64KB）→ 扩容到 mediumtext；
--    · 定时作业表缺重试 / 死信列 → 补列。
--  新库在上面已经把表建成最终形态，这里全部是空操作。

set @ddl = ifnull((select if(data_type in ('mediumtext', 'longtext'), 'select 1', 'alter table bpmn_model modify column bpmn_xml mediumtext not null')
    from information_schema.columns
    where table_schema = database() and table_name = 'bpmn_model' and column_name = 'bpmn_xml'), 'select 1');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = ifnull((select if(data_type in ('mediumtext', 'longtext'), 'select 1', 'alter table bpmn_model modify column svg mediumtext')
    from information_schema.columns
    where table_schema = database() and table_name = 'bpmn_model' and column_name = 'svg'), 'select 1');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = ifnull((select if(data_type in ('mediumtext', 'longtext'), 'select 1', 'alter table bpmn_model_history modify column bpmn_xml mediumtext not null')
    from information_schema.columns
    where table_schema = database() and table_name = 'bpmn_model_history' and column_name = 'bpmn_xml'), 'select 1');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = ifnull((select if(data_type in ('mediumtext', 'longtext'), 'select 1', 'alter table bpmn_model_history modify column svg mediumtext')
    from information_schema.columns
    where table_schema = database() and table_name = 'bpmn_model_history' and column_name = 'svg'), 'select 1');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column RETRIES_ int default 3',
        'select 1')
    from information_schema.columns where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'RETRIES_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column DEAD_LETTER_ tinyint default 0',
        'select 1')
    from information_schema.columns where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'DEAD_LETTER_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column EXCEPTION_MSG_ varchar(1000)',
        'select 1')
    from information_schema.columns where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'EXCEPTION_MSG_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'alter table ACT_RU_TIMER_JOB add column LAST_FAILURE_ datetime(3)',
        'select 1')
    from information_schema.columns where table_schema = database() and table_name = 'ACT_RU_TIMER_JOB' and column_name = 'LAST_FAILURE_');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;


-- ─────────────────────────── 执行结果自检 ───────────────────────────
--  期望 schema.version = 8.1.4（与 DatabaseSchemaChecker.ExpectedSchemaVersion 一致）；
--  next.dbid 是 ID 区块游标，重复执行不会重置。
select NAME_, VALUE_ from ACT_GE_PROPERTY where NAME_ in ('schema.version', 'next.dbid');

