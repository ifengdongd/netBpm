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

--  流程模型版本历史（管理端发布治理）。
--
--  背景：设计器保存模型时会覆盖 bpmn_model.bpmn_xml；本表在每次保存/回滚时追加一版快照，
--        支持查看历史、回滚到任意版本，并记录操作人与动作。
--
--  注意：
--   1. 这是应用层表，不属于 Activiti 的 13 张引擎表；
--   2. 已初始化的库需单独执行：
--      NetBpm.DbMigrate apply --provider sqlserver --connection "..." --script db/sqlserver/0008_create_model_history.sql
--   3. 本脚本幂等（object_id / sys.indexes 判重），可重复执行。

if object_id('bpmn_model_history', 'U') is null
    create table bpmn_model_history (
    id nvarchar(64) not null,
    tenant_id bigint not null,
    model_id nvarchar(64) not null,
    revision int not null,
    name nvarchar(200) not null,
    process_key nvarchar(50) not null,
    description nvarchar(500),
    bpmn_xml nvarchar(max) not null,
    svg nvarchar(max),
    action nvarchar(32) not null,
    operator nvarchar(255),
    created_at datetime not null,
    constraint pk_bpmn_model_history primary key clustered (id)
    )
go

if not exists (select 1 from sys.indexes where name = 'ux_bpmn_model_history_rev' and object_id = object_id('bpmn_model_history'))
create unique index ux_bpmn_model_history_rev on bpmn_model_history (model_id, revision);
go
if not exists (select 1 from sys.indexes where name = 'ix_bpmn_model_history_tenant' and object_id = object_id('bpmn_model_history'))
create index ix_bpmn_model_history_tenant on bpmn_model_history (tenant_id, model_id);
go

-- ─────────────────────────── 表与列注释（扩展属性 MS_Description） ───────────────────────────

exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程模型版本历史：每次保存/回滚追加一版，支持回滚与变更追溯',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'bpmn_model_history';

exec sp_addextendedproperty @name = N'MS_Description', @value = N'历史版本 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'多租户 id',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'tenant_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'模型 id（bpmn_model.id）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'model_id';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'版本号（同一模型内递增，从 1 开始）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'revision';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程名称',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'name';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程 key',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'process_key';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'保存时的流程描述',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'description';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'BPMN XML 快照',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'bpmn_xml';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'流程图 SVG 快照',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'svg';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'动作：save / rollback',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'action';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'操作人（请求身份）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'operator';
exec sp_addextendedproperty @name = N'MS_Description', @value = N'写入时间（UTC）',
    @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'bpmn_model_history',
    @level2type = N'COLUMN', @level2name = N'created_at';

go
