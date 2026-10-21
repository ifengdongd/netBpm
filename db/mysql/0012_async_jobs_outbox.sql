-- NetBpm 8.1.4 / 0012：异步作业（ACT_RU_JOB）与事务性 outbox（ACT_EVT_OUTBOX）
-- 幂等：全部 create table if not exists，可在已有库上重复执行。
--
-- ACT_RU_JOB       : asyncBefore / asyncAfter 作业（独占，一个作业 = 一个命令）
-- ACT_EVT_OUTBOX   : 引擎事件的事务性外发队列（至少一次投递）

create table if not exists ACT_RU_JOB (
    ID_                  varchar(64)  not null comment '作业 id（IIdGenerator 分配）',
    REV_                 int          not null default 1 comment '乐观锁版本',
    EXECUTION_ID_        varchar(64)  not null comment '执行 id',
    PROCESS_INSTANCE_ID_ varchar(64)  not null comment '流程实例 id',
    PROC_DEF_ID_         varchar(255) null     comment '流程定义 id',
    ACTIVITY_ID_         varchar(255) not null comment '活动 id',
    PHASE_               varchar(16)  not null comment 'before / after',
    DUEDATE_             datetime(3)  not null comment '到期时间（UTC）',
    TENANT_ID_           varchar(64)  not null default '' comment '租户',
    CREATE_TIME_         datetime(3)  not null comment '创建时间（UTC）',
    RETRIES_             int          not null default 3 comment '剩余重试次数',
    DEAD_LETTER_         tinyint      not null default 0 comment '1=已进死信',
    EXCEPTION_MSG_       varchar(1000) null    comment '最后一次失败原因',
    LAST_FAILURE_        datetime(3)  null     comment '最后一次失败时间（UTC）',
    primary key (ID_),
    key ACT_IDX_JOB_DUE (DUEDATE_, DEAD_LETTER_),
    key ACT_IDX_JOB_EXECUTION (EXECUTION_ID_),
    key ACT_IDX_JOB_PROC_INST (PROCESS_INSTANCE_ID_)
) engine = InnoDB default charset = utf8 collate = utf8_bin comment = '异步作业';

create table if not exists ACT_EVT_OUTBOX (
    ID_                  varchar(64)  not null comment '事件 id（订阅方幂等键）',
    EVENT_TYPE_          varchar(64)  not null comment '事件类型（PROCESS_COMPLETED / TASK_CREATED ...）',
    ENTITY_ID_           varchar(64)  null     comment '实体 id（任务 / 活动 / 实例）',
    PROCESS_INSTANCE_ID_ varchar(64)  null     comment '流程实例 id',
    PROC_DEF_ID_         varchar(255) null     comment '流程定义 id',
    EXECUTION_ID_        varchar(64)  null     comment '执行 id',
    ACTIVITY_ID_         varchar(255) null     comment '活动 id',
    TENANT_ID_           varchar(64)  not null default '' comment '租户',
    PAYLOAD_             mediumtext   null     comment '事件载荷（JSON）',
    CREATE_TIME_         datetime(3)  not null comment '创建时间（UTC）',
    PUBLISHED_           tinyint      not null default 0 comment '1=已发布',
    PUBLISH_ATTEMPTS_    int          not null default 0 comment '发布尝试次数',
    DEAD_LETTER_         tinyint      not null default 0 comment '1=投递失败进死信',
    LAST_ERROR_          varchar(1000) null    comment '最后一次发布失败原因',
    NEXT_ATTEMPT_        datetime(3)  null     comment '下次可发布时间（UTC）',
    PUBLISHED_TIME_      datetime(3)  null     comment '发布时间（UTC）',
    primary key (ID_),
    key ACT_IDX_OUTBOX_PENDING (PUBLISHED_, DEAD_LETTER_, CREATE_TIME_),
    key ACT_IDX_OUTBOX_PROC_INST (PROCESS_INSTANCE_ID_)
) engine = InnoDB default charset = utf8 collate = utf8_bin comment = '事务性事件 outbox';
