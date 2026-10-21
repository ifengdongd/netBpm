# NetBpm 数据库脚本

两套方言（`mysql/` 与 `sqlserver/`）由**单一数据源**生成，编号一一对应、结构严格一致，差异仅为方言语法。

## 单一数据源与生成

脚本**请勿手改**。真正的定义只在一处：

- `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` —— 表 / 列 / 索引 / 外键 / 注释 / 种子数据（方言无关）
- `tools/NetBpm.DbInit/Schema/SqlGenerator.cs` —— 按方言渲染出 MySQL / SQL Server 两套 `.sql`

```bash
# 只重新生成 db/mysql 与 db/sqlserver（不连库）
dotnet run --project tools/NetBpm.DbInit -- --generate-only

# 一步初始化：生成脚本 → 初始化结构 + 种子数据 → 校验 schema.version（幂等）
dotnet run --project tools/NetBpm.DbInit -- \
  --provider mysql --connection "<连接串>" --create-database
```

生成的脚本仍然签入版本库（运行时 `DatabaseMigrator` 直接读 `db/<dialect>/*.sql`，不依赖生成器）。

## 脚本分层

| 编号 | 脚本 | 层 | 说明 |
|---|---|---|---|
| 0001 | `0001_create_engine_schema.sql` | **引擎基础表** | Activiti 子集 13 张 ACT_* 表（执行树 / 任务 / 变量 / 历史 / 部署）+ 索引外键 + `schema.version` / `next.dbid` 等初值 |
| 0002 | `0002_create_business_tables.sql` | **流程实例业务表** | `instances` / `instance_current_task` / `deal_history` + `workflow_type` 流程类型表（`ProcessInfoEnum` 数据源，启动时加载） |
| 0003 | `0003_create_demo_tables.sql` | **Demo 业务表** | 请假单（`leave_bill`）、订单单（`order_bill`）、流程参数键值表（`workflow_parameters`）、组织架构（`sys_*` 四张：部门 / 员工 / 审批组） |
| 0004 | `0004_perf_indexes.sql` | **性能增强** | 运行期查询索引（执行树 / 任务 / 变量 / 关系）；幂等，可对已有库单独执行 |
| 0005 | `0005_create_idempotency.sql` | **应用层（REST）** | `ACT_IDEMPOTENCY` HTTP 幂等键表；仅 REST 宿主使用，不属于 Activiti 13 张引擎表 |
| 0006 | `0006_create_timer_job.sql` | **引擎扩展（定时器）** | `ACT_RU_TIMER_JOB` 边界定时作业表（一次性）；启用「用户任务 + 定时边界事件」时必需，幂等可重复执行 |
| 0007 | `0007_create_migration_log.sql` | **引擎扩展（迁移审计）** | `ACT_HI_MIGRATION` 流程实例迁移审计表（源/目标定义、活动映射、原因、操作人）；使用实例迁移时必需，幂等可重复执行 |
| 0008 | `0008_create_model_history.sql` | **管理端（模型治理）** | `bpmn_model_history` 流程模型版本历史（每次保存/回滚一版）；使用设计器服务端模型/回滚时必需，幂等可重复执行 |
| 0009 | `0009_create_admin_governance.sql` | **管理端（治理）** | `admin_audit_log` 管理动作审计（转办 / 跳转 / 变量直写）+ `admin_history_archive` 历史归档快照；启用审计查询 / 历史归档时必需，幂等可重复执行 |
| 0010 | `0010_model_key_unique.sql` | **管理端（模型治理）** | `bpmn_model (tenant_id, process_key)` 唯一索引 `ux_bpmn_model_key`（同一 key 只能对应一个模型）；已有库升级用，幂等可重复执行 |
| 0011 | `0011_engine_upgrade.sql` | **增量升级** | `ACT_GE_SCHEMA_LOG` 脚本执行日志 + 模型大文本列扩容（MySQL `text` 64KB → `mediumtext`）+ 定时作业重试/死信列（`RETRIES_` / `DEAD_LETTER_` / `EXCEPTION_MSG_` / `LAST_FAILURE_`）+ 死信索引；全部幂等 |
| 0012 | `0012_async_jobs_outbox.sql` | **异步作业 / outbox** | `ACT_RU_JOB`（asyncBefore/asyncAfter 作业，含重试与死信）+ `ACT_EVT_OUTBOX`（事务性事件外发队列，含发布重试/死信）+ 查询索引；全部幂等 |

> **其它 .NET 项目接入引擎**：必跑 0001 → 0002（引擎基础 + 流程实例/流程类型）；
> 0003 是 Demo 业务表，按需取舍。

## 执行顺序

`0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007 → 0008 → 0009 → 0010 → 0011 → 0012`

> **增量迁移（重要）**：`migrate` 现在按**脚本级**补跑，而不是「库已初始化就整体跳过」。
> 已执行脚本记录在 `ACT_GE_SCHEMA_LOG`（脚本名 + SHA-256 校验和）：
>
> - 已执行且校验和一致 → 跳过；
> - 已执行但内容变化 → 幂等脚本重跑并更新校验和；`0001-0003` 这类一次性建表脚本直接报错，
>   变更必须新增 `00xx_*.sql`；
> - 未执行 → 执行并记录。
>
> **老库首次接入**（没有 `ACT_GE_SCHEMA_LOG`）会按实际存在的表 / 索引回填基线，
> 因此不会再重跑 `0001-0003`，但会自动补上缺失的 `0004-0011`：
>
> ```bash
> dotnet run --project tools/NetBpm.DbMigrate -- migrate \
>   --provider mysql --connection "<连接串>"
> ```
>
> 新库同样一条 `migrate` 即可。`apply --script` 仅保留给运维手动执行单个脚本（脚本幂等）。
>
> 各脚本仍可单独 `apply`：`0004`（索引）、`0005`（幂等表）、`0006`（定时作业）、
> `0007`（迁移审计）、`0008`（模型历史）、`0009`（审计/归档）、`0010`（模型 key 唯一）、
> `0011`（增量升级：执行日志 + 大文本扩容 + 作业重试/死信列）、
> `0012`（异步作业 + outbox）均为幂等脚本。

