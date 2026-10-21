-- ============================================================================
--  NetBpm · SQL Server 数据库初始化脚本（自动生成，请勿手改）
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
--    ⑦ 表与列注释（SQL Server 扩展属性；MySQL 注释内联在建表语句里）
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
if db_id(N'netbpm') is null create database [netbpm];
go
use [netbpm];
go
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

if object_id(N'ACT_GE_PROPERTY', N'U') is null
    create table ACT_GE_PROPERTY (
    NAME_ nvarchar(64) not null,
    VALUE_ nvarchar(300),
    REV_ int,
    primary key (NAME_)
    );
go

if object_id(N'ACT_GE_BYTEARRAY', N'U') is null
    create table ACT_GE_BYTEARRAY (
    ID_ nvarchar(64) not null,
    REV_ int,
    NAME_ nvarchar(255),
    DEPLOYMENT_ID_ nvarchar(64),
    BYTES_ varbinary(max),
    GENERATED_ tinyint,
    primary key (ID_)
    );
go

if object_id(N'ACT_RE_DEPLOYMENT', N'U') is null
    create table ACT_RE_DEPLOYMENT (
    ID_ nvarchar(64) not null,
    NAME_ nvarchar(255),
    CATEGORY_ nvarchar(255),
    KEY_ nvarchar(255),
    TENANT_ID_ nvarchar(255) default '',
    DEPLOY_TIME_ datetime,
    ENGINE_VERSION_ nvarchar(255),
    VERSION_ int default 1,
    PROJECT_RELEASE_VERSION_ nvarchar(255),
    primary key (ID_)
    );
go

if object_id(N'ACT_RE_PROCDEF', N'U') is null
    create table ACT_RE_PROCDEF (
    ID_ nvarchar(64) not null,
    REV_ int,
    CATEGORY_ nvarchar(255),
    NAME_ nvarchar(255),
    KEY_ nvarchar(255) not null,
    VERSION_ int not null,
    DEPLOYMENT_ID_ nvarchar(64),
    RESOURCE_NAME_ nvarchar(4000),
    DGRM_RESOURCE_NAME_ nvarchar(4000),
    DESCRIPTION_ nvarchar(4000),
    HAS_START_FORM_KEY_ tinyint,
    HAS_GRAPHICAL_NOTATION_ tinyint,
    SUSPENSION_STATE_ int,
    TENANT_ID_ nvarchar(255) default '',
    ENGINE_VERSION_ nvarchar(255),
    APP_VERSION_ int,
    primary key (ID_)
    );
go

if object_id(N'ACT_RU_EXECUTION', N'U') is null
    create table ACT_RU_EXECUTION (
    ID_ nvarchar(64) not null,
    REV_ int,
    PROC_INST_ID_ nvarchar(64),
    BUSINESS_KEY_ nvarchar(255),
    PARENT_ID_ nvarchar(64),
    PROC_DEF_ID_ nvarchar(64),
    SUPER_EXEC_ nvarchar(64),
    ROOT_PROC_INST_ID_ nvarchar(64),
    ACT_ID_ nvarchar(255),
    IS_ACTIVE_ tinyint,
    IS_CONCURRENT_ tinyint,
    IS_SCOPE_ tinyint,
    IS_EVENT_SCOPE_ tinyint,
    IS_MI_ROOT_ tinyint,
    SUSPENSION_STATE_ int,
    CACHED_ENT_STATE_ int,
    TENANT_ID_ nvarchar(255) default '',
    NAME_ nvarchar(255),
    START_TIME_ datetime,
    START_USER_ID_ nvarchar(255),
    LOCK_TIME_ datetime,
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
    );
go

if object_id(N'ACT_RU_TASK', N'U') is null
    create table ACT_RU_TASK (
    ID_ nvarchar(64) not null,
    REV_ int,
    EXECUTION_ID_ nvarchar(64),
    PROC_INST_ID_ nvarchar(64),
    PROC_DEF_ID_ nvarchar(64),
    NAME_ nvarchar(255),
    BUSINESS_KEY_ nvarchar(255),
    PARENT_TASK_ID_ nvarchar(64),
    DESCRIPTION_ nvarchar(4000),
    TASK_DEF_KEY_ nvarchar(255),
    OWNER_ nvarchar(255),
    ASSIGNEE_ nvarchar(255),
    DELEGATION_ nvarchar(64),
    PRIORITY_ int,
    CREATE_TIME_ datetime,
    DUE_DATE_ datetime,
    CATEGORY_ nvarchar(255),
    SUSPENSION_STATE_ int,
    TENANT_ID_ nvarchar(255) default '',
    FORM_KEY_ nvarchar(255),
    CLAIM_TIME_ datetime,
    APP_VERSION_ int,
    primary key (ID_)
    );
go

if object_id(N'ACT_RU_IDENTITYLINK', N'U') is null
    create table ACT_RU_IDENTITYLINK (
    ID_ nvarchar(64) not null,
    REV_ int,
    GROUP_ID_ nvarchar(255),
    TYPE_ nvarchar(255),
    USER_ID_ nvarchar(255),
    TASK_ID_ nvarchar(64),
    PROC_INST_ID_ nvarchar(64),
    PROC_DEF_ID_ nvarchar(64),
    DETAILS_ varbinary(max),
    primary key (ID_)
    );
go

if object_id(N'ACT_RU_VARIABLE', N'U') is null
    create table ACT_RU_VARIABLE (
    ID_ nvarchar(64) not null,
    REV_ int,
    TYPE_ nvarchar(255) not null,
    NAME_ nvarchar(255) not null,
    EXECUTION_ID_ nvarchar(64),
    PROC_INST_ID_ nvarchar(64),
    TASK_ID_ nvarchar(64),
    BYTEARRAY_ID_ nvarchar(64),
    DOUBLE_ double precision,
    LONG_ numeric(19,0),
    TEXT_ nvarchar(4000),
    TEXT2_ nvarchar(4000),
    primary key (ID_)
    );
go

if object_id(N'ACT_HI_PROCINST', N'U') is null
    create table ACT_HI_PROCINST (
    ID_ nvarchar(64) not null,
    PROC_INST_ID_ nvarchar(64) not null,
    BUSINESS_KEY_ nvarchar(255),
    PROC_DEF_ID_ nvarchar(64) not null,
    START_TIME_ datetime not null,
    END_TIME_ datetime,
    DURATION_ numeric(19,0),
    START_USER_ID_ nvarchar(255),
    START_ACT_ID_ nvarchar(255),
    END_ACT_ID_ nvarchar(255),
    SUPER_PROCESS_INSTANCE_ID_ nvarchar(64),
    DELETE_REASON_ nvarchar(4000),
    TENANT_ID_ nvarchar(255) default '',
    NAME_ nvarchar(255),
    unique (PROC_INST_ID_),
    primary key (ID_)
    );
go

if object_id(N'ACT_HI_ACTINST', N'U') is null
    create table ACT_HI_ACTINST (
    ID_ nvarchar(64) not null,
    PROC_DEF_ID_ nvarchar(64) not null,
    PROC_INST_ID_ nvarchar(64) not null,
    EXECUTION_ID_ nvarchar(64) not null,
    ACT_ID_ nvarchar(255) not null,
    TASK_ID_ nvarchar(64),
    CALL_PROC_INST_ID_ nvarchar(64),
    ACT_NAME_ nvarchar(255),
    ACT_TYPE_ nvarchar(255) not null,
    ASSIGNEE_ nvarchar(255),
    START_TIME_ datetime not null,
    END_TIME_ datetime,
    DURATION_ numeric(19,0),
    DELETE_REASON_ nvarchar(4000),
    TENANT_ID_ nvarchar(255) default '',
    primary key (ID_)
    );
go

