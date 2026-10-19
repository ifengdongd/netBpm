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
    NAME_ varchar(64) not null,
    VALUE_ varchar(300),
    REV_ int,
    primary key (NAME_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='通用属性表：存放 schema 版本、schema 历史与 ID 区块游标等全局键值对';

create table ACT_GE_BYTEARRAY (
    ID_ varchar(64) not null,
    REV_ int,
    NAME_ varchar(255),
    DEPLOYMENT_ID_ varchar(64),
    BYTES_ LONGBLOB,
    GENERATED_ tinyint,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='二进制数据表：存放部署的流程定义资源（BPMN XML）等字节内容';

create table ACT_RE_DEPLOYMENT (
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

create table ACT_RE_PROCDEF (
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

create table ACT_RU_EXECUTION (
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

create table ACT_RU_TASK (
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

create table ACT_RU_IDENTITYLINK (
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

create table ACT_RU_VARIABLE (
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

create table ACT_HI_PROCINST (
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

create table ACT_HI_ACTINST (
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

create table ACT_HI_TASKINST (
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

create table ACT_HI_VARINST (
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

create table ACT_HI_IDENTITYLINK (
    ID_ varchar(64) not null,
    GROUP_ID_ varchar(255),
    TYPE_ varchar(255),
    USER_ID_ varchar(255),
    TASK_ID_ varchar(64),
    PROC_INST_ID_ varchar(64),
    DETAILS_ LONGBLOB,
    primary key (ID_)
) ENGINE=InnoDB DEFAULT CHARSET=utf8 COLLATE utf8_bin COMMENT='历史身份关联表：历史任务的用户 / 组关联';


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
    references ACT_RU_EXECUTION (ID_) on delete cascade on update cascade;
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_PARENT
    foreign key (PARENT_ID_)
    references ACT_RU_EXECUTION (ID_) on delete cascade;
alter table ACT_RU_EXECUTION
    add constraint ACT_FK_EXE_SUPER
    foreign key (SUPER_EXEC_)
    references ACT_RU_EXECUTION (ID_) on delete cascade;
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
