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
--
--  ⚠️ 表名、列名、索引名、外键名与 Activiti 逐字一致 —— §7.1 决定两端可读写同一套
--     表结构，从而支持 §9.2 的「库级 dump 行快照对拍」。请勿改名或「顺手优化」列类型。
--  ⚠️ ACT_IDC_EXEC_ROOT 的 IDC 为上游 Activiti 的原样拼写（非笔误），刻意保留。
--  ⚠️ 裁掉 ACT_HI_DETAIL 的前提是历史级别取 AUDIT（Activiti 默认，只记活动/任务/变量实例）。
--
--  本脚本合并原 0001（建表）与 0002（初值）：13 张引擎表 + 索引 + 唯一约束 / 外键 +
--  ACT_GE_PROPERTY 的三条初值。
--  ⚠️ 初值必须逐字一致（开发计划 §7.3）：
--     · schema.version  —— 引擎启动时校验（DatabaseSchemaChecker）
--     · schema.history  —— 记录建库时的版本，供后续迁移判断
--     · next.dbid       —— DbIdGenerator 的区块游标；区块大小默认 2500
-- ============================================================================

-- ─────────────────────────── 运行时表（8 张）───────────────────────────

create table ACT_GE_PROPERTY (
    NAME_ nvarchar(64) not null,
    VALUE_ nvarchar(300),
    REV_ int,
    primary key (NAME_)
);

create table ACT_GE_BYTEARRAY (
    ID_ nvarchar(64) not null,
    REV_ int,
    NAME_ nvarchar(255),
    DEPLOYMENT_ID_ nvarchar(64),
    BYTES_ varbinary(max),
    GENERATED_ tinyint,
    primary key (ID_)
);

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


-- ─────────────────────────── 索引 ───────────────────────────

create index ACT_IDX_EXEC_BUSKEY on ACT_RU_EXECUTION(BUSINESS_KEY_);
create index ACT_IDC_EXEC_ROOT on ACT_RU_EXECUTION(ROOT_PROC_INST_ID_);
create index ACT_IDX_TASK_CREATE on ACT_RU_TASK(CREATE_TIME_);
create index ACT_IDX_IDENT_LNK_USER on ACT_RU_IDENTITYLINK(USER_ID_);
create index ACT_IDX_IDENT_LNK_GROUP on ACT_RU_IDENTITYLINK(GROUP_ID_);
create index ACT_IDX_IDENT_LNK_TASK on ACT_RU_IDENTITYLINK(TASK_ID_);
create index ACT_IDX_VARIABLE_TASK_ID on ACT_RU_VARIABLE(TASK_ID_);
create index ACT_IDX_ATHRZ_PROCEDEF on ACT_RU_IDENTITYLINK(PROC_DEF_ID_);
create index ACT_IDX_RE_PROCDEF_DEPLOYMENT_ID on ACT_RE_PROCDEF(DEPLOYMENT_ID_, ID_);
create index ACT_IDX_HI_PRO_INST_END on ACT_HI_PROCINST(END_TIME_);
create index ACT_IDX_HI_PRO_I_BUSKEY on ACT_HI_PROCINST(BUSINESS_KEY_);
create index ACT_IDX_HI_ACT_INST_START on ACT_HI_ACTINST(START_TIME_);
create index ACT_IDX_HI_ACT_INST_END on ACT_HI_ACTINST(END_TIME_);
create index ACT_IDX_HI_ACT_INST_PROCINST on ACT_HI_ACTINST(PROC_INST_ID_, ACT_ID_);
create index ACT_IDX_HI_ACT_INST_EXEC on ACT_HI_ACTINST(EXECUTION_ID_, ACT_ID_);
create index ACT_IDX_HI_PROCVAR_PROC_INST on ACT_HI_VARINST(PROC_INST_ID_);
create index ACT_IDX_HI_PROCVAR_NAME_TYPE on ACT_HI_VARINST(NAME_, VAR_TYPE_);
create index ACT_IDX_HI_PROCVAR_TASK_ID on ACT_HI_VARINST(TASK_ID_);
create index ACT_IDX_HI_IDENT_LNK_USER on ACT_HI_IDENTITYLINK(USER_ID_);
create index ACT_IDX_HI_IDENT_LNK_TASK on ACT_HI_IDENTITYLINK(TASK_ID_);
create index ACT_IDX_HI_IDENT_LNK_PROCINST on ACT_HI_IDENTITYLINK(PROC_INST_ID_);
create index ACT_IDX_HI_TASK_INST_PROCINST on ACT_HI_TASKINST(PROC_INST_ID_);

