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
| 用户任务 | `userTask`（`assignee` / `candidateUsers` / `candidateGroups` / `formKey` / `dueDate` / `priority`），支持 `multiInstanceLoopCharacteristics`（会签 / 串签） |
| 服务任务 | `serviceTask`（`class` / `delegateExpression` / `expression` 统一路由） |
| 手工任务 | `manualTask` |
| 脚本任务 | `scriptTask`（`scriptFormat="csharp"`，Roslyn C# Scripting 执行；其它 `scriptFormat` 部署被拒） |
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

### 执行监听器 / 任务监听器 / 事件监听器（已支持）

- **执行监听器 `activiti:executionListener`**：任意流程元素可挂。事件 `start`（进入节点、行为执行前）/
  `take`（穿越顺序流，恰好一次）/ `end`（离开节点前）；**任务监听器 `activiti:taskListener`**：仅用户任务，
- **流程级执行监听器**（挂在流程 extensionElements 上，对齐 Activiti 设计器「流程上配监听器」）：语义是**全局观测** —— 全流程任何节点的 start/end、任何顺序流的 take 都会通知，元素不必逐个配置（请假 Demo 即此用法）。
  事件 `create` / `assignment`（实际指派了办理人时）/ `complete`（删除任务前，提交的变量已写入执行）。
- 实现方式三选一：`class`（.NET 类型名）/ `delegateExpression`（容器服务名，支持 ${name} 形式）/
  `expression`（EL 求值）。契约：执行监听器实现 `IExecutionListener.NotifyAsync`（`execution.EventName`
  为本次事件名）；任务监听器实现 `ITaskListener.NotifyAsync(TaskListenerContext)`。
- **流程级事件监听器 `activiti:eventListener`**：`events` 逗号分隔（`PROCESS_STARTED` / `PROCESS_COMPLETED` /
  `ACTIVITY_STARTED` / `ACTIVITY_COMPLETED` / `TASK_CREATED` / `TASK_COMPLETED`），实现契约
  `IEngineEventListener.OnEventAsync(WorkflowEngineEvent)`。`throwOnFailure=true` 时监听器抛错使命令失败
  （对应 Java 端 `isFailOnException()`），缺省只记警告并继续。
- 监听器实现与服务任务委托走同一注册通道：类上标 `[ServiceTaskName("名字")]` + `AddServiceTaskDelegates` 扫描。
- 部署期两道检查：`listener-configuration` 规则校验事件与实现必填（取值非法拒部署）；
  `ServiceTaskDelegateGuard` 预解析 class / delegateExpression（解析不到宿主起不来）。
- 设计器可配置、保存 → 重开 → 落库往返不丢；顺序流上的 take 监听器同样可配。
- **`activiti:field`（服务任务字段注入）仍不支持**：部署期直接拒绝。
- **`activiti:formProperty`（表单属性）引擎只透传不执行**：解析 / 落库往返保真（设计器「保存 → 重开」回显不丢、部署不被拒）；
  审批应用层（`NetBpm.Workflow`）以它为**表单契约** —— 枚举选项（`activiti:value`）随实例视图一起下发给前端、
  提交值按 `variable` 映射成流程变量供网关取道。

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

### 变量管理（B2.4）

- **任务局部变量**：`SetTaskVariableAsync` / `GetTaskVariableAsync` / `GetTaskVariablesAsync` / `RemoveTaskVariableAsync`（REST：`/api/runtime/tasks/{id}/variables*`）。挂在 `ACT_RU_VARIABLE.TASK_ID_` 行上，随任务完成 / 删除而清理，**不写回流程实例作用域**（与 Activiti 的 `setVariableLocal` 同语义）。
- **变量删除**：`RemoveVariableAsync(processInstanceId, name)`（REST：`DELETE .../variables/{name}`）沿作用域链查找并删除；longString 的字节行一并清理。
- **历史语义（有意为之）**：`ACT_HI_VARINST` 行**只增不删**——删除运行状态不抹审计痕迹（与历史身份关系同一策略）；真库测试钉死该语义。
- 支持 longString（> 4000 字符）与全部标量类型；删除顺序满足 `ACT_FK_VAR_BYTEARRAY`（RESTRICT）外键。
- **REST 写入的类型保真**：裸标量按 `string / number(long→double) / boolean` 推断；需要显式类型
  （尤其是 `date`）时写成 `{"type":"date","value":"2026-10-19T00:00:00Z"}`，支持
  `string / integer / long / double / boolean / date`。
