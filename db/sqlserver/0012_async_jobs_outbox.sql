-- NetBpm 8.1.4 / 0012：异步作业（ACT_RU_JOB）与事务性 outbox（ACT_EVT_OUTBOX）
-- 幂等：object_id 判重，可在已有库上重复执行。

if object_id(N'ACT_RU_JOB', N'U') is null
begin
    create table ACT_RU_JOB (
        ID_                  nvarchar(64)  not null,
        REV_                 int           not null default 1,
        EXECUTION_ID_        nvarchar(64)  not null,
        PROCESS_INSTANCE_ID_ nvarchar(64)  not null,
        PROC_DEF_ID_         nvarchar(255) null,
        ACTIVITY_ID_         nvarchar(255) not null,
        PHASE_               nvarchar(16)  not null,
        DUEDATE_             datetime2(3)  not null,
        TENANT_ID_           nvarchar(64)  not null default N'',
        CREATE_TIME_         datetime2(3)  not null,
        RETRIES_             int           not null default 3,
        DEAD_LETTER_         bit           not null default 0,
        EXCEPTION_MSG_       nvarchar(1000) null,
        LAST_FAILURE_        datetime2(3)  null,
        constraint PK_ACT_RU_JOB primary key (ID_)
    );

    create index ACT_IDX_JOB_DUE on ACT_RU_JOB (DUEDATE_, DEAD_LETTER_);
    create index ACT_IDX_JOB_EXECUTION on ACT_RU_JOB (EXECUTION_ID_);
    create index ACT_IDX_JOB_PROC_INST on ACT_RU_JOB (PROCESS_INSTANCE_ID_);
end;

if object_id(N'ACT_EVT_OUTBOX', N'U') is null
begin
    create table ACT_EVT_OUTBOX (
        ID_                  nvarchar(64)  not null,
        EVENT_TYPE_          nvarchar(64)  not null,
        ENTITY_ID_           nvarchar(64)  null,
        PROCESS_INSTANCE_ID_ nvarchar(64)  null,
        PROC_DEF_ID_         nvarchar(255) null,
        EXECUTION_ID_        nvarchar(64)  null,
        ACTIVITY_ID_         nvarchar(255) null,
        TENANT_ID_           nvarchar(64)  not null default N'',
        PAYLOAD_             nvarchar(max) null,
        CREATE_TIME_         datetime2(3)  not null,
        PUBLISHED_           bit           not null default 0,
        PUBLISH_ATTEMPTS_    int           not null default 0,
        DEAD_LETTER_         bit           not null default 0,
        LAST_ERROR_          nvarchar(1000) null,
        NEXT_ATTEMPT_        datetime2(3)  null,
        PUBLISHED_TIME_      datetime2(3)  null,
        constraint PK_ACT_EVT_OUTBOX primary key (ID_)
    );

    create index ACT_IDX_OUTBOX_PENDING on ACT_EVT_OUTBOX (PUBLISHED_, DEAD_LETTER_, CREATE_TIME_);
    create index ACT_IDX_OUTBOX_PROC_INST on ACT_EVT_OUTBOX (PROCESS_INSTANCE_ID_);
end;