if object_id(N'ACT_HI_TASKINST', N'U') is null
    create table ACT_HI_TASKINST (
    ID_ nvarchar(64) not null,
    PROC_DEF_ID_ nvarchar(64),
    TASK_DEF_KEY_ nvarchar(255),
    PROC_INST_ID_ nvarchar(64),
    EXECUTION_ID_ nvarchar(64),
    NAME_ nvarchar(255),
    PARENT_TASK_ID_ nvarchar(64),
    DESCRIPTION_ nvarchar(4000),
    OWNER_ nvarchar(255),
    ASSIGNEE_ nvarchar(255),
    START_TIME_ datetime not null,
    CLAIM_TIME_ datetime,
    END_TIME_ datetime,
    DURATION_ numeric(19,0),
    DELETE_REASON_ nvarchar(4000),
    PRIORITY_ int,
    DUE_DATE_ datetime,
    FORM_KEY_ nvarchar(255),
    CATEGORY_ nvarchar(255),
    TENANT_ID_ nvarchar(255) default '',
    primary key (ID_)
    );
go

if object_id(N'ACT_HI_VARINST', N'U') is null
    create table ACT_HI_VARINST (
    ID_ nvarchar(64) not null,
    PROC_INST_ID_ nvarchar(64),
    EXECUTION_ID_ nvarchar(64),
    TASK_ID_ nvarchar(64),
    NAME_ nvarchar(255) not null,
    VAR_TYPE_ nvarchar(100),
    REV_ int,
    BYTEARRAY_ID_ nvarchar(64),
    DOUBLE_ double precision,
    LONG_ numeric(19,0),
    TEXT_ nvarchar(4000),
    TEXT2_ nvarchar(4000),
    CREATE_TIME_ datetime,
    LAST_UPDATED_TIME_ datetime,
    primary key (ID_)
    );
go

if object_id(N'ACT_HI_IDENTITYLINK', N'U') is null
    create table ACT_HI_IDENTITYLINK (
    ID_ nvarchar(64) not null,
    GROUP_ID_ nvarchar(255),
    TYPE_ nvarchar(255),
    USER_ID_ nvarchar(255),
    TASK_ID_ nvarchar(64),
    PROC_INST_ID_ nvarchar(64),
    DETAILS_ varbinary(max),
    primary key (ID_)
    );
go