- **null 语义（统一）**：`null` 不是可写变量值 —— 启动 / 完成 / 批量写 / 单写的所有入口都忽略 null
  （单写返回 400），删除变量请用 `DELETE`。这样避免用 null 值制造空变量行。

### 多租户（B2.3）

- `DeployAsync(..., tenantId: "acme")`：租户落到 `ACT_RE_DEPLOYMENT` / `ACT_RE_PROCDEF.TENANT_ID_`；同 key 在不同租户下**版本独立**（各自从 1 开始）。
- 启动实例**自动继承定义的租户**（与 Activiti 一致，启动接口不单独收租户参数），逐层传递到执行（`ACT_RU_EXECUTION`）、任务（`ACT_RU_TASK`）与全部历史行（`ACT_HI_PROCINST` / `ACT_HI_ACTINST` / `ACT_HI_TASKINST`）。
- `FindProcessDefinitionsAsync(tenantId)` 按租户过滤；REST 部署端点接受 `?tenantId=`；任务响应带 `tenantId`。
- 变量行（`ACT_RU_VARIABLE` / `ACT_HI_VARINST`）本子集 DDL 无 `TENANT_ID_` 列，不携带租户（已登记偏差）；无租户部署全链路写 `''`，与 Activiti 行级一致（对拍通过）。

### 多实例会签 / 串签（userTask 的 multiInstanceLoopCharacteristics）

- **只支持用户任务上的多实例**；服务任务 / 手工任务 / 脚本任务配了多实例会被部署期拒绝（`multi-instance` 规则）。
- 实例数来源**二选一**：`<loopCardinality>`（数字或表达式）或 `activiti:collection`（集合表达式）；`activiti:elementVariable` 把集合元素逐实例写入变量（如 `assignee="${approver}"`）。
- `isSequential="true"` 为**串签**（一次一个任务，完成后再建下一个）；缺省为**并行会签**（N 个任务同时创建）。
- `<completionCondition>`（如 `${nrOfCompletedInstances == 2}`）：每次实例完成后判定，为真即提前收口——剩余实例与待办被取消。
- 循环变量（与 Activiti 同名，落在流程实例作用域）：`nrOfInstances` / `nrOfActiveInstances` / `nrOfCompletedInstances` / `loopCounter`（1 起）。
- 与 Activiti 的已登记偏差：`ACT_HI_ACTINST` 每节点 1 行（非每实例 1 行）；并行模式下 `loopCounter` / `elementVariable` 变量只反映最后创建的实例（任务的办理人 / 名称在创建时逐实例求值，不受影响）；实例级 executionListener 不逐实例触发。多实例用户任务不支持 `activiti:skipExpression`（部署期拒绝）。

### 服务任务（ServiceTask）

- 实现方式**三选一**：`activiti:class` / `activiti:delegateExpression` / `activiti:expression`，一个都不填部署失败；填多个不报错但按 class > delegateExpression > expression **静默取第一个**，建议只保留一个。
- `activiti:class` 的含义是 **DI 容器中的注册名**（keyed service / 已注册委托），而非 Java 式反射加载类名 —— 必须在宿主 `IServiceProvider` 中注册后才能运行。
- `activiti:resultVariableName` 可选，用于接收执行结果。
- `activiti:field` 字段注入已支持（`stringValue` 字面量 / `expression` 每次执行时求值），注入到委托实例的同名**可写公共属性或公共字段**（区分大小写）；成员不存在 / 类型不兼容时运行期响亮失败。⚠️ 委托通常是 DI 单例——字段注入只应用于部署期常量，依赖执行上下文的值请在委托内部读流程变量。监听器上的字段注入仍不支持（部署期拒绝）。

### 脚本任务（ScriptTask）