-- ─────────────────────────── 唯一约束与外键 ───────────────────────────

alter table ACT_RE_PROCDEF
    add constraint ACT_UNIQ_PROCDEF
    unique (KEY_, VERSION_, TENANT_ID_);
alter table ACT_GE_BYTEARRAY
    add constraint ACT_FK_BYTEARR_DEPL
    foreign key (DEPLOYMENT_ID_)
    references ACT_RE_DEPLOYMENT (ID_);
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_PROCINST
    foreign key (PROC_INST_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_PARENT
    foreign key (PARENT_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_SUPER
    foreign key (SUPER_EXEC_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_PROCDEF
    foreign key (PROC_DEF_ID_)
    references ACT_RE_PROCDEF (ID_);
alter table ACT_RU_IDENTITYLINK
    add constraint ACT_FK_TSKASS_TASK
    foreign key (TASK_ID_)
    references ACT_RU_TASK (ID_);
alter table ACT_RU_IDENTITYLINK
    add constraint ACT_FK_ATHRZ_PROCEDEF
    foreign key (PROC_DEF_ID_)
    references ACT_RE_PROCDEF (ID_);
alter table ACT_RU_IDENTITYLINK
    add constraint ACT_FK_IDL_PROCINST
    foreign key (PROC_INST_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_TASK
    add constraint ACT_FK_TASK_EXE
    foreign key (EXECUTION_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_TASK
    add constraint ACT_FK_TASK_PROCINST
    foreign key (PROC_INST_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_TASK
    add constraint ACT_FK_TASK_PROCDEF
    foreign key (PROC_DEF_ID_)
    references ACT_RE_PROCDEF (ID_);
alter table ACT_RU_VARIABLE
    add constraint ACT_FK_VAR_EXE
    foreign key (EXECUTION_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_VARIABLE
    add constraint ACT_FK_VAR_PROCINST
    foreign key (PROC_INST_ID_)
    references ACT_RU_EXECUTION (ID_);
alter table ACT_RU_VARIABLE
    add constraint ACT_FK_VAR_BYTEARRAY
    foreign key (BYTEARRAY_ID_)
    references ACT_GE_BYTEARRAY (ID_);

-- ─────────────────────────── 引擎初值（ACT_GE_PROPERTY） ───────────────────────────

insert into ACT_GE_PROPERTY values ('schema.version', '8.1.4', '1');
insert into ACT_GE_PROPERTY values ('schema.history', 'create(8.1.4)', '1');
insert into ACT_GE_PROPERTY values ('next.dbid', '1', '1');

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'通用属性表：存放 schema 版本、schema 历史与 ID 区块游标等全局键值对',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_GE_PROPERTY';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'二进制数据表：存放部署的流程定义资源（BPMN XML）等字节内容',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_GE_BYTEARRAY';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'部署信息表：每次流程部署产生一条记录',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RE_DEPLOYMENT';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程定义表：部署解析后的流程定义（KEY_ + VERSION_ + TENANT_ID_ 唯一）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RE_PROCDEF';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时执行实例表：执行树，引擎唯一的运行态（流程实例 / 并发分支 / 作用域）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RU_EXECUTION';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时任务表：当前待办的用户任务',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RU_TASK';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时身份关联表：任务的用户 / 组关联（办理人、候选人与候选组）',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RU_IDENTITYLINK';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'运行时变量表：流程实例、执行与任务级变量',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_RU_VARIABLE';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史流程实例表：记录已启动过的流程实例',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_HI_PROCINST';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史活动实例表：流程图着色数据源',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_HI_ACTINST';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史任务实例表：记录已产生过的用户任务',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_HI_TASKINST';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史变量实例表：变量在流程结束时的最终值',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_HI_VARINST';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史身份关联表：历史任务的用户 / 组关联',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'ACT_HI_IDENTITYLINK';

go
