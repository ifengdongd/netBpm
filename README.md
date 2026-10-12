# NetBpm

> 参照 Activiti 定义的 .NET 原生轻量子集流程引擎 —— **BPMN 2.0 定义格式 + Activiti 解析行为 + Activiti 运行时语义**，只实现「顺序 / 分支合并 / 并行同步」这一子集，配一个基于 bpmn-js 的浏览器设计器。

目标平台：**.NET 10 LTS / C# 14** ｜ 数据库：**SQL Server + MySQL** ｜ 状态：**0.1.0 开发中**

---

## 项目定位

NetBpm 不是 Activiti 的移植，也不是从零发明语义，而是**以 Activiti 为行为字典的受控子集实现**：架构与命名向 Activiti 对齐（便于比对、查资料），代码从零编写（异步原生、无 Java 包袱）。

三个层次的对齐：

| 层次 | 内容 |
|---|---|
| L1 定义格式 | BPMN 2.0 XML + Activiti 扩展命名空间，同一份 `.bpmn20.xml` 两端都能跑 |
| L2 解析行为 | 每元素一个 Converter / ParseHandler，同一套校验红线 |
| L3 运行时语义 | ActivityBehavior + Agenda 调度，同一套执行树模型 |

---

## 支持的 BPMN 子集

| 元素 | XML 标签 |
|---|---|
| 开始事件（空） | `startEvent` |
| 结束事件（空） | `endEvent` |
| 顺序流 | `sequenceFlow`（含 `conditionExpression`） |
| 排他网关 | `exclusiveGateway`（含 `default` 默认流） |
| 并行网关 | `parallelGateway`（分叉 / 同步合并） |
| 用户任务 | `userTask`（`assignee` / `candidateUsers` / `candidateGroups` / `formKey` / `dueDate` / `priority`） |
| 服务任务 | `serviceTask`（`class` / `delegateExpression` / `expression` 统一路由） |
| 手工任务 | `manualTask` |
| 图形信息 | `bpmndi:BPMNDiagram` / `BPMNShape` / `BPMNEdge` |

行为类规模：Activiti 58 个 → NetBpm 8 个 + 3 个基类。

**明确不做**（占 Activiti 全部复杂度 70% 以上）：子流程 / 调用活动、边界事件、定时器与消息等事件定义、事件子流程、多实例会签、包容网关、异步作业、泳道协作池、动态流程修改、表单引擎、PNG 图形生成、其余 6 种数据库方言。

---

## 目录结构

```
NetBpm/
├── src/                          # 引擎核心（9 个项目）
│   ├── NetBpm.Bpmn.Model/        # BPMN 2.0 模型对象
│   ├── NetBpm.Bpmn.Converter/    # XML ⇄ 模型 转换器与解析器
│   ├── NetBpm.Bpmn.Validation/   # 定义校验（无出流、缺默认流等红线）
│   ├── NetBpm.El/                # EL 表达式求值
│   ├── NetBpm.Engine/            # 行为类 + Agenda 调度 + 执行树
│   ├── NetBpm.Runtime/           # 运行时服务（部署 / 启动 / 任务 / 历史）
│   ├── NetBpm.Persistence/       # Dapper 数据访问 + ID 区块分配 + 迁移
│   ├── NetBpm.Identity/          # 身份与办理人解析
│   └── NetBpm.Rest/              # ASP.NET Core Minimal API 宿主
├── tests/
│   ├── NetBpm.UnitTests/         # 单元测试
│   ├── NetBpm.IntegrationTests/  # 集成测试（真实 MySQL / Testcontainers）
│   ├── NetBpm.DifferentialTests/ # 差分对拍（与 Java 预言机逐维比对）
│   └── corpus/                   # 自建语料与生成器（脚本生成，禁手改）
├── web/designer/                 # 流程设计器（bpmn-js 18 + Vite 6 + TS）
├── db/
│   ├── mysql/                    # 0001_create_schema.sql / 0002_init_data.sql
│   └── sqlserver/
├── tools/
│   ├── NetBpm.DbMigrate/         # 建库 / 迁移 / 校验 / 清库命令行工具
│   └── Oracle/                   # Java 预言机（产生对拍真值）
└── doc/                          # 开发计划（.md / .html）
```

---

