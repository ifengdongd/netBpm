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

--  HTTP 幂等键（Idempotency-Key）。
--
--  背景：POST 启动实例 / 批量操作在客户端超时重试时会被重复执行。
--  本表缓存「键 → 首次响应」，同键重试直接回放，不进入引擎命令。
--
--  注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0005_create_idempotency.sql
--   3. STATUS_CODE_ 为空表示「已占位、仍在处理」，并发同键请求会得到 409。

if object_id('ACT_IDEMPOTENCY', 'U') is null
    create table ACT_IDEMPOTENCY (
    IDEMPOTENCY_KEY_ nvarchar(120) not null,
    REQUEST_HASH_ nvarchar(64) not null,
    STATUS_CODE_ int,
    CONTENT_TYPE_ nvarchar(200),
    RESPONSE_BODY_ nvarchar(max),
    CREATED_AT_ datetime2(3) not null,
    EXPIRES_AT_ datetime2(3) not null,
    constraint pk_ACT_IDEMPOTENCY primary key clustered (IDEMPOTENCY_KEY_)
    )
go

if not exists (select 1 from sys.indexes where name = 'ACT_IDX_IDEMP_EXPIRES' and object_id = object_id('ACT_IDEMPOTENCY'))
create index ACT_IDX_IDEMP_EXPIRES on ACT_IDEMPOTENCY (EXPIRES_AT_);
go