-- ─────────────────────────── ①-1 引擎表索引 ───────────────────────────

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXEC_BUSKEY' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_BUSKEY on ACT_RU_EXECUTION (BUSINESS_KEY_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDC_EXEC_ROOT' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDC_EXEC_ROOT on ACT_RU_EXECUTION (ROOT_PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_CREATE' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_CREATE on ACT_RU_TASK (CREATE_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDENT_LNK_USER' and object_id = object_id('ACT_RU_IDENTITYLINK'))
create index ACT_IDX_IDENT_LNK_USER on ACT_RU_IDENTITYLINK (USER_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDENT_LNK_GROUP' and object_id = object_id('ACT_RU_IDENTITYLINK'))
create index ACT_IDX_IDENT_LNK_GROUP on ACT_RU_IDENTITYLINK (GROUP_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDENT_LNK_TASK' and object_id = object_id('ACT_RU_IDENTITYLINK'))
create index ACT_IDX_IDENT_LNK_TASK on ACT_RU_IDENTITYLINK (TASK_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_VARIABLE_TASK_ID' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_TASK_ID on ACT_RU_VARIABLE (TASK_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_ATHRZ_PROCEDEF' and object_id = object_id('ACT_RU_IDENTITYLINK'))
create index ACT_IDX_ATHRZ_PROCEDEF on ACT_RU_IDENTITYLINK (PROC_DEF_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID' and object_id = object_id('ACT_RE_PROCDEF'))
create index ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID on ACT_RE_PROCDEF (DEPLOYMENT_ID_, ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PRO_INST_END' and object_id = object_id('ACT_HI_PROCINST'))
create index ACT_IDX_HI_PRO_INST_END on ACT_HI_PROCINST (END_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PRO_I_BUSKEY' and object_id = object_id('ACT_HI_PROCINST'))
create index ACT_IDX_HI_PRO_I_BUSKEY on ACT_HI_PROCINST (BUSINESS_KEY_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_ACT_INST_START' and object_id = object_id('ACT_HI_ACTINST'))
create index ACT_IDX_HI_ACT_INST_START on ACT_HI_ACTINST (START_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_ACT_INST_END' and object_id = object_id('ACT_HI_ACTINST'))
create index ACT_IDX_HI_ACT_INST_END on ACT_HI_ACTINST (END_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_ACT_INST_PROCINST' and object_id = object_id('ACT_HI_ACTINST'))
create index ACT_IDX_HI_ACT_INST_PROCINST on ACT_HI_ACTINST (PROC_INST_ID_, ACT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_ACT_INST_EXEC' and object_id = object_id('ACT_HI_ACTINST'))
create index ACT_IDX_HI_ACT_INST_EXEC on ACT_HI_ACTINST (EXECUTION_ID_, ACT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PROCVAR_PROC_INST' and object_id = object_id('ACT_HI_VARINST'))
create index ACT_IDX_HI_PROCVAR_PROC_INST on ACT_HI_VARINST (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PROCVAR_NAME_TYPE' and object_id = object_id('ACT_HI_VARINST'))
create index ACT_IDX_HI_PROCVAR_NAME_TYPE on ACT_HI_VARINST (NAME_, VAR_TYPE_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PROCVAR_TASK_ID' and object_id = object_id('ACT_HI_VARINST'))
create index ACT_IDX_HI_PROCVAR_TASK_ID on ACT_HI_VARINST (TASK_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_IDENT_LNK_USER' and object_id = object_id('ACT_HI_IDENTITYLINK'))
create index ACT_IDX_HI_IDENT_LNK_USER on ACT_HI_IDENTITYLINK (USER_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_IDENT_LNK_TASK' and object_id = object_id('ACT_HI_IDENTITYLINK'))
create index ACT_IDX_HI_IDENT_LNK_TASK on ACT_HI_IDENTITYLINK (TASK_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_IDENT_LNK_PROCINST' and object_id = object_id('ACT_HI_IDENTITYLINK'))
create index ACT_IDX_HI_IDENT_LNK_PROCINST on ACT_HI_IDENTITYLINK (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_TASK_INST_PROCINST' and object_id = object_id('ACT_HI_TASKINST'))
create index ACT_IDX_HI_TASK_INST_PROCINST on ACT_HI_TASKINST (PROC_INST_ID_);
go


-- ─────────────────────────── ①-2 引擎表唯一约束与外键 ───────────────────────────

if not exists (select 1 from sys.objects where name = 'ACT_UNIQ_PROCDEF' and parent_object_id = object_id('ACT_RE_PROCDEF'))
    alter table ACT_RE_PROCDEF
        add constraint ACT_UNIQ_PROCDEF
        unique (KEY_, VERSION_, TENANT_ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_BYTEARR_DEPL' and parent_object_id = object_id('ACT_GE_BYTEARRAY'))
    alter table ACT_GE_BYTEARRAY
        add constraint ACT_FK_BYTEARR_DEPL
        foreign key (DEPLOYMENT_ID_)
        references ACT_RE_DEPLOYMENT (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_EXE_PROCINST' and parent_object_id = object_id('ACT_RU_EXECUTION'))
    alter table ACT_RU_EXECUTION
        add constraint ACT_FK_EXE_PROCINST
        foreign key (PROC_INST_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_EXE_PARENT' and parent_object_id = object_id('ACT_RU_EXECUTION'))
    alter table ACT_RU_EXECUTION
        add constraint ACT_FK_EXE_PARENT
        foreign key (PARENT_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_EXE_SUPER' and parent_object_id = object_id('ACT_RU_EXECUTION'))
    alter table ACT_RU_EXECUTION
        add constraint ACT_FK_EXE_SUPER
        foreign key (SUPER_EXEC_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_EXE_PROCDEF' and parent_object_id = object_id('ACT_RU_EXECUTION'))
    alter table ACT_RU_EXECUTION
        add constraint ACT_FK_EXE_PROCDEF
        foreign key (PROC_DEF_ID_)
        references ACT_RE_PROCDEF (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_TSKASS_TASK' and parent_object_id = object_id('ACT_RU_IDENTITYLINK'))
    alter table ACT_RU_IDENTITYLINK
        add constraint ACT_FK_TSKASS_TASK
        foreign key (TASK_ID_)
        references ACT_RU_TASK (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_ATHRZ_PROCEDEF' and parent_object_id = object_id('ACT_RU_IDENTITYLINK'))
    alter table ACT_RU_IDENTITYLINK
        add constraint ACT_FK_ATHRZ_PROCEDEF
        foreign key (PROC_DEF_ID_)
        references ACT_RE_PROCDEF (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_IDL_PROCINST' and parent_object_id = object_id('ACT_RU_IDENTITYLINK'))
    alter table ACT_RU_IDENTITYLINK
        add constraint ACT_FK_IDL_PROCINST
        foreign key (PROC_INST_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_TASK_EXE' and parent_object_id = object_id('ACT_RU_TASK'))
    alter table ACT_RU_TASK
        add constraint ACT_FK_TASK_EXE
        foreign key (EXECUTION_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_TASK_PROCINST' and parent_object_id = object_id('ACT_RU_TASK'))
    alter table ACT_RU_TASK
        add constraint ACT_FK_TASK_PROCINST
        foreign key (PROC_INST_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_TASK_PROCDEF' and parent_object_id = object_id('ACT_RU_TASK'))
    alter table ACT_RU_TASK
        add constraint ACT_FK_TASK_PROCDEF
        foreign key (PROC_DEF_ID_)
        references ACT_RE_PROCDEF (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_VAR_EXE' and parent_object_id = object_id('ACT_RU_VARIABLE'))
    alter table ACT_RU_VARIABLE
        add constraint ACT_FK_VAR_EXE
        foreign key (EXECUTION_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_VAR_PROCINST' and parent_object_id = object_id('ACT_RU_VARIABLE'))
    alter table ACT_RU_VARIABLE
        add constraint ACT_FK_VAR_PROCINST
        foreign key (PROC_INST_ID_)
        references ACT_RU_EXECUTION (ID_);
go

if not exists (select 1 from sys.foreign_keys where name = 'ACT_FK_VAR_BYTEARRAY' and parent_object_id = object_id('ACT_RU_VARIABLE'))
    alter table ACT_RU_VARIABLE
        add constraint ACT_FK_VAR_BYTEARRAY
        foreign key (BYTEARRAY_ID_)
        references ACT_GE_BYTEARRAY (ID_);
go


-- ─────────────────────────── ①-3 引擎初值（ACT_GE_PROPERTY：仅在缺失时插入） ───────────────────────────

if not exists (select 1 from ACT_GE_PROPERTY where NAME_ = 'schema.version')
    insert into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('schema.version', '8.1.4', '1');
go
if not exists (select 1 from ACT_GE_PROPERTY where NAME_ = 'schema.history')
    insert into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('schema.history', 'create(8.1.4)', '1');
go
if not exists (select 1 from ACT_GE_PROPERTY where NAME_ = 'next.dbid')
    insert into ACT_GE_PROPERTY (NAME_, VALUE_, REV_) values ('next.dbid', '1', '1');
go


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

if object_id(N'instances', N'U') is null
    create table instances (
    tenant_id bigint not null,
    apply_id bigint not null,
    apply_time datetime not null,
    workflow_type_id nvarchar(64) not null,
    workflow_version nvarchar(50),
    [describe] nvarchar(200) not null,
    apply_emp_id nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_id bigint not null,
    business_dept_id nvarchar(50),
    status_id nvarchar(50) not null,
    newest_flag smallint not null constraint df_instances_newest_flag default (1),
    workflow_instance_id nvarchar(64),
    last_audited_time datetime,
    create_emp_id nvarchar(50) not null,
    create_time datetime not null,
    current_step nvarchar(200),
    assign_emp nvarchar(200),
    modify_emp_id nvarchar(50),
    modify_time datetime,
    deleted_flag tinyint not null constraint df_instances_deleted_flag default (0),
    original_business_id bigint,
    flow_file_id bigint,
    constraint pk_instances primary key clustered (apply_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_instances_workflow_instance_id' and object_id = object_id('instances'))
create index ix_instances_workflow_instance_id on instances (workflow_instance_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_instances_business_id' and object_id = object_id('instances'))
create index ix_instances_business_id on instances (business_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_instances_status_id' and object_id = object_id('instances'))
create index ix_instances_status_id on instances (tenant_id, status_id);
go
if object_id(N'instance_current_task', N'U') is null
    create table instance_current_task (
    tenant_id bigint not null,
    step_id bigint not null,
    apply_id bigint,
    task_id nvarchar(64),
    task_node nvarchar(50),
    task_owner nvarchar(200),
    assign_emp_id nvarchar(200),
    form_key nvarchar(100),
    create_time datetime not null,
    constraint pk_instance_current_task primary key clustered (step_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_instance_current_task_apply_id' and object_id = object_id('instance_current_task'))
create index ix_instance_current_task_apply_id on instance_current_task (apply_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_instance_current_task_task_id' and object_id = object_id('instance_current_task'))
create index ix_instance_current_task_task_id on instance_current_task (task_id);
go
if object_id(N'deal_history', N'U') is null
    create table deal_history (
    tenant_id bigint not null,
    deal_history_id bigint not null,
    business_id bigint,
    apply_id bigint,
    task_node nvarchar(50),
    deal_emp_id nvarchar(50),
    deal_time datetime,
    deal_result nvarchar(50),
    deal_comment nvarchar(2000),
    form_key nvarchar(200),
    deal_emp_name nvarchar(50),
    deal_domain_account nvarchar(50),
    constraint pk_deal_history primary key clustered (deal_history_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_deal_history_apply_id' and object_id = object_id('deal_history'))
create index ix_deal_history_apply_id on deal_history (apply_id);
go
if not exists (select 1 from sys.indexes where name = 'ix_deal_history_deal_emp_id' and object_id = object_id('deal_history'))
create index ix_deal_history_deal_emp_id on deal_history (tenant_id, deal_emp_id);
go
if object_id(N'workflow_type', N'U') is null
    create table workflow_type (
    tenant_id bigint not null,
    id nvarchar(64) not null,
    name nvarchar(50) not null,
    [describe] nvarchar(200) not null,
    view_url nvarchar(500) not null,
    approve_url nvarchar(500) not null,
    sort_order int not null constraint df_workflow_type_sort_order default (0),
    enabled tinyint not null constraint df_workflow_type_enabled default (1),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_workflow_type primary key clustered (id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_workflow_type_sort' and object_id = object_id('workflow_type'))
create index ix_workflow_type_sort on workflow_type (tenant_id, sort_order);
go
if not exists (select 1 from workflow_type where id = '02830358-46ef-40e5-b3b9-4d5c4eb23f9f')
    insert into workflow_type (tenant_id, id, name, [describe], view_url, approve_url, sort_order, enabled, create_time)
    values (1, '02830358-46ef-40e5-b3b9-4d5c4eb23f9f', 'order_process', N'订单审批流程', '/bill.html?process=order_process&businessId={businessId}', '/approve.html?process=order_process&taskId={taskId}', 1, 1, getutcdate());
go
if not exists (select 1 from workflow_type where id = '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7')
    insert into workflow_type (tenant_id, id, name, [describe], view_url, approve_url, sort_order, enabled, create_time)
    values (1, '3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7', 'leave_process', N'请假申请流程', '/bill.html?process=leave_process&businessId={businessId}', '/approve.html?process=leave_process&taskId={taskId}', 2, 1, getutcdate());
go

if object_id(N'bpmn_model', N'U') is null
    create table bpmn_model (
    id nvarchar(64) not null,
    tenant_id bigint not null,
    name nvarchar(200) not null,
    process_key nvarchar(50) not null,
    description nvarchar(500),
    bpmn_xml nvarchar(max) not null,
    svg nvarchar(max),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_bpmn_model primary key clustered (id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ux_bpmn_model_key' and object_id = object_id('bpmn_model'))
create unique index ux_bpmn_model_key on bpmn_model (tenant_id, process_key);
go
if not exists (select 1 from sys.indexes where name = 'ix_bpmn_model_modify' and object_id = object_id('bpmn_model'))
create index ix_bpmn_model_modify on bpmn_model (tenant_id, modify_time);
go

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

if object_id(N'leave_bill', N'U') is null
    create table leave_bill (
    tenant_id bigint not null,
    business_id bigint not null,
    apply_user nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_dept_id nvarchar(50),
    days smallint,
    reason nvarchar(500),
    ext_params nvarchar(2000),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_leave_bill primary key clustered (business_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_leave_bill_apply_user' and object_id = object_id('leave_bill'))
create index ix_leave_bill_apply_user on leave_bill (tenant_id, apply_user);
go
if object_id(N'order_bill', N'U') is null
    create table order_bill (
    tenant_id bigint not null,
    business_id bigint not null,
    product_name nvarchar(200),
    amount decimal(18,2),
    remark nvarchar(500),
    status smallint not null constraint df_order_bill_status default (1),
    approve_comment nvarchar(500),
    apply_user nvarchar(50) not null,
    apply_dept_id nvarchar(50) not null,
    business_dept_id nvarchar(50),
    process_instance_id nvarchar(64),
    create_time datetime not null,
    modify_time datetime,
    deleted_flag tinyint not null constraint df_order_bill_deleted_flag default (0),
    constraint pk_order_bill primary key clustered (business_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_order_bill_apply_user' and object_id = object_id('order_bill'))
create index ix_order_bill_apply_user on order_bill (tenant_id, apply_user);
go
if not exists (select 1 from sys.indexes where name = 'ix_order_bill_status' and object_id = object_id('order_bill'))
create index ix_order_bill_status on order_bill (tenant_id, status);
go
if object_id(N'workflow_parameters', N'U') is null
    create table workflow_parameters (
    tenant_id bigint not null,
    workflow_instance_id nvarchar(64) not null,
    param_name nvarchar(100) not null,
    param_value nvarchar(max),
    create_time datetime not null,
    modify_time datetime,
    constraint pk_workflow_parameters primary key clustered (workflow_instance_id, param_name)
    );
go

if not exists (select 1 from sys.indexes where name = 'ix_workflow_parameters_instance' and object_id = object_id('workflow_parameters'))
create index ix_workflow_parameters_instance on workflow_parameters (workflow_instance_id);
go
if object_id(N'sys_dept', N'U') is null
    create table sys_dept (
    dept_id bigint not null,
    tenant_id bigint not null,
    dept_name nvarchar(100) not null,
    parent_id bigint,
    sort_order int not null constraint df_sys_dept_sort_order default (0),
    create_time datetime not null,
    constraint pk_sys_dept primary key clustered (dept_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'idx_sys_dept_tenant' and object_id = object_id('sys_dept'))
create index idx_sys_dept_tenant on sys_dept (tenant_id, parent_id);
go
if object_id(N'sys_emp', N'U') is null
    create table sys_emp (
    emp_id bigint not null,
    tenant_id bigint not null,
    emp_name nvarchar(50) not null,
    domain_account nvarchar(50) not null,
    dept_id bigint not null,
    create_time datetime not null,
    constraint pk_sys_emp primary key clustered (emp_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_account' and object_id = object_id('sys_emp'))
create unique index uk_sys_emp_account on sys_emp (tenant_id, domain_account);
go
if not exists (select 1 from sys.indexes where name = 'idx_sys_emp_dept' and object_id = object_id('sys_emp'))
create index idx_sys_emp_dept on sys_emp (dept_id);
go
if object_id(N'sys_emp_group', N'U') is null
    create table sys_emp_group (
    group_id nvarchar(50) not null,
    tenant_id bigint not null,
    group_name nvarchar(100) not null,
    sort_order int not null constraint df_sys_emp_group_sort_order default (0),
    create_time datetime not null,
    constraint pk_sys_emp_group primary key clustered (group_id)
    );
go

if object_id(N'sys_emp_group_member', N'U') is null
    create table sys_emp_group_member (
    member_id bigint not null,
    tenant_id bigint not null,
    group_id nvarchar(50) not null,
    emp_id bigint not null,
    create_time datetime not null,
    constraint pk_sys_emp_group_member primary key clustered (member_id)
    );
go

if not exists (select 1 from sys.indexes where name = 'uk_sys_emp_group_member' and object_id = object_id('sys_emp_group_member'))
create unique index uk_sys_emp_group_member on sys_emp_group_member (group_id, emp_id);
go
if not exists (select 1 from sys.indexes where name = 'idx_sys_emp_group_member_emp' and object_id = object_id('sys_emp_group_member'))
create index idx_sys_emp_group_member_emp on sys_emp_group_member (emp_id);
go

-- ─────────────────────────── ③-1 组织架构种子数据 ───────────────────────────

if not exists (select 1 from sys_dept where dept_id = 101)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (101, 1, N'信息技术部', null, 1, getutcdate());
go
if not exists (select 1 from sys_dept where dept_id = 102)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (102, 1, N'成本管理部', null, 2, getutcdate());
go
if not exists (select 1 from sys_dept where dept_id = 103)
    insert into sys_dept (dept_id, tenant_id, dept_name, parent_id, sort_order, create_time)
    values (103, 1, N'综合管理部', null, 3, getutcdate());
go

if not exists (select 1 from sys_emp where emp_id = 1001)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1001, 1, N'王小爱', 'alice', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1002)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1002, 1, N'李小波', 'bob', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1003)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1003, 1, N'赵志强', 'leader1', 101, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1004)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1004, 1, N'钱有为', 'leader2', 102, getutcdate());
go
if not exists (select 1 from sys_emp where emp_id = 1005)
    insert into sys_emp (emp_id, tenant_id, emp_name, domain_account, dept_id, create_time)
    values (1005, 1, N'孙建国', 'manager1', 103, getutcdate());
go

if not exists (select 1 from sys_emp_group where group_id = 'leaders')
    insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
    values ('leaders', 1, N'组长', 1, getutcdate());
go
if not exists (select 1 from sys_emp_group where group_id = 'managers')
    insert into sys_emp_group (group_id, tenant_id, group_name, sort_order, create_time)
    values ('managers', 1, N'经理', 2, getutcdate());
go

if not exists (select 1 from sys_emp_group_member where group_id = 'leaders' and emp_id = 1003)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (1, 1, 'leaders', 1003, getutcdate());
go
if not exists (select 1 from sys_emp_group_member where group_id = 'leaders' and emp_id = 1004)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (2, 1, 'leaders', 1004, getutcdate());
go
if not exists (select 1 from sys_emp_group_member where group_id = 'managers' and emp_id = 1005)
    insert into sys_emp_group_member (member_id, tenant_id, group_id, emp_id, create_time)
    values (3, 1, 'managers', 1005, getutcdate());
go


-- ─────────────────────────── ④ 运行期查询索引（性能增强，不影响行内容） ───────────────────────────

--  背景：上游 Activiti 的索引清单里，运行期（ACT_RU_*）只有少数几个；
--        本引擎高频执行的查询（执行树按流程实例/父执行、任务按办理人/流程实例、
--        变量按执行、关系按流程实例）在 MySQL 上会全表扫描。
--
--  幂等：MySQL 用 information_schema 判重后动态执行，SQL Server 用 sys.indexes 判重。

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXEC_PROC_INST_PARENT' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_PROC_INST_PARENT on ACT_RU_EXECUTION (PROC_INST_ID_, PARENT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXEC_PARENT' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_PARENT on ACT_RU_EXECUTION (PARENT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXEC_ACT_PROCINST_ACTIVE' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_ACT_PROCINST_ACTIVE on ACT_RU_EXECUTION (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXEC_PROC_INST_ID' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_PROC_INST_ID on ACT_RU_EXECUTION (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXECUTION_PROC' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXECUTION_PROC on ACT_RU_EXECUTION (PROC_DEF_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXECUTION_SUPER' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXECUTION_SUPER on ACT_RU_EXECUTION (SUPER_EXEC_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_EXECUTION_IDANDREV' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXECUTION_IDANDREV on ACT_RU_EXECUTION (ID_, REV_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_PROCINST' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_PROCINST on ACT_RU_TASK (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_EXEC' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_EXEC on ACT_RU_TASK (EXECUTION_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_PROC_DEF_ID' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_PROC_DEF_ID on ACT_RU_TASK (PROC_DEF_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_ASSIGNEE' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_ASSIGNEE on ACT_RU_TASK (ASSIGNEE_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_VARIABLE_EXEC_NAME' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_EXEC_NAME on ACT_RU_VARIABLE (EXECUTION_ID_, NAME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_VARIABLE_EXEC' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_EXEC on ACT_RU_VARIABLE (EXECUTION_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_VARIABLE_PROCINST' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_PROCINST on ACT_RU_VARIABLE (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_VARIABLE_BA' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_BA on ACT_RU_VARIABLE (BYTEARRAY_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDENT_LNK_PROCINST' and object_id = object_id('ACT_RU_IDENTITYLINK'))
create index ACT_IDX_IDENT_LNK_PROCINST on ACT_RU_IDENTITYLINK (PROC_INST_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_ASSIGNEE_CREATE' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_ASSIGNEE_CREATE on ACT_RU_TASK (ASSIGNEE_, CREATE_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_PROCINST_DEFKEY' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_PROCINST_DEFKEY on ACT_RU_TASK (PROC_INST_ID_, TASK_DEF_KEY_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_TENANT' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_TENANT on ACT_RU_TASK (TENANT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_TASK_DUEDATE' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_DUEDATE on ACT_RU_TASK (DUE_DATE_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_TASK_ASSIGNEE_END' and object_id = object_id('ACT_HI_TASKINST'))
create index ACT_IDX_HI_TASK_ASSIGNEE_END on ACT_HI_TASKINST (ASSIGNEE_, END_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_TASK_PROCINST_END' and object_id = object_id('ACT_HI_TASKINST'))
create index ACT_IDX_HI_TASK_PROCINST_END on ACT_HI_TASKINST (PROC_INST_ID_, END_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_TASK_DEFKEY' and object_id = object_id('ACT_HI_TASKINST'))
create index ACT_IDX_HI_TASK_DEFKEY on ACT_HI_TASKINST (TASK_DEF_KEY_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_HI_PROC_TENANT_END' and object_id = object_id('ACT_HI_PROCINST'))
create index ACT_IDX_HI_PROC_TENANT_END on ACT_HI_PROCINST (TENANT_ID_, END_TIME_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_BYTEARRAY_DEPLOYMENT' and object_id = object_id('ACT_GE_BYTEARRAY'))
create index ACT_IDX_BYTEARRAY_DEPLOYMENT on ACT_GE_BYTEARRAY (DEPLOYMENT_ID_);
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_DEPLOYMENT_TIME' and object_id = object_id('ACT_RE_DEPLOYMENT'))
create index ACT_IDX_DEPLOYMENT_TIME on ACT_RE_DEPLOYMENT (DEPLOY_TIME_);
go


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

if object_id(N'ACT_IDEMPOTENCY', N'U') is null
    create table ACT_IDEMPOTENCY (
    IDEMPOTENCY_KEY_ nvarchar(120) not null,
    REQUEST_HASH_ nvarchar(64) not null,
    STATUS_CODE_ int,
    CONTENT_TYPE_ nvarchar(200),
    RESPONSE_BODY_ nvarchar(max),
    CREATED_AT_ datetime2(3) not null,
    EXPIRES_AT_ datetime2(3) not null,
    constraint pk_ACT_IDEMPOTENCY primary key clustered (IDEMPOTENCY_KEY_)
    );
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDEMP_EXPIRES' and object_id = object_id('ACT_IDEMPOTENCY'))
create index ACT_IDX_IDEMP_EXPIRES on ACT_IDEMPOTENCY (EXPIRES_AT_);
go
if object_id(N'ACT_RU_TIMER_JOB', N'U') is null
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
    );
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
if object_id(N'ACT_HI_MIGRATION', N'U') is null
    create table ACT_HI_MIGRATION (
    ID_ nvarchar(64) not null,
    PROC_INST_ID_ nvarchar(64) not null,
    SOURCE_PROC_DEF_ID_ nvarchar(255) not null,
    TARGET_PROC_DEF_ID_ nvarchar(255) not null,
    ACTIVITY_MAPPINGS_ nvarchar(4000),
    REASON_ nvarchar(400),
    USER_ID_ nvarchar(255),
    MIGRATED_AT_ datetime not null,
    primary key (ID_)
    );
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_MIGRATION_PROCINST' and object_id = object_id('ACT_HI_MIGRATION'))
create index ACT_IDX_MIGRATION_PROCINST on ACT_HI_MIGRATION (PROC_INST_ID_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_MIGRATION_PROCINST_TIME' and object_id = object_id('ACT_HI_MIGRATION'))
create index ACT_IDX_MIGRATION_PROCINST_TIME on ACT_HI_MIGRATION (PROC_INST_ID_, MIGRATED_AT_);
go
if object_id(N'bpmn_model_history', N'U') is null
    create table bpmn_model_history (
    id nvarchar(64) not null,
    tenant_id bigint not null,
    model_id nvarchar(64) not null,
    revision int not null,
    name nvarchar(200) not null,
    process_key nvarchar(50) not null,
    description nvarchar(500),
    bpmn_xml nvarchar(max) not null,
    svg nvarchar(max),
    action nvarchar(32) not null,
    operator nvarchar(255),
    created_at datetime not null,
    constraint pk_bpmn_model_history primary key clustered (id)
    );
go

if not exists (select 1 from sys.indexes where name = 'ux_bpmn_model_history_rev' and object_id = object_id('bpmn_model_history'))
create unique index ux_bpmn_model_history_rev on bpmn_model_history (model_id, revision);
go
if not exists (select 1 from sys.indexes where name = 'ix_bpmn_model_history_tenant' and object_id = object_id('bpmn_model_history'))
create index ix_bpmn_model_history_tenant on bpmn_model_history (tenant_id, model_id);
go
if object_id(N'admin_audit_log', N'U') is null
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
    );
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
if object_id(N'admin_history_archive', N'U') is null
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
    );
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
if object_id(N'ACT_RU_JOB', N'U') is null
    create table ACT_RU_JOB (
    ID_ nvarchar(64) not null,
    REV_ int not null default 1,
    EXECUTION_ID_ nvarchar(64) not null,
    PROCESS_INSTANCE_ID_ nvarchar(64) not null,
    PROC_DEF_ID_ nvarchar(255),
    ACTIVITY_ID_ nvarchar(255) not null,
    PHASE_ nvarchar(16) not null,
    DUEDATE_ datetime not null,
    TENANT_ID_ nvarchar(64) not null default '',
    CREATE_TIME_ datetime not null,
    RETRIES_ int not null default 3,
    DEAD_LETTER_ tinyint not null default 0,
    EXCEPTION_MSG_ nvarchar(1000),
    LAST_FAILURE_ datetime,
    primary key (ID_)
    );
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_JOB_DUE' and object_id = object_id('ACT_RU_JOB'))
create index ACT_IDX_JOB_DUE on ACT_RU_JOB (DUEDATE_, DEAD_LETTER_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_JOB_EXECUTION' and object_id = object_id('ACT_RU_JOB'))
create index ACT_IDX_JOB_EXECUTION on ACT_RU_JOB (EXECUTION_ID_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_JOB_PROC_INST' and object_id = object_id('ACT_RU_JOB'))
create index ACT_IDX_JOB_PROC_INST on ACT_RU_JOB (PROCESS_INSTANCE_ID_);
go
if object_id(N'ACT_EVT_OUTBOX', N'U') is null
    create table ACT_EVT_OUTBOX (
    ID_ nvarchar(64) not null,
    EVENT_TYPE_ nvarchar(64) not null,
    ENTITY_ID_ nvarchar(64),
    PROCESS_INSTANCE_ID_ nvarchar(64),
    PROC_DEF_ID_ nvarchar(255),
    EXECUTION_ID_ nvarchar(64),
    ACTIVITY_ID_ nvarchar(255),
    TENANT_ID_ nvarchar(64) not null default '',
    PAYLOAD_ nvarchar(max),
    CREATE_TIME_ datetime not null,
    PUBLISHED_ tinyint not null default 0,
    PUBLISH_ATTEMPTS_ int not null default 0,
    DEAD_LETTER_ tinyint not null default 0,
    LAST_ERROR_ nvarchar(1000),
    NEXT_ATTEMPT_ datetime,
    PUBLISHED_TIME_ datetime,
    primary key (ID_)
    );
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_OUTBOX_PENDING' and object_id = object_id('ACT_EVT_OUTBOX'))
create index ACT_IDX_OUTBOX_PENDING on ACT_EVT_OUTBOX (PUBLISHED_, DEAD_LETTER_, CREATE_TIME_);
go
if not exists (select 1 from sys.indexes where name = 'ACT_IDX_OUTBOX_PROC_INST' and object_id = object_id('ACT_EVT_OUTBOX'))
create index ACT_IDX_OUTBOX_PROC_INST on ACT_EVT_OUTBOX (PROCESS_INSTANCE_ID_);
go

-- ─────────────────────────── ⑥ 老库兼容补齐（幂等；新库执行为空操作） ───────────────────────────

--  这一节只为「用旧版脚本建过库」的环境准备：
--    · 模型大文本列仍是 text（64KB）→ 扩容到 mediumtext；
--    · 定时作业表缺重试 / 死信列 → 补列。
--  新库在上面已经把表建成最终形态，这里全部是空操作。

-- bpmn_xml / svg 在 SQL Server 已是 nvarchar(max)，无需扩容。

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


-- ─────────────────────────── ⑦ 表与列注释（扩展属性 MS_Description，已存在则跳过） ───────────────────────────

if object_id('ACT_GE_PROPERTY', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_GE_PROPERTY') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'通用属性表：存放 schema 版本、schema 历史与 ID 区块游标等全局键值对',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_GE_PROPERTY';
go

if object_id('ACT_GE_BYTEARRAY', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_GE_BYTEARRAY') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'二进制数据表：存放部署的流程定义资源（BPMN XML）等字节内容',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_GE_BYTEARRAY';
go

if object_id('ACT_RE_DEPLOYMENT', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RE_DEPLOYMENT') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'部署信息表：每次流程部署产生一条记录',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RE_DEPLOYMENT';
go

if object_id('ACT_RE_PROCDEF', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RE_PROCDEF') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义表：部署解析后的流程定义（KEY_ + VERSION_ + TENANT_ID_ 唯一）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RE_PROCDEF';
go

if object_id('ACT_RU_EXECUTION', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_EXECUTION') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时执行实例表：执行树，引擎唯一的运行态（流程实例 / 并发分支 / 作用域）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_EXECUTION';
go

if object_id('ACT_RU_TASK', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_TASK') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时任务表：当前待办的用户任务',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_TASK';
go

if object_id('ACT_RU_IDENTITYLINK', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_IDENTITYLINK') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时身份关联表：任务的用户 / 组关联（办理人、候选人与候选组）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_IDENTITYLINK';
go

if object_id('ACT_RU_VARIABLE', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_VARIABLE') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时变量表：流程实例、执行与任务级变量',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_VARIABLE';
go

if object_id('ACT_HI_PROCINST', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_PROCINST') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史流程实例表：记录已启动过的流程实例',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_PROCINST';
go

if object_id('ACT_HI_ACTINST', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_ACTINST') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史活动实例表：流程图着色数据源',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_ACTINST';
go

if object_id('ACT_HI_TASKINST', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_TASKINST') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史任务实例表：记录已产生过的用户任务',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_TASKINST';
go

if object_id('ACT_HI_VARINST', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_VARINST') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史变量实例表：变量在流程结束时的最终值',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_VARINST';
go

if object_id('ACT_HI_IDENTITYLINK', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_IDENTITYLINK') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史身份关联表：历史任务的用户 / 组关联',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_IDENTITYLINK';
go

if object_id('instances', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('instances') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例表（订单审批/请假审批流程共用；workflow_type_id=流程信息枚举的流程类型id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'instances';
go

if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'apply_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'apply_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'apply_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'apply_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'apply_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'apply_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'workflow_type_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'workflow_type_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型id（业务侧流程信息枚举 Id：订单=02830358-46ef-40e5-b3b9-4d5c4eb23f9f，请假=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'workflow_type_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'workflow_version')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'workflow_version')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程版本',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'workflow_version';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'describe')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'describe')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例描述',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'describe';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'apply_emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'apply_emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'apply_emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'apply_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'apply_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人部门',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'apply_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'business_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'business_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应的业务id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'business_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'business_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'business_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'business_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'status_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'status_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例状态 审批中、已完成',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'status_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'newest_flag')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'newest_flag')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'是否是最新流程实例',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'newest_flag';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'workflow_instance_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'workflow_instance_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例id(对应于流程引擎中的id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'workflow_instance_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'last_audited_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'last_audited_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后审批完成时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'last_audited_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'create_emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'create_emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'create_emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'current_step')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'current_step')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'current_step';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'assign_emp')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'assign_emp')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前处理人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'assign_emp';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'modify_emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'modify_emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'modify_emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'deleted_flag')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'deleted_flag')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'删除标志',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'deleted_flag';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'original_business_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'original_business_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'原始业务ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'original_business_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instances') and name = N'flow_file_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instances') and c.name = N'flow_file_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图文件ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instances',
        @level2type = N'COLUMN', @level2name = N'flow_file_id';
go

if object_id('instance_current_task', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('instance_current_task') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批步骤及审批人（订单审批/请假审批流程共用）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'instance_current_task';
go

if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'step_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'step_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'step_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'apply_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'apply_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'apply_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'task_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'task_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'task_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'task_node')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'task_node')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前步骤名称',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'task_node';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'task_owner')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'task_owner')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'待办项归属人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'task_owner';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'assign_emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'assign_emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'当前审批者',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'assign_emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'form_key')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'form_key')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'form_key';
go
if exists (select 1 from sys.columns
    where object_id = object_id('instance_current_task') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('instance_current_task') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'instance_current_task',
        @level2type = N'COLUMN', @level2name = N'create_time';
go

if object_id('deal_history', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('deal_history') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批历史记录（订单审批/请假审批流程共用）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'deal_history';
go

if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_history_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_history_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'数据行id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_history_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'business_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'business_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'对应业务表单的ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'business_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'apply_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'apply_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'apply_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'task_node')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'task_node')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'步骤名称',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'task_node';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批者员工id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_result')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_result')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批结果',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_result';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_comment')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_comment')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批说明',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_comment';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'form_key')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'form_key')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程步骤对应的环节配置',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'form_key';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_emp_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_emp_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人姓名',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_emp_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('deal_history') and name = N'deal_domain_account')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('deal_history') and c.name = N'deal_domain_account')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审批人域帐号',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'deal_history',
        @level2type = N'COLUMN', @level2name = N'deal_domain_account';
go

if object_id('workflow_type', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('workflow_type') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型表（流程信息枚举 ProcessInfoEnum 的数据源；系统启动时加载）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'workflow_type';
go

if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程类型id（流程信息枚举 Id，如 02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程名称（流程 key，= BPMN process/@id，如 order_process）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'describe')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'describe')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程描述',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'describe';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'view_url')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'view_url')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务单查看页地址（{businessId}/{processInstanceId} 占位符）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'view_url';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'approve_url')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'approve_url')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程审批页地址（{taskId}/{processInstanceId} 占位符）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'approve_url';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'sort_order')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'sort_order')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'展示顺序（小在前）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'sort_order';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'enabled')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'enabled')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'是否启用（0=停用，加载时跳过）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'enabled';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_type') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_type') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_type',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go

if object_id('bpmn_model', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('bpmn_model') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程模型表（管理端流程设计器的模型落点；BPMN XML 的编辑源，部署后产生 ACT_RE_PROCDEF 版本）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'bpmn_model';
go

if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'模型 ID（设计器 modelId，GUID 字符串）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程名称（BPMN process/@name）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'process_key')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'process_key')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程 key（= BPMN process/@id，如 order_process）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'process_key';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'description')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'description')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程描述',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'description';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'bpmn_xml')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'bpmn_xml')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'BPMN 2.0 XML 内容',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'bpmn_xml';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'svg')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'svg')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图 SVG 预览（前端导出，可选）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'svg';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go

if object_id('leave_bill', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('leave_bill') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假单（业务表单；days/reason 对应请假流程参数；流程类型id=3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'leave_bill';
go

if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'business_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'business_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务id（请假单号，由业务侧生成）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'business_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'apply_user')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'apply_user')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'apply_user';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'apply_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'apply_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请部门',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'apply_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'business_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'business_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'business_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'days')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'days')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假天数（对应流程参数 days）；NULL = 还没填',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'days';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'reason')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'reason')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'请假事由（对应流程参数 reason）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'reason';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'ext_params')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'ext_params')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'扩展流程参数（JSON 对象）：除 days / reason 外的键值对',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'ext_params';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('leave_bill') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('leave_bill') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'leave_bill',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go

if object_id('order_bill', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('order_bill') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'订单表（业务表单；状态随订单审批流程回写：1=审批中，-1=审批拒绝，100=审批通过；流程类型id=02830358-46ef-40e5-b3b9-4d5c4eb23f9f）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'order_bill';
go

if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'business_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'business_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务id（订单单号，由业务侧生成）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'business_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'product_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'product_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'订购产品（对应流程参数 product_name）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'product_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'amount')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'amount')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'订单金额（对应流程参数 amount）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'amount';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'remark')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'remark')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'备注（对应流程参数 remark）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'remark';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'status')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'status')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'状态：1=审批中，-1=审批拒绝，100=审批通过',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'status';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'approve_comment')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'approve_comment')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最终审批意见（审批结束时随状态一起写回）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'approve_comment';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'apply_user')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'apply_user')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'apply_user';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'apply_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'apply_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'申请部门',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'apply_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'business_dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'business_dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务部门',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'business_dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'process_instance_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'process_instance_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id（提交申请后回填，用于反查审批进度）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'process_instance_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('order_bill') and name = N'deleted_flag')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('order_bill') and c.name = N'deleted_flag')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'伪删除标志（0 正常 / 1 已删除；公共数据操作接口 DeleteAsync 置位，列表与明细自动过滤）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'order_bill',
        @level2type = N'COLUMN', @level2name = N'deleted_flag';
go

if object_id('workflow_parameters', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('workflow_parameters') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程参数表（订单/请假审批流程共用；「实例 × 参数名」键值对）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'workflow_parameters';
go

if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'workflow_instance_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'workflow_instance_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例id（对应 instances.workflow_instance_id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'workflow_instance_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'param_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'param_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'参数名（如 days / reason / product_name）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'param_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'param_value')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'param_value')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'参数值（null = 显式置空）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'param_value';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'create_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('workflow_parameters') and name = N'modify_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('workflow_parameters') and c.name = N'modify_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后修改时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'workflow_parameters',
        @level2type = N'COLUMN', @level2name = N'modify_time';
go

if object_id('sys_dept', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('sys_dept') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门信息表',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'sys_dept';
go

if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'dept_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'dept_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'部门名称',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'dept_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'parent_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'parent_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'上级部门ID（NULL=顶级）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'parent_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'sort_order')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'sort_order')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'排序号',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'sort_order';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_dept') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_dept') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_dept',
        @level2type = N'COLUMN', @level2name = N'create_time';
go

if object_id('sys_emp', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('sys_emp') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工信息表',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'sys_emp';
go

if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'emp_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'emp_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工姓名',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'emp_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'domain_account')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'domain_account')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'域账号（审批人/申请人在流程里的用户ID）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'domain_account';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'dept_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'dept_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'所属部门ID（sys_dept.dept_id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'dept_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp',
        @level2type = N'COLUMN', @level2name = N'create_time';
go

if object_id('sys_emp_group', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('sys_emp_group') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'用户组表（审批组）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'sys_emp_group';
go

if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group') and name = N'group_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group') and c.name = N'group_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'组标识（流程定义 candidateGroups 引用的就是它）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
        @level2type = N'COLUMN', @level2name = N'group_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group') and name = N'group_name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group') and c.name = N'group_name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'组名称（如：组长 / 经理）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
        @level2type = N'COLUMN', @level2name = N'group_name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group') and name = N'sort_order')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group') and c.name = N'sort_order')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'排序号',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
        @level2type = N'COLUMN', @level2name = N'sort_order';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group',
        @level2type = N'COLUMN', @level2name = N'create_time';
go

if object_id('sys_emp_group_member', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('sys_emp_group_member') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'用户组成员表',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'sys_emp_group_member';
go

if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group_member') and name = N'member_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group_member') and c.name = N'member_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'成员行ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
        @level2type = N'COLUMN', @level2name = N'member_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group_member') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group_member') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户ID',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group_member') and name = N'group_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group_member') and c.name = N'group_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'组标识（sys_emp_group.group_id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
        @level2type = N'COLUMN', @level2name = N'group_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group_member') and name = N'emp_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group_member') and c.name = N'emp_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'员工ID（sys_emp.emp_id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
        @level2type = N'COLUMN', @level2name = N'emp_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('sys_emp_group_member') and name = N'create_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('sys_emp_group_member') and c.name = N'create_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'sys_emp_group_member',
        @level2type = N'COLUMN', @level2name = N'create_time';
go

if object_id('ACT_RU_TIMER_JOB', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_TIMER_JOB') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'定时作业表：边界定时器（一次性）到点后触发',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB';
go

if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_TIMER_JOB') and name = N'RETRIES_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_TIMER_JOB') and c.name = N'RETRIES_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'剩余重试次数；0 表示已进死信',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
        @level2type = N'COLUMN', @level2name = N'RETRIES_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_TIMER_JOB') and name = N'DEAD_LETTER_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_TIMER_JOB') and c.name = N'DEAD_LETTER_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=已进死信，不再参与到期轮询',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
        @level2type = N'COLUMN', @level2name = N'DEAD_LETTER_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_TIMER_JOB') and name = N'EXCEPTION_MSG_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_TIMER_JOB') and c.name = N'EXCEPTION_MSG_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败原因',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
        @level2type = N'COLUMN', @level2name = N'EXCEPTION_MSG_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_TIMER_JOB') and name = N'LAST_FAILURE_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_TIMER_JOB') and c.name = N'LAST_FAILURE_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_TIMER_JOB',
        @level2type = N'COLUMN', @level2name = N'LAST_FAILURE_';
go

if object_id('ACT_HI_MIGRATION', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_HI_MIGRATION') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例迁移审计表：记录源/目标定义、活动映射、原因与操作人',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_HI_MIGRATION';
go

if object_id('bpmn_model_history', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('bpmn_model_history') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程模型版本历史：每次保存/回滚追加一版，支持回滚与变更追溯',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'bpmn_model_history';
go

if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史版本 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'model_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'model_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'模型 id（bpmn_model.id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'model_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'revision')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'revision')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'版本号（同一模型内递增，从 1 开始）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'revision';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'name')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'name')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程名称',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'name';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'process_key')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'process_key')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程 key',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'process_key';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'description')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'description')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程描述',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'description';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'bpmn_xml')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'bpmn_xml')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'BPMN XML 快照',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'bpmn_xml';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'svg')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'svg')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图 SVG 快照',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'svg';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'action')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'action')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作：save / rollback',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'action';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'operator')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'operator')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'操作人（请求身份）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'operator';
go
if exists (select 1 from sys.columns
    where object_id = object_id('bpmn_model_history') and name = N'created_at')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('bpmn_model_history') and c.name = N'created_at')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'写入时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
        @level2type = N'COLUMN', @level2name = N'created_at';
go

if object_id('admin_audit_log', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('admin_audit_log') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'管理动作审计：转办 / 跳转 / 变量直写等高风险动作的操作人、目标与结果',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'admin_audit_log';
go

if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'审计记录 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户（与 ACT_* 的 TENANT_ID_ 同口径）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'actor')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'actor')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'操作人（JWT sub / X-User-Id）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'actor';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'auth_mode')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'auth_mode')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'鉴权方式：jwt / api_key / none',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'auth_mode';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'action')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'action')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作标识，如 task.reassign / instance.jump / variable.write',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'action';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'target_type')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'target_type')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'目标类型：task / process_instance / variable / history',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'target_type';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'target_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'target_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'目标 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'target_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'process_instance_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'process_instance_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'关联流程实例 id（便于按实例追溯）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'process_instance_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'detail')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'detail')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作明细（JSON；不记录变量值）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'detail';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'success')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'success')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=成功，0=失败',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'success';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'error')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'error')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'失败原因',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'error';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_audit_log') and name = N'created_at')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_audit_log') and c.name = N'created_at')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'写入时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_audit_log',
        @level2type = N'COLUMN', @level2name = N'created_at';
go

if object_id('admin_history_archive', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('admin_history_archive') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史归档：已结束实例的 ACT_HI_* 行快照（JSON），原历史行在归档后删除',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'admin_history_archive';
go

if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档行 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'process_instance_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'process_instance_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'process_instance_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'tenant_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'tenant_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'tenant_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'process_definition_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'process_definition_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义 id（启动时版本）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'process_definition_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'business_key')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'business_key')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'业务主键',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'business_key';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'start_user_id')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'start_user_id')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'发起人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'start_user_id';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'start_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'start_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'实例开始时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'start_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'end_time')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'end_time')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'实例结束时间',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'end_time';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'archived_at')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'archived_at')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'archived_at';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'archived_by')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'archived_by')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'归档操作人',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'archived_by';
go
if exists (select 1 from sys.columns
    where object_id = object_id('admin_history_archive') and name = N'payload')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('admin_history_archive') and c.name = N'payload')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史行快照（processInstance / activities / tasks / variables / identityLinks）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'admin_history_archive',
        @level2type = N'COLUMN', @level2name = N'payload';
go

if object_id('ACT_RU_JOB', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_RU_JOB') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'异步作业表：asyncBefore / asyncAfter 作业（独占，一个作业 = 一个命令）',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_RU_JOB';
go

if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'作业 id（IIdGenerator 分配）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'REV_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'REV_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'乐观锁版本',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'REV_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'EXECUTION_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'EXECUTION_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'执行 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'EXECUTION_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'PROCESS_INSTANCE_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'PROCESS_INSTANCE_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'PROCESS_INSTANCE_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'PROC_DEF_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'PROC_DEF_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'PROC_DEF_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'ACTIVITY_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'ACTIVITY_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'活动 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'ACTIVITY_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'PHASE_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'PHASE_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'before / after',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'PHASE_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'DUEDATE_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'DUEDATE_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'到期时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'DUEDATE_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'TENANT_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'TENANT_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'TENANT_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'CREATE_TIME_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'CREATE_TIME_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'CREATE_TIME_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'RETRIES_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'RETRIES_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'剩余重试次数',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'RETRIES_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'DEAD_LETTER_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'DEAD_LETTER_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=已进死信',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'DEAD_LETTER_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'EXCEPTION_MSG_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'EXCEPTION_MSG_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败原因',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'EXCEPTION_MSG_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_RU_JOB') and name = N'LAST_FAILURE_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_RU_JOB') and c.name = N'LAST_FAILURE_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次失败时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_RU_JOB',
        @level2type = N'COLUMN', @level2name = N'LAST_FAILURE_';
go

if object_id('ACT_EVT_OUTBOX', 'U') is not null
    and not exists (select 1 from sys.extended_properties where name = N'MS_Description'
        and major_id = object_id('ACT_EVT_OUTBOX') and minor_id = 0)
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'事务性事件 outbox：引擎事件与业务写入同事务落库，由发布器至少一次投递',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX';
go

if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'事件 id（订阅方幂等键）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'EVENT_TYPE_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'EVENT_TYPE_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'事件类型（PROCESS_COMPLETED / TASK_CREATED ...）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'EVENT_TYPE_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'ENTITY_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'ENTITY_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'实体 id（任务 / 活动 / 实例）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'ENTITY_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PROCESS_INSTANCE_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PROCESS_INSTANCE_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程实例 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PROCESS_INSTANCE_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PROC_DEF_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PROC_DEF_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PROC_DEF_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'EXECUTION_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'EXECUTION_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'执行 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'EXECUTION_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'ACTIVITY_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'ACTIVITY_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'活动 id',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'ACTIVITY_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'TENANT_ID_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'TENANT_ID_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'租户',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'TENANT_ID_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PAYLOAD_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PAYLOAD_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'事件载荷（JSON）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PAYLOAD_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'CREATE_TIME_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'CREATE_TIME_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'创建时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'CREATE_TIME_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PUBLISHED_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PUBLISHED_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=已发布',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PUBLISHED_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PUBLISH_ATTEMPTS_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PUBLISH_ATTEMPTS_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'发布尝试次数',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PUBLISH_ATTEMPTS_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'DEAD_LETTER_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'DEAD_LETTER_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'1=投递失败进死信',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'DEAD_LETTER_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'LAST_ERROR_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'LAST_ERROR_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'最后一次发布失败原因',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'LAST_ERROR_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'NEXT_ATTEMPT_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'NEXT_ATTEMPT_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'下次可发布时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'NEXT_ATTEMPT_';
go
if exists (select 1 from sys.columns
    where object_id = object_id('ACT_EVT_OUTBOX') and name = N'PUBLISHED_TIME_')
    and not exists (select 1 from sys.extended_properties ep
        join sys.columns c on c.object_id = ep.major_id and c.column_id = ep.minor_id
        where ep.name = N'MS_Description' and ep.major_id = object_id('ACT_EVT_OUTBOX') and c.name = N'PUBLISHED_TIME_')
    exec sp_addextendedproperty @name = N'MS_Description', @value = N'发布时间（UTC）',
        @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'ACT_EVT_OUTBOX',
        @level2type = N'COLUMN', @level2name = N'PUBLISHED_TIME_';
go


-- ─────────────────────────── 执行结果自检 ───────────────────────────
--  期望 schema.version = 8.1.4（与 DatabaseSchemaChecker.ExpectedSchemaVersion 一致）；
--  next.dbid 是 ID 区块游标，重复执行不会重置。
select NAME_, VALUE_ from ACT_GE_PROPERTY where NAME_ in ('schema.version', 'next.dbid');