- 只支持 **`scriptFormat="csharp"`**（Roslyn C# Scripting 执行，编译结果按脚本原文缓存）；缺省或其它格式（如 `javascript`）**部署期显式拒绝**——Activiti 对缺省格式回退 JavaScript，本子集不内置 JS 引擎。
- 脚本里可直接使用两个全局对象：
  - `execution` —— 当前执行（完整引擎 API：`GetVariableAsync` / `SetVariableAsync` 等）；
  - `vars` —— 进入脚本时的**流程变量快照**（dynamic，读成员取值；对 `vars.xxx` 赋值**不会**写回流程变量）。
- 变量写回语义与 Activiti 对齐：脚本顶层声明的变量默认**不**写回；`activiti:autoStoreVariables="true"` 时全部存回；`activiti:resultVariable` 存脚本**最后一条顶层表达式的值**。
- 部署期校验：脚本任务必须有 `<script>` 内容；与委托相同的约束 —— 脚本与流程同处一个命令事务，内部不得 await 外部系统 IO。

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
│   ├── NetBpm.Rest/              # ASP.NET Core Minimal API 宿主
│   └── NetBpm.Workflow/          # 审批流应用层：发起/环节/提交/历史 + Hosting 公共类
├── tests/
│   ├── NetBpm.UnitTests/         # 单元测试
│   ├── NetBpm.IntegrationTests/  # 集成测试（真实 MySQL + SqlServer 双库功能矩阵）
│   ├── NetBpm.DifferentialTests/ # 差分对拍（与 Java 预言机逐维比对）
│   └── corpus/                   # 自建语料与生成器（脚本生成，禁手改）
├── samples/
│   └── NetBpm.Demo/              # 合并审批 Demo（订单 + 请假；公共应用层 + 通用端点的完整接入示例）
├── web/designer/                 # 流程设计器（bpmn-js 18 + Vite 6 + TS）
├── db/
│   ├── mysql/                    # 0001 引擎表 / 0002 初始数据 / 0003 流程实例业务表 / 0004-0008 业务表
│   └── sqlserver/                # 同上（两套方言同步维护）
├── docs/
│   └── process-design-guide.md   # 流程设计操作指引（以请假 / 订单流程为例）
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

