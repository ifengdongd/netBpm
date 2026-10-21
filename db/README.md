# NetBpm 数据库脚本

两套方言各 **一份** 初始化脚本，供测试环境与生产环境通用：

| 方言 | 脚本 | 手工执行 |
|---|---|---|
| MySQL | `mysql/netbpm_init.sql` | `mysql -u<user> -p < db/mysql/netbpm_init.sql`（脚本自带建库与 `use`） |
| SQL Server | `sqlserver/netbpm_init.sql` | 用 SSMS / `sqlcmd` 连 `master` 执行（脚本自带建库与 `use`） |

两份脚本由**单一数据源**生成，结构严格一一对应，差异仅为方言语法。
测试环境与生产环境**用同一份脚本**，只是库名 / 连接串不同（改脚本顶部「⓪ 建库 / 选库」两行，或用工具按连接串建库）。

## 单一数据源与生成

脚本**请勿手改**。真正的定义只在一处：

- `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` —— 表 / 列 / 索引 / 外键 / 注释 / 种子数据（方言无关）
- `tools/NetBpm.DbInit/Schema/SqlGenerator.cs` —— 按方言渲染出 MySQL / SQL Server 两份 `.sql`

```bash
# 只重新生成 db/mysql 与 db/sqlserver（不连库）
dotnet run --project tools/NetBpm.DbInit -- --generate-only

# 一步初始化：生成脚本 → 按需建库 → 执行脚本 → 校验 schema.version（幂等）
dotnet run --project tools/NetBpm.DbInit -- \
  --provider mysql --connection "<连接串>" --create-database
```

生成的脚本仍然签入版本库（运行时 `DatabaseMigrator` 直接读 `db/<dialect>/*.sql`，不依赖生成器）。

## 幂等模型（重要）

脚本是 **单文件、全幂等** 的：每一段建表 / 索引 / 外键 / 初值都带方言守卫，
**可以重复执行**——新库一次建成，老库重复执行只补缺失部分，已有数据不会被覆盖。

| 操作 | MySQL 守卫 | SQL Server 守卫 |
|---|---|---|
| 建库 | `create database if not exists` | `if db_id(...) is null` |
| 建表 | `create table if not exists` | `if object_id(...,'U') is null` |
| 索引 | `information_schema.statistics` 判重 + `@ddl` 动态执行 | `sys.indexes` 判重 |
| 唯一约束 / 外键 | `information_schema.table_constraints` 判重 + `@ddl` | `sys.objects` / `sys.foreign_keys` 判重 |
| 加列 | `information_schema.columns` 判重 + `@ddl` | `sys.columns` 判重 |
| 初值 | `insert ignore`（仅缺失时插入） | `if not exists (...)` |
| 种子数据 | `on duplicate key update` / `insert ignore` | `if not exists (...)` |
| 表列注释 | 内联 `comment` | `sys.extended_properties` 判重 |

> ⚠️ MySQL 连接串必须带 `Allow User Variables=true`（`@ddl` 是用户变量）。
> 不带会报 `Parameter '@ddl' must be defined`。
>
> ⚠️ 初值（`ACT_GE_PROPERTY` 的 `schema.version` / `schema.history` / `next.dbid`）
> **只在缺失时插入**：重复执行不会把 `next.dbid` 重置回 1，也就不会造成 id 重用。
> 想重置 ID 游标要显式 `update ACT_GE_PROPERTY`。

此前按「脚本编号 + `ACT_GE_SCHEMA_LOG` 校验和」做脚本级增量迁移的机制已移除：
单文件幂等取代了它。老库里残留的 `ACT_GE_SCHEMA_LOG` 表不再被读写，可手工 `drop table ACT_GE_SCHEMA_LOG;`。

## 脚本内容分层