- MySQL：`mysql -u<user> -p <database> < db/mysql/000x_*.sql`（库示例 `netbpm_demo`）；
- SQL Server：脚本统一落 `dbo`（与引擎 ACT_* 表同 schema），先建库后逐个执行。

## 方言差异

- MySQL：`utf8mb4`（业务表）/ `utf8`（引擎表，与上游 Activiti 一致）、`comment` 列注释；
- SQL Server：`nvarchar`、命名约束（`pk_*` / `df_*` / `ix_*`）、`go` 批分隔、
  `if not exists` 保护的索引与种子创建；
- 列结构两方言一一对应（0001 / 0002 / 0003 等头注释含来源与差异说明）。

## 对齐决策清单

合并前两套脚本是「按方言各自手写、结构互有出入」。合并为单一数据源时做了以下对齐，
**对齐原则：表 / 列 / 索引 / 外键 / 注释两方言最终一一对应，仅保留方言语法差异；对齐结果不低于原任一方言。**

| 项 | 决策 |
|---|---|
| 引擎表类型 | `SUSPENSION_STATE_`（`ACT_RE_PROCDEF` / `ACT_RU_EXECUTION`）SQL Server 原为 `tinyint`，统一为 `int`（与 MySQL / 上游一致） |
| 默认值 | `ACT_RE_DEPLOYMENT.VERSION_`、`workflow_type.sort_order` / `enabled` 的默认值（`1` / `0` / `1`）补齐到 SQL Server |
| 时间类型 | 引擎表 `datetime(3)` / `timestamp(3)` → SQL Server `datetime`；幂等表 → `datetime2(3)` |
| 索引 | `0001_create_engine_schema.sql` = 上游 Activiti MySQL 的 22 个索引（两方言一致，`ACT_IDC_EXEC_ROOT` 保留上游拼写）；`0004_perf_indexes.sql` = 两方言运行期索引并集 16 个（SQL Server 原引擎脚本的 12 个额外索引移入 0004，并补齐 MySQL 缺失；`ACT_IDX_IDENT_LNK_TASK` 去掉 INCLUDE 使定义一致） |
| 外键 | 14 个外键两方言一致；新增 `ACT_FK_EXE_PROCINST`（`PROC_INST_ID_` 自引用）到 SQL Server。自引用执行树 FK：MySQL 渲染 `on delete cascade`（`ACT_FK_EXE_PROCINST` 另含 `on update cascade`），SQL Server 因「多重级联路径」限制渲染为 NO ACTION；引擎删除本身先子后父，不依赖 DB 级联 |
| 注释 | SQL Server 补齐业务表（`workflow_type` / `leave_bill` / `sys_*` / `order_bill` / `workflow_parameters`）的表与列 `MS_Description`（原缺失），与 MySQL `comment` 一一对应 |
| 约束命名 | SQL Server 统一 `pk_<table>` / `df_<table>_<column>`（原流程实例表的 `PK_instances` / `pk__xxx` 自动名统一为小写命名） |
| 幂等策略 | `0001` / `0002` 及 `0003` 的业务表单（`leave_bill` / `order_bill` / `workflow_parameters`）一次性执行；`0003` 的组织架构（`sys_*`）、`0004` / `0005` 幂等可重复（`apply` 单独执行用） |

## 运维：历史表空间回收（高频归档场景）

历史归档（`POST /api/admin/history/archive`）会在**同一事务**内把 5 张 `ACT_HI_*`
的行搬进 `admin_history_archive`（JSON 快照）并删除原行——逻辑上历史已转移，
但 InnoDB / SQL Server 的页内空洞不会自动归还给操作系统，`.ibd` / `.mdf` 文件只增不减。
长期高频归档的库建议按以下节奏做空间回收（低频归档可忽略）：

```sql
-- MySQL：先确认碎片率，再决定是否重建
select table_name, data_free / (data_length + index_length) as frag_ratio
  from information_schema.tables
 where table_schema = 'netbpm' and table_name like 'ACT_HI_%';

-- 碎片率高（如 > 30%）时逐表重建（锁表期间业务会阻塞，放维护窗口）：
optimize table ACT_HI_PROCINST;
optimize table ACT_HI_ACTINST;
optimize table ACT_HI_TASKINST;
optimize table ACT_HI_VARINST;
optimize table ACT_HI_IDENTITYLINK;

-- SQL Server：重建聚集索引回收页内空洞（ ONLINE = ON 需企业版）
alter index all on ACT_HI_PROCINST rebuild;
```

配套的**数据保留**机制（应用层已内置）：

| 数据 | 机制 | 配置 |
|---|---|---|
| 管理动作审计 | `AdminAuditCleanupService`（每 6h） | `NetBpm:Admin:AuditRetentionDays`（0=永久） |
| 历史归档快照 | `AdminArchiveCleanupService`（每 6h） | `NetBpm:Admin:ArchiveRetentionDays`（0=永久） |
| 模型版本历史 | 端点手动清理 `DELETE /api/admin/models/{id}/history?keepLatest=N` | — |

> ⚠️ 归档记录删除前如需长期留存，请先把 `admin_history_archive.payload`
> 导出到对象存储 / 冷库——删除后引擎侧 ACT_HI_* 原行已不存在，无法重建。
