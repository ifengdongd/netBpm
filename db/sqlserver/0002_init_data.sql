-- ============================================================================
--  NetBpm · SQL Server 初始化数据
-- ============================================================================
--  来源：Activiti 9.0.0 的 activiti.mssql.create.engine.sql 中的三条 insert
--       （Activiti 把它们与建表语句放在同一个文件里，这里拆开以配合 DbUp 的顺序执行）
--
--  ⚠️ 初值必须逐字一致（开发计划 §7.3）：
--     · schema.version  —— 引擎启动时校验（DatabaseSchemaChecker）
--     · schema.history  —— 记录建库时的版本，供后续迁移判断
--     · next.dbid       —— DbIdGenerator 的区块游标；区块大小默认 2500
-- ============================================================================

insert into ACT_GE_PROPERTY
values ('schema.version', '8.1.4', 1);

insert into ACT_GE_PROPERTY
values ('schema.history', 'create(8.1.4)', 1);

insert into ACT_GE_PROPERTY
values ('next.dbid', '1', 1);

go