## 快速开始

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)（`global.json` 锁定 `10.0.401`）
- Node.js 22（仅设计器需要）
- MySQL 8.0 或 SQL Server（可选，不配数据库时以内存模式运行）

### 构建与测试

```bash
dotnet restore NetBpm.slnx
dotnet build NetBpm.slnx -c Release

# 单元测试
dotnet test tests/NetBpm.UnitTests/NetBpm.UnitTests.csproj -c Release --no-build

# 差分对拍（不依赖数据库）
dotnet test tests/NetBpm.DifferentialTests/NetBpm.DifferentialTests.csproj -c Release --no-build

# 代码格式检查
dotnet format NetBpm.slnx --verify-no-changes --no-restore
```

### 初始化数据库

```bash
dotnet run --project tools/NetBpm.DbMigrate -- \
  migrate --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;"
```

支持 `migrate` / `check` / `probe` / `purge` 四个子命令，`--provider` 可选 `mysql` 或 `sqlserver`。建表脚本为 13 张 `ACT_*` 表，与 Activiti 表结构对齐。

### 启动 REST 服务

```bash
dotnet run --project src/NetBpm.Rest
```

修改 `src/NetBpm.Rest/appsettings.json`：

```json
{
  "NetBpm": {
    "DatabaseType": "MySql",
    "ConnectionString": "",
    "IdBlockSize": 2500,
    "EnableHistory": false,
    "UseInMemoryExecutionTree": true,
    "AutoMigrate": false,
    "ValidateSchemaOnStartup": true
  }
}
```

`ConnectionString` 留空即内存模式，无需数据库即可体验。OpenAPI 文档：`/openapi/v1.json`。

### 启动设计器

```bash
cd web/designer
npm install
npm run dev      # 开发
npm run build    # 类型检查 + 构建
npm test         # 设计器单元测试
```

---

## REST API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| POST | `/api/designer/deploy` | 部署 BPMN XML 定义 |
| GET | `/api/designer/definition/{id}/xml` | 获取定义 XML |
| POST | `/api/runtime/process-instances` | 启动流程实例 |
| GET | `/api/runtime/process-instances/{id}` | 查询流程实例 |
| GET | `/api/runtime/tasks` | 查询任务列表 |
| POST | `/api/runtime/tasks/{taskId}/claim` | 签收任务 |
| POST | `/api/runtime/tasks/{taskId}/complete` | 完成任务 |
| GET | `/api/runtime/process-instances/{id}/variables` | 查询流程变量 |
| GET | `/api/runtime/process-instances/{id}/diagram` | 获取流程图数据 |
| GET | `/api/runtime/history/process-instances/{id}` | 历史流程实例 |
| GET | `/api/runtime/history/process-instances/{id}/activities` | 历史活动 |
| GET | `/api/runtime/history/process-instances/{id}/tasks` | 历史任务 |

---

## 测试策略

| 类型 | 说明 |
|---|---|
| 单元测试 | 转换器、校验、EL、行为类、调度 |
| 集成测试 | 真实 MySQL 验证表结构与 ID 区块分配；无 MySQL 时自动跳过 |
| 差分对拍 | 与 `tools/Oracle` 的 Java 预言机逐维度比对，真值由 `<stem>.oracle.json` 提供 |
| 语料漂移校验 | `dotnet run --project tests/corpus/generator -- --check`，语料必须由脚本生成 |

---

## 工程约定

- 目标框架 `net10.0`，语言版本 `C# 14`，`Nullable` 与 `ImplicitUsings` 全开
- `TreatWarningsAsErrors` 开启（测试工程除外），`EnforceCodeStyleInBuild` 开启
- 通过 `BannedApiAnalyzers` + `BannedSymbols.txt` 将 `[ThreadStatic]` 变为编译期错误 —— 异步续体间线程静态状态会丢失或串号
- 集中包版本管理（CPM），见 `Directory.Packages.props`
- 包源在 `NuGet.config` 中显式 `<clear />`，仅保留 nuget.org

---

## 相关文档

- 开发计划：`doc/NetBpm-子集流程引擎-开发计划.md`（同目录另有 HTML 版）

---

## 说明

产品命名 `NetBpm` 用于避开 "Activiti" 商标。本项目为独立实现，与 Alfresco / Activiti 官方无隶属关系。
