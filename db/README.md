# NetBpm 数据库脚本

两套方言（`mysql/` 与 `sqlserver/`）脚本编号一一对应、同步维护。

## 脚本分层

| 编号 | 脚本 | 层 | 说明 |
|---|---|---|---|
| 0001 | `0001_create_schema.sql` | **引擎基础表** | Activiti 子集 13 张 ACT_* 表（执行树 / 任务 / 变量 / 历史 / 部署）+ 索引外键 |
| 0002 | `0002_init_data.sql` | **引擎基础表** | `schema.version` / `next.dbid` 等初值（引擎启动校验依赖） |
| 0003 | `0003_create_process_instance_tables.sql` | **流程实例业务表** | `instances` / `instance_current_task` / `deal_history`（`workflow_type_id` 存流程类型 id） |
| 0008 | `0008_create_workflow_type.sql` | **流程实例业务表** | `workflow_type` 流程类型表（`ProcessInfoEnum` 数据源，启动时加载） |
| 0004 | `0004_create_leave_bill.sql` | Demo 业务表 | 请假单（`days` / `reason` 真列） |
| 0006 | `0006_create_order_bill.sql` | Demo 业务表 | 订单单（`status` 随审批回写 100 / -1） |
| 0007 | `0007_create_workflow_parameters.sql` | Demo 业务表 | 流程参数键值表（「实例 × 参数名」） |
| 0005 | `0005_create_org_tables.sql` | Demo 业务表 | 组织架构（`sys_*` 四张：部门 / 员工 / 审批组） |

> **其它 .NET 项目接入引擎**：必跑 0001 → 0002 → 0003 → 0008（引擎 + 流程实例 + 流程类型）；
> 0004 / 0005 / 0006 / 0007 是 Demo 业务表，按需取舍。

## 执行顺序

`0001 → 0002 → 0003 → 0004 → 0005 → 0006 → 0007 → 0008`

- MySQL：`mysql -u<user> -p <database> < db/mysql/000x_*.sql`（库示例 `netbpm_demo`）；
- SQL Server：脚本统一落 `dbo`（与引擎 ACT_* 表同 schema），先建库后逐个执行。

## 方言差异

- MySQL：`utf8mb4`（业务表）/ `utf8`（引擎表，与上游 Activiti 一致）、`comment` 列注释；
- SQL Server：`nvarchar`、命名约束（`pk_*` / `ix_*`）、`go` 批分隔、
  `if not exists` 保护的索引创建；
- 列结构两方言一一对应（0003 / 0008 等头注释含来源与差异说明）。
