-- 0009 运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）
--
-- 背景：0001 的索引沿用了上游 Activiti 的清单，其中运行期（ACT_RU_*）只有
-- BUSINESS_KEY_ / ROOT_PROC_INST_ID_ / CREATE_TIME_ 等少数几个。本引擎实际高频执行的
-- 查询（执行树按流程实例/父执行、任务按办理人/流程实例、变量按执行、关系按流程实例）
-- 在 MySQL 上会全表扫描；表量上万后 Join / 会签 / 待办列表明显变慢。
--
-- 幂等：MySQL 不支持 create index if not exists，这里用 information_schema 判重后动态执行。
-- 对已初始化的库请单独执行本脚本：
--   NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0009_perf_indexes.sql
-- 新库执行 migrate 时会连同本脚本一起执行。

-- 执行树：按流程实例取全部子执行（并行网关 Join / 结束判定）
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PROC_INST_PARENT on ACT_RU_EXECUTION (PROC_INST_ID_, PARENT_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_PROC_INST_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 执行树：按父执行递归取子执行（删除 / 计数）
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PARENT on ACT_RU_EXECUTION (PARENT_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 包含网关 Join：某活动上仍未激活的执行
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_ACT_PROCINST_ACTIVE on ACT_RU_EXECUTION (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_ACT_PROCINST_ACTIVE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 任务：按流程实例列待办 / 实例级删除
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROCINST on ACT_RU_TASK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 任务：按执行取任务
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_EXEC on ACT_RU_TASK (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 任务：按办理人查待办（REST /api/runtime/tasks?assignee=）
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_ASSIGNEE on ACT_RU_TASK (ASSIGNEE_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_ASSIGNEE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 变量：按执行作用域加载变量（每个作用域一次全表扫描是变量读的最大热点）
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_EXEC_NAME on ACT_RU_VARIABLE (EXECUTION_ID_, NAME_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_EXEC_NAME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 变量：按流程实例清理 / 查询
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_PROCINST on ACT_RU_VARIABLE (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

-- 关系：按流程实例查参与者（involveUser 查重 / 实例级清理）
set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_PROCINST on ACT_RU_IDENTITYLINK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK'
      and index_name = 'ACT_IDX_IDENT_LNK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;
