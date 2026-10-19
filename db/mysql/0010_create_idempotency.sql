-- 0010 HTTP 幂等键（Idempotency-Key）
--
-- 背景：POST 启动实例 / 批量操作在客户端超时重试时会被重复执行。
-- 本表缓存「键 → 首次响应」，同键重试直接回放，不进入引擎命令。
--
-- 注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0010_create_idempotency.sql
--   3. STATUS_CODE_ 为空表示「已占位、仍在处理」，并发同键请求会得到 409。

create table if not exists ACT_IDEMPOTENCY (
    IDEMPOTENCY_KEY_ varchar(120) not null,
    REQUEST_HASH_ varchar(64) not null,
    STATUS_CODE_ int null,
    CONTENT_TYPE_ varchar(200) null,
    RESPONSE_BODY_ mediumtext null,
    CREATED_AT_ datetime(3) not null,
    EXPIRES_AT_ datetime(3) not null,
    primary key (IDEMPOTENCY_KEY_),
    key ACT_IDX_IDEMP_EXPIRES (EXPIRES_AT_)
) engine = InnoDB default charset = utf8mb4;
