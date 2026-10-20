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

--  流程模型版本历史（管理端发布治理）。
--
--  背景：设计器保存模型时会覆盖 bpmn_model.bpmn_xml；本表在每次保存/回滚时追加一版快照，
--        支持查看历史、回滚到任意版本，并记录操作人与动作。
--
--  注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider mysql --connection "..." --script db/mysql/0008_create_model_history.sql
--   3. 本脚本幂等，可重复执行。

create table if not exists bpmn_model_history (
    id varchar(64) not null comment '历史版本 id',
    tenant_id bigint not null comment '多租户 id',
    model_id varchar(64) not null comment '模型 id（bpmn_model.id）',
    revision int not null comment '版本号（同一模型内递增，从 1 开始）',
    name varchar(200) not null comment '保存时的流程名称',
    process_key varchar(50) not null comment '保存时的流程 key',
    description varchar(500) comment '保存时的流程描述',
    bpmn_xml text not null comment 'BPMN XML 快照',
    svg text comment '流程图 SVG 快照',
    action varchar(32) not null comment '动作：save / rollback',
    operator varchar(255) comment '操作人（请求身份）',
    created_at datetime(3) not null comment '写入时间（UTC）',
    primary key (id),
    unique key ux_bpmn_model_history_rev (model_id, revision),
    index ix_bpmn_model_history_tenant (tenant_id, model_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE utf8mb4_bin COMMENT='流程模型版本历史：每次保存/回滚追加一版，支持回滚与变更追溯';

