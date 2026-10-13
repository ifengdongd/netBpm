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
| 包含网关 | `inclusiveGateway`（条件分叉 / 按入流数同步合并，含 `default` 默认流） |
| 事件网关 | `eventBasedGateway`（**不可用**：可解析、可配置，部署期显式拒绝） |
| 用户任务 | `userTask`（`assignee` / `candidateUsers` / `candidateGroups` / `formKey` / `dueDate` / `priority`） |
| 服务任务 | `serviceTask`（`class` / `delegateExpression` / `expression` 统一路由） |
| 手工任务 | `manualTask` |
| 脚本任务 | `scriptTask`（**不可用**：可解析、可配置，运行期抛「二期实现」，见 §1.2） |
| 图形信息 | `bpmndi:BPMNDiagram` / `BPMNShape` / `BPMNEdge` |

行为类规模：Activiti 58 个 → NetBpm 9 个 + 3 个基类。

**明确不做**（占 Activiti 全部复杂度 70% 以上）：子流程 / 调用活动、边界事件、定时器与消息等事件定义、事件子流程、多实例会签、异步作业、泳道协作池、动态流程修改、表单引擎、PNG 图形生成、其余 6 种数据库方言。

---

## 流程图设置注意事项

以下要求来自 `ProcessValidationRules`（后端部署期强制校验）与设计器属性面板（前端即时校验，仅作镜像，**以后端报错为准**）。

### 流程（Process）

- **流程 ID（key）必填**：为空部署直接失败。设计器侧要求 `^[A-Za-z_][A-Za-z0-9_-]*$`，即以字母或下划线开头，只含字母、数字、下划线与短横。
- 部署后流程定义 id 形如 `{key}:{version}:{id}`，其中的 key 就是流程 ID，后续按 key 启动流程实例。
- **流程名称（name）可选**，无长度与字符限制。
- 只支持**恰好一个**开始事件（多个会被拒绝部署）与至少一个结束事件。
- `activiti:initiator` 可用；开始/结束事件必须为空事件，不支持定时器、消息、信号等事件定义。
- 设计器属性面板为白名单接管，仅暴露子集支持的属性；documentation、事件定义、candidateStarter 等填了也无效。

### 元素 ID

- 所有元素 id 非空且**全局唯一**（区分大小写）。
- 顺序流的 `sourceRef` / `targetRef` 必须能解析到流程节点，否则部署失败。

### 执行监听器 / 任务监听器 / 事件监听器（重要）

- XML 可解析、设计器可配置，但**当前引擎执行未实现，部署期会被直接拒绝**：
  - `activiti:executionListener`（配置于流程、用户任务、服务任务、顺序流）
  - `activiti:taskListener`（仅用户任务，事件 `create` / `assignment` / `complete` / `delete`）
  - `activiti:field`（服务任务字段注入）
  - `activiti:eventListener`（后端不识别，设计器仅给警告）
  - `activiti:formProperty`（表单属性，后端不识别，设计器仅给警告）
- 设计器中执行监听器事件下拉为 `start` / `end` / `take`（顺序流默认 `take`，其余默认 `start`），配置方式支持 `activiti:class` / `activiti:expression` / `activiti:delegateExpression`。
- **在监听器放行之前，请勿在正式流程中使用**；设计器会给出「部署前请确认后端校验已放开」的警告。

### 线（顺序流 / SequenceFlow）

- 除结束事件外每个节点必须有出流；除开始事件外每个节点必须有入流（孤立节点拒绝部署）。
- 排他网关（单一网关）：出流必须**全部带条件时设置默认流（default）**，或至少保留一条无条件出流，否则条件全为假时流程会卡住。
- 包含网关：至少一入一出；与排他网关同理，**无默认流时不能所有出流都带条件**（分叉取所有条件为真的出流，条件全假同样无流可走）。合并时按入流数同步等待，入流为 0 也会被拒。
- 并行网关：至少一入一出，**不支持默认流**（配了 `default` 部署被拒）；出口不校验条件。
- 事件网关：本子集未实现（后续必须是中间捕获事件：定时/消息/信号），**部署期显式拒绝**，请改用单一网关或包含网关。
- 条件表达式必须写成 `${...}` 或 `#{...}`（`<conditionExpression xsi:type="tFormalExpression">`），否则后端按字面量处理，不当作表达式求值；设计器会对不合规的条件给出警告。

### 用户任务（UserTask）

- `assignee` / `candidateUsers` / `candidateGroups` / `owner` / `formKey` / `category` 均支持表达式（如 `${starter}`）；`candidateUsers` / `candidateGroups` 多个值用**英文逗号**分隔。
- **办理人不是必填**：无人办理的任务照常创建，只是按办理人查不到（仅警告）。
- `dueDate`：支持表达式，求值结果须为日期或 **ISO-8601 日期串**（如 `2030-01-02T03:04:05`），否则运行期报错；不支持业务日历。
- `priority`：**整数**（支持表达式，结果须可转 int），无上下限约束。
- `skipExpression`：求值结果为布尔 true 时跳过任务。

### 服务任务（ServiceTask）

- 实现方式**三选一**：`activiti:class` / `activiti:delegateExpression` / `activiti:expression`，一个都不填部署失败；填多个不报错但按 class > delegateExpression > expression **静默取第一个**，建议只保留一个。
- `activiti:class` 的含义是 **DI 容器中的注册名**（keyed service / 已注册委托），而非 Java 式反射加载类名 —— 必须在宿主 `IServiceProvider` 中注册后才能运行。
- `activiti:resultVariableName` 可选，用于接收执行结果。
- `activiti:field` 字段注入不支持，配置后部署被拒。

### 脚本任务（ScriptTask）

- 设计器可拖拽、属性面板可配置 `scriptFormat` / `script` / `resultVariableName` / `autoStoreVariables`，XML 可正常解析部署。
- 但**引擎执行尚未实现**（计划二期用 Roslyn Scripting）：运行到脚本任务会直接抛「未实现」异常，脚本不会执行。
- 设计器对没有脚本正文的脚本任务给出警告。**正式流程请暂勿使用**，可用服务任务（`expression` / `class`）替代。

### BPMN XML 整体要求

- 必须保留 **Activiti 扩展命名空间 `http://activiti.org/bpmn`**，assignee、candidateUsers、class 等扩展属性均按此命名空间读取。
- `targetNamespace` 可选，导出为空时自动回填默认命名空间。
- 子集外的 BPMN 元素会**显式拒绝部署**（提示「不在本子集支持范围内」）；仅 `extensionElements`、`documentation`、`textAnnotation`、`association` 等被容忍并忽略。
- 图形信息（`bpmndi:BPMNDiagram`）**可选**，没有也能部署；XML 必须格式合法（禁止 DTD），否则解析失败。

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