| 段 | 内容 | 说明 |
|---|---|---|
| ⓪ | 建库 / 选库 | 默认库名 `netbpm`；仅手工执行时生效（工具执行会跳过，库名取连接串） |
| ① | **引擎基础表** | Activiti 子集 13 张 `ACT_*` 表（执行树 / 任务 / 变量 / 历史 / 部署）+ 索引外键 + `schema.version` / `next.dbid` 等初值 |
| ② | **流程实例业务表** | `instances` / `instance_current_task` / `deal_history` + `workflow_type` 流程类型表（`ProcessInfoEnum` 数据源，含种子）+ `bpmn_model` 流程模型表 |
| ③ | **Demo 业务表** | 请假单（`leave_bill`）、订单单（`order_bill`）、流程参数键值表（`workflow_parameters`）、组织架构（`sys_*` 四张，含演示种子数据） |
| ④ | **性能增强** | 运行期查询索引（执行树 / 任务 / 变量 / 关系 / 历史），幂等 |
| ⑤ | **应用层扩展表** | `ACT_IDEMPOTENCY`（HTTP 幂等键）、`ACT_RU_TIMER_JOB`（边界定时作业）、`ACT_HI_MIGRATION`（实例迁移审计）、`bpmn_model_history`（模型版本历史）、`admin_audit_log` + `admin_history_archive`（管理审计与历史归档）、`ACT_RU_JOB`（异步作业）、`ACT_EVT_OUTBOX`（事务性事件外发队列） |
| ⑥ | **老库兼容补齐** | 模型大文本列扩容（MySQL `text` 64KB → `mediumtext`）、定时作业重试 / 死信列；新库执行为空操作 |

> **其它 .NET 项目接入引擎**：整份脚本执行一次即可；只接引擎、不要业务表的项目，
> 忽略 ② ③ 建出来的表就行（对引擎功能无影响）。

## 执行方式

```bash
# 1. 工具（推荐）：建库 + 建表 + 初值 + 结构版本校验，幂等可反复跑
dotnet run --project tools/NetBpm.DbMigrate -- migrate --create-database \
  --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;"

# 2. 只校验结构版本
dotnet run --project tools/NetBpm.DbMigrate -- check --provider mysql --connection "<连接串>"

# 3. 宿主启动：NetBpm:AutoMigrate=true 时执行本脚本（库需已存在，或先跑一次 1）
#    NetBpm:ValidateSchemaOnStartup=true 时校验 schema.version
```

- 工具执行时脚本顶部的「建库 / 选库」段会被 `SqlScriptReader` 跳过（标记 `-- @@bootstrap-begin` / `-- @@bootstrap-end`），
  库名一律取自连接串，避免把会话切到脚本里的默认库；
- 手工执行（mysql 客户端 / SSMS）时该段生效，脚本自己建库并切库。

## 方言差异

- MySQL：`utf8mb4`（业务表）/ `utf8`（引擎表，与上游 Activiti 一致）、`comment` 列注释；
- SQL Server：`nvarchar`、命名约束（`pk_*` / `df_*`）、`go` 批分隔、
  `if not exists` 保护的索引 / 约束 / 注释创建；
- 列结构两方言一一对应。

## 对齐决策清单

合并为单一数据源时做了以下对齐，
**对齐原则：表 / 列 / 索引 / 外键 / 注释两方言最终一一对应，仅保留方言语法差异；对齐结果不低于原任一方言。**

| 项 | 决策 |
|---|---|
| 引擎表类型 | `SUSPENSION_STATE_`（`ACT_RE_PROCDEF` / `ACT_RU_EXECUTION`）SQL Server 原为 `tinyint`，统一为 `int`（与 MySQL / 上游一致） |
| 默认值 | `ACT_RE_DEPLOYMENT.VERSION_`、`workflow_type.sort_order` / `enabled` 的默认值（`1` / `0` / `1`）补齐到 SQL Server |
| 时间类型 | 引擎表 `datetime(3)` / `timestamp(3)` → SQL Server `datetime`；幂等表 → `datetime2(3)` |
| 索引 | ① 段 = 上游 Activiti MySQL 的 22 个索引（两方言一致，`ACT_IDC_EXEC_ROOT` 保留上游拼写）；④ 段 = 两方言运行期索引并集（`ACT_IDX_IDENT_LNK_TASK` 去掉 INCLUDE 使定义一致） |
| 外键 | 14 个外键两方言一致；新增 `ACT_FK_EXE_PROCINST`（`PROC_INST_ID_` 自引用）到 SQL Server。自引用执行树 FK：MySQL 渲染 `on delete cascade`（`ACT_FK_EXE_PROCINST` 另含 `on update cascade`），SQL Server 因「多重级联路径」限制渲染为 NO ACTION；引擎删除本身先子后父，不依赖 DB 级联 |
| 注释 | SQL Server 补齐业务表（`workflow_type` / `leave_bill` / `sys_*` / `order_bill` / `workflow_parameters`）的表与列 `MS_Description`，与 MySQL `comment` 一一对应 |
| 约束命名 | SQL Server 统一 `pk_<table>` / `df_<table>_<column>` |
| 顺序依赖 | 索引不再跨脚本引用「后面才建的表」：`ACT_HI_MIGRATION` 的索引放在该表的建表段里，老库由 ⑥ 段幂等补齐 |

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
