-- 0009 运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）
--
-- 背景：0001 已覆盖大部分运行期索引（PROC_INST_ID_ / PARENT_ID_ / EXECUTION_ID_ 等），
-- 这里只补本引擎实际使用、但上游清单里没有的三处：
--   1) 包含网关 Join 的 (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_)；
--   2) 待办按办理人过滤的 ACT_RU_TASK(ASSIGNEE_)；
--   3) 变量按执行作用域加载的 (EXECUTION_ID_, NAME_)。
--
-- 幂等：sys.indexes 判重，可重复执行。
-- 对已初始化的库请单独执行本脚本：
--   NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0009_perf_indexes.sql

if not exists (select 1 from sys.indexes
               where name = 'ACT_IDX_EXEC_ACT_PROCINST_ACTIVE' and object_id = object_id('ACT_RU_EXECUTION'))
create index ACT_IDX_EXEC_ACT_PROCINST_ACTIVE on ACT_RU_EXECUTION (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_);

if not exists (select 1 from sys.indexes
               where name = 'ACT_IDX_TASK_ASSIGNEE' and object_id = object_id('ACT_RU_TASK'))
create index ACT_IDX_TASK_ASSIGNEE on ACT_RU_TASK (ASSIGNEE_);

if not exists (select 1 from sys.indexes
               where name = 'ACT_IDX_VARIABLE_EXEC_NAME' and object_id = object_id('ACT_RU_VARIABLE'))
create index ACT_IDX_VARIABLE_EXEC_NAME on ACT_RU_VARIABLE (EXECUTION_ID_, NAME_);
