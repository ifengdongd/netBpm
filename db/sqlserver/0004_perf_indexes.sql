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

--  运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）。
--
--  幂等：sys.indexes 判重，可重复执行。
--  对已初始化的库请单独执行本脚本：
--    NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0004_perf_indexes.sql

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

