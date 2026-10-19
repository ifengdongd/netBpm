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

--  运行期查询索引（性能增强，不影响行内容，可与 Activiti 共库）。
--
--  背景：0001 的索引沿用上游 Activiti 清单，其中运行期（ACT_RU_*）只有少数几个。
--  本引擎实际高频执行的查询（执行树按流程实例/父执行、任务按办理人/流程实例、
--  变量按执行、关系按流程实例）在 MySQL 上会全表扫描。
--
--  幂等：MySQL 不支持 create index if not exists，这里用 information_schema 判重后动态执行。
--  对已初始化的库请单独执行本脚本：
--    NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0004_perf_indexes.sql
--  新库执行 migrate 时会连同本脚本一起执行。

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PROC_INST_PARENT on ACT_RU_EXECUTION (PROC_INST_ID_, PARENT_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_PROC_INST_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PARENT on ACT_RU_EXECUTION (PARENT_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_PARENT');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_ACT_PROCINST_ACTIVE on ACT_RU_EXECUTION (ACT_ID_, PROC_INST_ID_, IS_ACTIVE_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_ACT_PROCINST_ACTIVE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXEC_PROC_INST_ID on ACT_RU_EXECUTION (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXEC_PROC_INST_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_PROC on ACT_RU_EXECUTION (PROC_DEF_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXECUTION_PROC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_SUPER on ACT_RU_EXECUTION (SUPER_EXEC_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXECUTION_SUPER');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_EXECUTION_IDANDREV on ACT_RU_EXECUTION (ID_, REV_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_EXECUTION'
      and index_name = 'ACT_IDX_EXECUTION_IDANDREV');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROCINST on ACT_RU_TASK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_EXEC on ACT_RU_TASK (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_PROC_DEF_ID on ACT_RU_TASK (PROC_DEF_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_PROC_DEF_ID');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_TASK_ASSIGNEE on ACT_RU_TASK (ASSIGNEE_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_TASK'
      and index_name = 'ACT_IDX_TASK_ASSIGNEE');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_EXEC_NAME on ACT_RU_VARIABLE (EXECUTION_ID_, NAME_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_EXEC_NAME');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_EXEC on ACT_RU_VARIABLE (EXECUTION_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_EXEC');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_PROCINST on ACT_RU_VARIABLE (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_VARIABLE_BA on ACT_RU_VARIABLE (BYTEARRAY_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_VARIABLE'
      and index_name = 'ACT_IDX_VARIABLE_BA');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

set @ddl = (select if(count(*) = 0,
        'create index ACT_IDX_IDENT_LNK_PROCINST on ACT_RU_IDENTITYLINK (PROC_INST_ID_)',
        'select 1')
    from information_schema.statistics
    where table_schema = database() and table_name = 'ACT_RU_IDENTITYLINK'
      and index_name = 'ACT_IDX_IDENT_LNK_PROCINST');
prepare stmt from @ddl;
execute stmt;
deallocate prepare stmt;