# 单元测试 / 差分对拍（不依赖数据库）
# ⚠️ 用 dotnet run（MTP 原生方式）：本仓库 dotnet test 会「运行了零个测试」（exit 5），
#    两个入口执行的是同一个测试程序集，CI（.github/workflows/ci.yml）同样用 dotnet run
# 单元测试含「全语料内存回归」（B1.3）：210 条对拍语料在纯内存宿主上跑通，约 0.5 秒，
# 日常改引擎先跑它；行级真值仍由差分对拍裁决
dotnet run --project tests/NetBpm.UnitTests -c Release --no-build
dotnet run --project tests/NetBpm.DifferentialTests -c Release --no-build

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
    "ValidateSchemaOnStartup": true,
    "Security": {
      "ApiKey": "",
      "ApiKeyTenants": [],
      "Jwt": {
        "SigningKey": "",
        "Issuer": "",
        "Audience": ""
      }
    }
  }
}
```

`ConnectionString` 留空即内存模式，无需数据库即可体验。OpenAPI 文档：`/openapi/v1.json`。

**数据库传输安全**：MySQL 连接在未显式关闭 TLS（`SslMode != None/Disabled`）时由客户端锁定
`TlsVersion=TLS 1.2, TLS 1.3`；连接串里显式写 `TLS 1.0/1.1` 会在启动时直接报错。
SQL Server 由 SqlClient 负责（4.0 起已移除 TLS 1.0/1.1 支持），请使用支持 TLS 1.2+ 的实例 ——
只协商 TLS 1.0 的旧实例会触发 .NET「非安全协议」告警，属于应淘汰的部署。
服务器侧同样应收敛协议（MySQL 5.7 默认仍允许 TLSv1/1.1，可在 `my.cnf` 设 `tls_version=TLSv1.2,TLSv1.3`，
不支持 1.3 的版本只写 `TLSv1.2`）。

**时间契约**：响应里的时间字段统一按 UTC 序列化（带 `Z`）。引擎写入的是 UTC，数据库读回
`DateTimeKind` 为 `Unspecified` 时会由 `UtcDateTimeJsonConverter` 补齐，避免浏览器按本地时区解析。

**鉴权**：两种方式可单独或同时启用，任一非空后所有 `/api/**` 请求都必须通过校验，否则 401。

- 服务间：`NetBpm:Security:ApiKey`，请求带 `X-Api-Key: <key>`（未启用 JWT 时也兼容 `Authorization: Bearer <key>`）。
  调用方用 `X-User-Id` / `X-Tenant-Id` 声明操作人与租户：启动实例、认领任务、定义/实例列表会优先使用请求体，
  缺省时回落到这两个头。默认代表可信服务、可跨租户管理；配置 `ApiKeyTenants` 后收敛为服务作用域。
- 用户级：`NetBpm:Security:Jwt:SigningKey`（HS256，至少 32 字节），请求带 `Authorization: Bearer <jwt>`。
  操作人取 `sub`，租户取 `tenant` / `tenant_id`；配置了 `Issuer` / `Audience` 时一并校验，过期令牌一律 401。
  JWT 模式下忽略 `X-User-Id` / `X-Tenant-Id`，身份只来自签名令牌。

**JWT 下的租户与身份边界**：请求体 / 查询参数里的 `startUserId`、`tenantId` 与令牌不一致一律 **403**
（不允许用请求参数冒充他人或跨租户读写）；按 id 访问实例、任务、历史、部署时按令牌租户校验，
不属于本租户的一律 **404**，避免用 403 探测对象是否存在。

**API Key 服务作用域**：`NetBpm:Security:ApiKeyTenants`（字符串数组）非空时，该密钥只能在列出的租户内读写：

- 部署 / 启动实例 / 各类列表的 `tenantId`（请求体或查询参数）必须命中白名单，越界返回 **403**；
  白名单只有一个租户时可省略并自动落到它，列出多个时必须显式指定（避免「全租户」查询）。
- `X-Tenant-Id` 同样必须在白名单内，否则 **403** —— 请求头不能被用来「自证」属于目标租户。
- 按 id 访问实例、任务、历史、部署时，不属于生效租户的一律 **404**。
- 留空保持旧行为：可信服务，可跨租户管理。

**任务级授权**：JWT 用户「完成 / 认领 / 取消认领 / 委派 / 解决委派」时，必须是当前办理人、
候选人（`groups` 声明匹配候选组）或持有 `netbpm.admin` scope，否则 **403**；
「转办 / 修改任务字段 / 增删候选关系」时，必须是当前办理人、所有者或流程实例发起人（或管理员）——
这些动作会改变办理人 / 候选关系，若不校验就能把别人待办改给自己后以办理人身份完成，从而绕过上面的边界。
API Key 模式代表可信服务，不做任务级校验。令牌支持 `groups` / `group`（空格、逗号或 JSON 数组）与
`scope` / `scp` 声明。

**变量读写授权**：JWT 用户读写**流程实例变量**时，必须是发起人，或该实例运行中任务的办理人 / 所有者 /
候选人（含候选组）；读写**任务局部变量**时，必须是该任务的办理人 / 所有者 / 候选人，或流程实例发起人。
不满足一律 **403**。`netbpm.admin` scope 可越过；API Key 模式不做参与人校验。

两者都留空则保持开放（仅限开发环境，此时请求头声明的身份被直接信任）。

**错误码约定**：`400` 参数 / 业务规则拒绝、`403` 身份或租户与令牌不一致、`404` 对象不存在（含跨租户）、
`409` 状态冲突（已认领 / 仍有运行实例 / 乐观锁失败）、`501` 明确未实现、`500` 只返回 traceId，
不回传异常原文。错误体带机器可读 `code`（`validation_error` / `forbidden` / `not_found` /
`optimistic_lock_conflict` / `conflict` / `engine_rule_violation` / `internal_error` …），
调用方应按 `code` 程序化分支，而不是匹配中文文案。

**幂等**：改变状态的请求可带 `Idempotency-Key`。同键同请求回放首次响应（`Idempotency-Replayed: true`），
同键不同请求返回 409。数据库宿主使用 `ACT_IDEMPOTENCY` 表跨实例去重；过期记录由宿主每小时清理一次
（`IdempotencyCleanupService`，启动后延迟 1 分钟首轮执行）。已初始化的库需执行：

```bash
dotnet run --project tools/NetBpm.DbMigrate -- apply --provider mysql \
  --connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;" \
  --script db/mysql/0010_create_idempotency.sql
```

**分页信封**：列表端点默认返回裸数组 + `X-Total-Count`；加 `?envelope=true` 返回
`{ items, total, firstResult, maxResults }`。`maxResults` 统一默认 20、上限 200。

### 启动设计器

```bash
cd web/designer
npm install
npm run dev      # 开发
npm run build    # 类型检查 + 构建
npm test         # 设计器单元测试
```

**设计器地址**：`http://localhost:5273/?modelId=<模型 id>`（`npm run dev` 后浏览器打开）。
画完的流程导出 BPMN XML，放进 `web/designer/examples/`（或宿主的 `processes/` 目录）即可被引擎启动时部署；
两套示例：`order-process.bpmn20.xml`（订单审批）与 `leave-process.bpmn20.xml`（请假审批）。
流程设计指引见 [`docs/process-design-guide.md`](docs/process-design-guide.md)。

### 引擎启动步骤（其它 .NET 项目接入）

1. **建库**：执行 `db/mysql/` 或 `db/sqlserver/` 建表脚本（分层与顺序见 [`db/README.md`](db/README.md)；
   引擎必跑 `0001 → 0002`，流程实例业务表 `0003` 与流程类型表 `0008` 按需；
   REST 宿主启用 HTTP 幂等时还需 `0010_create_idempotency.sql`）；
2. **引用引擎包**：`dotnet pack` 产出见下节「打包分发」，或直接引用 `src/` 各项目；
3. **组合根注册**（详见 [`samples/NetBpm.Demo/README.md`](samples/NetBpm.Demo/README.md)）：

```csharp
builder.Services.AddNetBpm(builder.Configuration);          // 引擎（连接串 / 执行树模式 / schema 校验）
builder.Services.AddNetBpmWorkflow<MySqlWorkflowDataStore>(); // 业务数据端口（五块数据落库）
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly); // 收口委托扫描
```

4. **启动即部署**：`WorkflowProcessBootstrapper.DeployAsync(...)` 把 BPMN 定义灌进引擎
   （`NetBpm.Rest` 宿主亦可 `AutoMigrate=true` 自动建表）；
5. **验证**：`/openapi/v1.json` 查看端点；`ACT_*` 表与业务表开始产生数据。

### 打包分发

```bash
powershell -File tools/pack.ps1 -Configuration Release
```

产物（`artifacts/`，已 gitignore）：

- `nuget/*.nupkg` —— 9 个引擎包（接入入口 `NetBpm.Workflow`，依赖链自动带上；
  `NetBpm.Persistence` 包内含两方言建表脚本）；
- `netbpm-<版本>.zip` —— nupkg + 两方言 SQL 脚本 + 接入说明（`README.txt`），
  供其它 .NET 项目离线引用（本地 NuGet 源：`dotnet nuget add source <目录> -n netbpm`）。

### 运行合并审批 Demo

`samples/NetBpm.Demo` 是「审批流公共能力」的完整接入示例：一个宿主承载**两条流程**
（订单审批 `order_process` + 请假审批 `leave_process`），演示页在根路径，
启动即自动部署 `web/designer/examples/*.bpmn20.xml`，流程信息从 `workflow_type` 表加载。

演示页含七个独立页面：流程发起 / 待办任务列表 / 流程审批 / 业务单查看 / **流程部署**（含
历史版本流程图链接）/ **待办任务转移** / **流程跳转**——两个引擎能力（`SetAssigneeCommand` /
`MoveActivityCommand`，对应 Activiti 的 `setAssignee` / `moveActivityIdTo`）经应用层
`ReassignTaskAsync` / `JumpToNodeAsync` 暴露为 `POST /tasks/{id}/assignee` 与
`POST /instances/{id}/jump`，任何宿主一行接入。审批页 / 业务单页还能打开**流转状态图**
（走过的节点/线标红），部署列表可查看历史版本流程图。

```bash
# 默认 MySQL 宿主（引擎与业务数据一起落 netbpm_demo；JSON 演示模式见 appsettings.json 注释）
dotnet run --project samples/NetBpm.Demo --urls http://localhost:5188
# 打开 http://localhost:5188，从首页发起订单 / 请假申请
```

Demo 的 HTTP 接口（发起 / 实例 / 历史 / 待办 / 提交 / 流程图 / 业务数据核对）见
`docs/process-design-guide.md` §9.1，详细设计见 `samples/NetBpm.Demo/README.md`。

### 在自己的宿主接入审批流（公共类）

流程相关功能已封装为 `NetBpm.Workflow` 的公共类，宿主五步接入：

```csharp
builder.Services.AddNetBpm(builder.Configuration);                  // ① 引擎
builder.Services.AddNetBpmWorkflow<MyWorkflowDataStore>();          // ② 应用层 + 业务数据端口（IWorkflowDataStore）
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly); // ③ 服务任务委托（类上标 [ServiceTaskName]）

app.MapWorkflowEndpoints("/api/workflow", new WorkflowEndpointOptions // ④ 通用流程 HTTP 端点
{
    ProcessKey = _ => "my_process",
    // TransformStartRequest / DecorateInstanceView / DecorateInstanceSummary / ErrorResponseFactory
    // 是业务定制点：单号生成、DTO 摊平、错误响应形状
});

await WorkflowProcessBootstrapper.DeployAsync(app.Services, new WorkflowDeployOptions // ⑤ 启动即部署
{
    ResourcePath = "processes/my-process.bpmn20.xml",
    DeploymentName = "我的流程",
    VerifyAsync = async (sp, engine, definitionId, logger, ct) =>
    {
        await ApprovalResultFormGuard.VerifyAsync(engine, definitionId, logger, ct);   // 可选自检钩子
        await ServiceTaskDelegateGuard.VerifyAsync(sp, engine, definitionId, logger, ct);
    },
}, app.Logger);
```

核心类型一览：

| 公共类 | 职责 |
|---|---|
| `IWorkflowService` / `WorkflowService` | 发起申请 / 获取环节信息 / 提交审批 / 审批历史四件事的应用层门面 |
| `WorkflowEndpoints` + `WorkflowEndpointOptions` | 通用流程 HTTP 端点（路由约定统一，业务语义经选项注入） |
| `WorkflowProcessBootstrapper` + `WorkflowDeployOptions` | 启动即部署 BPMN 资源 + 部署期自检钩子 |
| `WorkflowRuntimeContext` / `WorkflowNodeRegistry` | 部署后运行期上下文：流程定义 id + 环节名 ↔ 环节 id 对照表（随 ② 自动注册） |
| `IWorkflowDataStore` | 应用层与宿主之间唯一的缝：实例登记 / 审批结论 / 流程参数 / 审批记录 / 当前待办 |
| `JsonWorkflowDataStore` | 端口的**参考实现**：五个区块直接落一个 JSON 文件（零落库跑通全链路，换真库时数据形状不变） |
| `WorkflowCompletionDelegate` | 「结束收口」委托基类：派生一个类只需声明 `[ServiceTaskName]` 与结论名 |
| `IOrgDirectory` / `InMemoryOrgDirectory` | 组织架构查询（部门 / 员工 / 按候选组找审批人）+ 内置演示实现；真库实现参照 Demo 的 `MySqlOrgDirectory` |
| `ApprovalFormGuard` | 部署期自检：审批表单约定（`approvalResult` 枚举 ↔ `result` 变量） |
| `ListenerStats` | 监听器触发计数（端到端断言「监听器真的被调到了」） |

多个流程接入同一宿主：② ③ ④ 各自传入流程 key / 资源路径即可重复部署；
`WorkflowRuntimeContext.NodeRegistry` 按流程定义 id 分表缓存，互不干扰。

服务任务委托里写结论只需一句话：

```csharp
[ServiceTaskName("my.ApproveComplete")]
public sealed class ApproveCompleteDelegate : IServiceTaskDelegate
{
    public ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default) =>
        _workflow.CompleteAsync(execution, WorkflowOutcomes.Approved, ct);
}
```

---

## REST API

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| GET | `/health/live` | 存活探针（只判断进程） |
| GET | `/health/ready` | 就绪探针（数据库不可用返回 503） |
| GET | `/api/admin/info` | 运维信息（版本 / 方言 / 历史开关 / 结构版本 / 运行时长） |
| GET | `/openapi/v1.json` | OpenAPI 文档 |
| POST | `/api/designer/deploy` | 部署 BPMN XML 定义（`?resourceName=` / `?deploymentName=` / `?tenantId=`） |
| POST | `/api/designer/validate` | 试校验 BPMN XML（不落库，返回 errors / warnings） |
| GET | `/api/designer/definitions` | 定义列表（`tenantId`/`key` 过滤 + 分页） |
| GET | `/api/designer/definitions/{key}/versions` | 按 key 查全部版本（版本倒序） |
| GET | `/api/designer/deployments` | 部署列表（按部署聚合定义，`tenantId` 过滤） |
| GET | `/api/designer/deployments/{deploymentId}/resources` | 部署资源清单（资源名 → 定义 id） |
| GET | `/api/designer/definition/{id}/xml` | 获取定义 XML |
| POST | `/api/designer/definitions/{id}/suspend` | 挂起定义（`?suspendProcessInstances=`） |
| POST | `/api/designer/definitions/{id}/activate` | 激活定义（`?activateProcessInstances=`） |
| DELETE | `/api/designer/deployments/{deploymentId}` | 删除部署（`?cascade=`） |
| POST | `/api/runtime/process-instances` | 启动流程实例 |
| GET | `/api/runtime/process-instances` | 运行中实例列表（业务主键 / 定义 / 发起人 / 时间过滤 + 分页） |
| GET | `/api/runtime/process-instances/{id}` | 查询流程实例 |
| DELETE | `/api/runtime/process-instances/{id}` | 删除（撤销）实例 |
| POST | `/api/runtime/process-instances/{id}/suspend` `/activate` | 挂起 / 激活实例 |
| POST | `/api/runtime/process-instances/{id}/jump` | 跳转到指定节点（管理动作） |
| GET | `/api/runtime/tasks` | 查询任务列表（`assignee`/`candidateUser`/`candidateGroup`/实例 + 分页） |
| GET | `/api/runtime/tasks/{taskId}` | 任务详情（含描述 / 分类 / 业务主键 / 委派状态 / 身份关系） |
| GET | `/api/runtime/tasks/{taskId}/identity-links` | 任务身份关系（候选人 / 候选组） |
| PATCH | `/api/runtime/tasks/{taskId}` | 修改任务字段（名称 / 描述 / 优先级 / 到期 / 分类 / 所有者；`clearDueDate` / `clearDescription` 显式清空） |
| POST | `/api/runtime/tasks/{taskId}/assignee` | 转办 / 退回未分配 |
| POST | `/api/runtime/tasks/{taskId}/candidate-users` | 加候选用户 |
| DELETE | `/api/runtime/tasks/{taskId}/candidate-users/{userId}` | 移除候选用户 |
| POST | `/api/runtime/tasks/{taskId}/candidate-groups` | 加候选组 |
| DELETE | `/api/runtime/tasks/{taskId}/candidate-groups/{groupId}` | 移除候选组 |
| POST | `/api/runtime/tasks/{taskId}/delegate` | 委派（PENDING，期间不能完成） |
| POST | `/api/runtime/tasks/{taskId}/resolve` | 解决委派（回到原办理人） |
| GET | `/api/runtime/tasks/{taskId}/form` | 表单元数据（`formKey` + `formProperty`，只读） |
| POST | `/api/runtime/tasks/{taskId}/claim` | 签收任务（认领人取自 JWT `sub` / `X-User-Id` / 请求体 `userId`；解析不出 → 400） |
| POST | `/api/runtime/tasks/{taskId}/unclaim` | 取消签收（显式端点；`claim` 的 `{"unclaim":true}` 兼容旧客户端） |
| POST | `/api/runtime/tasks/{taskId}/complete` | 完成任务 |
| GET | `/api/runtime/tasks/stats` | 待办统计（总数 / 逾期 / 今天到期 / 按流程 key 分组；`timeZoneOffsetMinutes` 指定「今天」的时区） |
| POST | `/api/runtime/tasks/batch/assignee` | 批量转办（逐项结果，非原子） |
| POST | `/api/runtime/process-instances/batch/delete` | 批量删除实例（逐项结果，非原子） |
| GET | `/api/runtime/process-instances/{id}/variables` | 查询流程变量 |
| PUT | `/api/runtime/process-instances/{id}/variables/{name}` | 写流程变量（请求体即 JSON 值） |
| PUT | `/api/runtime/process-instances/{id}/variables` | 批量写流程变量（同一事务） |
| DELETE | `/api/runtime/process-instances/{id}/variables/{name}` | 删除流程变量（历史行保留） |
| GET | `/api/runtime/tasks/{taskId}/variables` | 查询任务局部变量 |
| PUT | `/api/runtime/tasks/{taskId}/variables/{name}` | 写任务局部变量（请求体即 JSON 值） |
| DELETE | `/api/runtime/tasks/{taskId}/variables/{name}` | 删除任务局部变量 |
| GET | `/api/runtime/process-instances/{id}/diagram` | 获取流程图数据 |
| GET | `/api/runtime/history/process-instances` | 历史实例列表（业务主键 / 定义 / 发起人 / 时间 / 结束状态 + 分页） |
| GET | `/api/runtime/history/process-instances/{id}` | 历史流程实例 |
| GET | `/api/runtime/history/process-instances/{id}/activities` | 历史活动 |
| GET | `/api/runtime/history/process-instances/{id}/tasks` | 历史任务 |
| GET | `/api/runtime/history/process-instances/{id}/variables` | 历史变量（含已删除的运行态变量） |
| GET | `/api/runtime/history/tasks` | 跨实例已办（`assignee` / 定义 / 节点 / 时间过滤 + 分页） |
| GET | `/api/identity/users/{userId}/groups` | 用户所属组（只读，依赖 `IUserGroupManager`） |
| GET | `/api/identity/groups/{groupId}` | 组信息（未接入身份源时 404） |
| GET | `/api/identity/groups/{groupId}/users` | 组成员 id（空集是合法结果） |

列表端点统一使用 `firstResult` / `maxResults`（默认 20、上限 200），总数放在 `X-Total-Count` 响应头；
加 `?envelope=true` 则返回 `{ items, total, firstResult, maxResults }`。
历史表只增不删：运行态变量删除后，`/history/.../variables` 仍能查到最后一次的值。
任务详情返回 `revision`；完成 / 认领时可带 `expectedRevision`，版本不一致返回 **409 Conflict**（防止重复提交）。
改变状态的请求可带 `Idempotency-Key` 做重试去重（见「启动 REST 服务」）。

---

## 测试策略

| 类型 | 说明 |
|---|---|
| 单元测试 | 转换器、校验、EL、行为类、调度 |
| 集成测试 | 真库验证表结构 / ID 区块 / 全部 B1·B2 功能：**MySQL（localhost）+ SqlServer（Testcontainers `mssql/server:2022`，可用 `NETBPM_SQLSERVER_CONNECTION` 改指已有实例）双库功能矩阵**——库与表自动创建迁移；方言不可用（Docker 未运行 / 实例连不上）时对应测试自动跳过（env：`NETBPM_MYSQL_CONNECTION` / `NETBPM_SQLSERVER_CONNECTION`） |
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

- 流程设计操作指引（以请假流程为例，含表达式写法、部署红线、应用层封装）：`docs/process-design-guide.md`
- 开发计划：`doc/NetBpm-子集流程引擎-开发计划.md`（同目录另有 HTML 版）

---

## 说明

产品命名 `NetBpm` 用于避开 "Activiti" 商标。本项目为独立实现，与 Alfresco / Activiti 官方无隶属关系。
