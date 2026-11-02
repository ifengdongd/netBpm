# NetBpm

> 参照 Activiti 定义的 .NET 原生轻量子集流程引擎 —— **BPMN 2.0 定义格式 + Activiti 解析行为 + Activiti 运行时语义**，只实现「顺序 / 分支合并 / 并行同步」这一子集，配一个基于 bpmn-js 的浏览器设计器。

目标平台：**.NET 10 LTS / C# 14** ｜ 数据库：**SQL Server + MySQL** ｜ 状态：**0.1.3**

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
| 开始事件（空） | `startEvent`（定时/消息启动仍不支持） |
| 结束事件 | `endEvent`（空事件 / **终止结束事件** `terminateEventDefinition`） |
| 边界定时事件 | `boundaryEvent` + `timerEventDefinition`（**中断型、一次性**，只能挂在用户任务上） |
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

行为类规模：Activiti 58 个 → NetBpm 12 个 + 3 个基类。

**明确不做**（占 Activiti 全部复杂度 70% 以上）：子流程 / 调用活动、**非中断型边界事件**、定时/消息启动事件、中间捕获/抛出事件、消息 / 信号 / 错误 / 补偿事件、事件子流程、**用户任务以外的多实例**（服务/脚本/手工任务）、泳道协作池、动态流程修改、表单引擎、PNG 图形生成、其余 6 种数据库方言。
（**独占异步作业 `asyncBefore` / `asyncAfter` 已支持**，见「异步作业与事务性 outbox」；并发异步作业 `exclusive="false"` 仍不支持。）
（用户任务的多实例会签/串签**已支持**，见下文「多实例会签 / 串签」；中断型定时边界事件与终止结束事件**已支持**，见「边界定时器」与「终止结束事件」。）

---

## 流程图设置注意事项

以下要求来自 `ProcessValidationRules`（后端部署期强制校验）与设计器属性面板（前端即时校验，仅作镜像，**以后端报错为准**）。

### 流程（Process）

- **流程 ID（key）必填**：为空部署直接失败。设计器按 `^[A-Za-z_][A-Za-z0-9_-]*$` 即时提示
  （字母或下划线开头，只含字母、数字、下划线与短横）；后端对不符合该习惯的 id 只告警、不阻断
  （Activiti 允许任意 id，对拍语料里就有 `myProcess{0}` 这类模板 id）。
- 部署后流程定义 id 形如 `{key}:{version}:{id}`，其中的 key 就是流程 ID，后续按 key 启动流程实例。
- **流程名称（name）可选**，无长度与字符限制。
- 只支持**恰好一个**开始事件（多个会被拒绝部署）与至少一个结束事件。
- `activiti:initiator` 可用；**开始事件必须为空事件**，定时/消息/信号启动事件部署期显式拒绝
  （`unsupported-event-definition`）—— 不会出现「部署成功但定时语义丢失」。要表达定时触发，请在宿主侧调度并调用启动接口。
- **结束事件支持两种**：空结束事件（正常结束）与 `terminateEventDefinition`（立即终止整个实例，
  取消并行/会签分支，见下文「终止结束事件」）；其它事件定义仍拒绝。
- 流程 id（key）非空（`process-id` 规则）；不符合标识符习惯（如含冒号）只提示，不阻断（Activiti 允许任意 id）。
- **一个 BPMN 文件只能有一个 `isExecutable="true"` 的流程**（不写 `isExecutable` 时按可执行处理）：
  多个会被部署/校验显式拒绝（非可执行流程不参与部署）。
- 设计器属性面板为白名单接管，仅暴露子集支持的属性；documentation、子集外事件定义、candidateStarter 等填了也无效。

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
  （对应 Java 端 `isFailOnException()`），缺省继续流程 —— 但失败**不再静默**：计入指标
  `netbpm.listener.failure`（tag：`event` / `kind`）并记 warning，宿主没注入 `ILogger` 时直接抛出。
  「事件没送达、流程照常走」是最难查的一类偏差：要么给监听器开 `throwOnFailure`，要么对这条指标配告警。
- 监听器实现与服务任务委托走同一注册通道：类上标 `[ServiceTaskName("名字")]` + `AddServiceTaskDelegates` 扫描。
- 部署期两道检查：`listener-configuration` 规则校验事件与实现必填（取值非法拒部署）；
  `ServiceTaskDelegateGuard` 预解析 class / delegateExpression（解析不到宿主起不来）。
- 设计器可配置、保存 → 重开 → 落库往返不丢；顺序流上的 take 监听器同样可配。
- **`activiti:field`（服务任务字段注入）已支持**（`stringValue` / `expression`）；**监听器上的字段注入仍不支持**，
  部署期由 `unsupported-capability` 规则拒绝。
- **任务监听器的 `delete` 事件不支持**：引擎各条删除路径都没有通知点，部署期由 `listener-configuration` 规则拒绝
  （可用 `complete` 事件或宿主侧逻辑替代）。
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
- **默认流必须指向本节点的出流**（写错 id / 指向别的节点的流由 `default-flow` 规则拒绝）；
  **默认流上不要写条件** —— Activiti 语义下它不参与判定，规则会给提示。
- **所有表达式属性在部署期预解析**（`expression-parseable`）：条件、办理人、`dueDate`、`priority`、
  `skipExpression`、多实例的实例数/集合/完成条件、监听器表达式，语法错误部署即拒绝，不会等到运行期。

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
- 历史活动行（`ACT_HI_ACTINST`）**每实例一行**（对齐 Activiti）：串签在同一执行上依次开合；会签每个实例一行（另加主执行的容器行），实例完成即收口、提前完成时剩余行记录取消原因。并行模式下 `loopCounter` / `elementVariable` 变量只反映最后创建的实例（任务的办理人 / 名称在创建时逐实例求值，不受影响）；实例级 executionListener 不逐实例触发。多实例用户任务不支持 `activiti:skipExpression`（部署期拒绝）。

### 边界定时器（boundaryEvent + timerEventDefinition）

```xml
<bpmn:userTask id="task_approve" name="经理审批" activiti:assignee="${manager}" />
<bpmn:boundaryEvent id="timer_approve" attachedToRef="task_approve" cancelActivity="true">
  <bpmn:timerEventDefinition>
    <bpmn:timeDuration>PT48H</bpmn:timeDuration>
  </bpmn:timerEventDefinition>
</bpmn:boundaryEvent>
<bpmn:sequenceFlow id="flow_timeout" sourceRef="timer_approve" targetRef="task_escalate" />
```

- **只支持中断型（`cancelActivity="true"`）一次性定时器，且只能挂在用户任务上**：
  非中断型（催办）、`timeCycle`（重复/cron）、挂在服务/脚本任务上都会被部署期规则
  `boundary-event-configuration` 拒绝。
- `timeDuration`（ISO-8601 时长，如 `PT30M` / `PT2H` / `P1D`）与 `timeDate`
  （ISO-8601 时间点，如 `2026-10-20T09:00:00Z`）**二选一**；都可以写成表达式（`${timeout}`），
  在**任务创建时**求值 —— 发起人可以在表单里决定"几小时不处理就转办"。
- 到点后的语义：删除待办（`ACT_HI_TASKINST.DELETE_REASON_ = boundary timer`）、
  收口该用户任务的历史活动行（`ACT_HI_ACTINST` 结束时间 + 删除原因）、沿边界事件出流继续。
- **作业存储**：`ACT_RU_TIMER_JOB`（`db/{dialect}/netbpm_init.sql`）。任务完成 / 跳转 /
  实例结束 / 终止时作业一并删除；任务先完成则到点作业自动作废。
- **调度**：宿主后台服务轮询（`TimerJobSchedulerService`），配置节：

  ```json
  "NetBpm": { "Timer": { "Enabled": true, "PollIntervalMs": 5000, "BatchSize": 50 } }
  ```

  默认 5 秒一轮；多宿主同时轮询也不会重复触发（作业以条件删除抢占），但建议只在一个宿主启用。
- 时钟口径：一律 UTC，服务端当前时间；**不支持工作日历 / 时区平移**（Activiti 的
  `businessCalendarName` 不在子集内）。
- 设计器：选中用户任务 → 调色板「边界定时器」→ 在右侧属性面板改时长/时间点。

### 异步作业（asyncBefore / asyncAfter）

```xml
<serviceTask id="call_erp" activiti:delegateExpression="erpDelegate"
             activiti:asyncBefore="true" activiti:asyncAfter="true" />
```

- `asyncBefore`：令牌到达活动时**不执行行为**，只落一条 `ACT_RU_JOB`；命令提交后由
  `AsyncJobExecutorService` 在独立事务里执行。「进入活动」的历史、任务创建、服务调用都发生在作业事务中。
- `asyncAfter`：活动行为在当前命令内执行完，但「离开节点」推迟到作业（历史结束、节点 end 监听器、出流都在作业事务中）。
- 两个属性可同时使用：先 `before` 作业，再 `after` 作业，各一个事务。
- 作业语义：条件删除抢占（多宿主只会执行一次）、失败按 5/10/20… 秒退避、3 次后进死信；
  死信可人工重试/删除（见管理端 API）。**至少一次**：作业重试会重复调用委托，外部副作用需幂等。
- 配置：

  ```json
  "NetBpm": {
    "Jobs": { "Enabled": true, "BatchSize": 50, "PollIntervalSeconds": 2 }
  }
  ```

- 限制：只支持独占作业；多实例活动与 `exclusive="false"` 在部署期拒绝（`async-job` 规则）。
- 宿主未注册 `IAsyncJobManager`（`AddNetBpm` 会注册）或未启动执行器时，作业会停留在表里，
  流程停在该节点；可调用 `IProcessEngine.ExecuteDueAsyncJobsAsync` 手动推进。

### 事务性 outbox（事件外发）

开启后（`NetBpm:EnableOutbox=true`），每条引擎事件在**业务命令的同一事务**里写入
`ACT_EVT_OUTBOX`，由 `OutboxPublisherService` 投递给宿主注册的 `IOutboxEventHandler`：

```csharp
builder.Services.AddSingleton<IOutboxEventHandler, WebhookOutboxHandler>(); // 可注册多个
builder.Services.AddNetBpmOutboxPublisher(builder.Configuration);
```

- 事件类型与 `activiti:eventListener` 一致：`PROCESS_STARTED` / `PROCESS_COMPLETED` /
  `ACTIVITY_STARTED` / `ACTIVITY_COMPLETED` / `TASK_CREATED` / `TASK_COMPLETED`。
- 载荷是 JSON（事件类型 + 各类 id），不含变量；订阅方按 `processInstanceId` / `taskId` 回查。
- **至少一次**：以 `OutboxEvent.Id` 做幂等；发布失败按 5/10/20… 秒退避，5 次后进死信。
- 关闭时（默认）不产生任何写入，事件只走进程内 `activiti:eventListener`。
- 运维 API：`GET /api/admin/outbox`、`POST /api/admin/outbox/{id}/retry`、`DELETE /api/admin/outbox/{id}`。

### 终止结束事件（terminateEventDefinition）

```xml
<bpmn:endEvent id="end_terminate">
  <bpmn:terminateEventDefinition />
</bpmn:endEvent>
```

- 到达即**终止整个流程实例**：删除执行树、取消全部待办（含并行分支与会签剩余实例）、收口历史。
- `ACT_HI_PROCINST.DELETE_REASON_ = terminate end event`，`END_ACT_ID_` 指向该结束事件；
  **不触发** `PROCESS_COMPLETED`（被终止不是正常完成）。
- **不能有出流**（部署期 `terminate-end-event` 规则拒绝）：终止后任何节点都不会执行。
- 与"删除流程实例"的区别：终止是流程内部的一条合法路径（有历史、有原因），删除是运维动作。
- 设计器：选中结束事件 → 勾选「终止结束事件」。

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

### 实例迁移（定义升级后的存量实例）

流程定义不可变：改图再部署 = 新版本，存量实例仍在旧版本上。迁移把运行中的实例切到同 key 的新版本：

```http
POST /api/runtime/process-instances/{id}/migrate
{
  "targetProcessDefinitionId": "leave:3:90001",
  "activityMappings": { "task_leader_approve": "task_manager_approve" },
  "reason": "审批环节合并",
  "validateOnly": true
}
```

- **先预演**（`validateOnly=true`）：只读返回活动映射、待取消待办数与风险提示；再正式提交。
- 规则：同 key / 同租户；实例与目标定义都未挂起；**有待办的执行只能迁到用户任务**；
  停在网关 Join 上的非活动执行只能迁到同类型网关；**会签/串签节点不支持迁移**；
  目标定义存在同 id 且类型兼容的节点时默认按同 id 迁移，只需列 id 变化的映射。
- 一个事务内完成：切整棵执行树的 `PROC_DEF_ID_` → 取消旧待办（留删除原因）→ 作废旧边界定时器 →
  收口旧活动行 → 按新定义重入目标节点（重建待办/办理人/表单/定时器）→ 写审计。
- **待办 id 会变**、**定时器重新计时**、**节点 start 监听器会再跑一次**（有外部副作用需幂等）。
- 历史口径：`ACT_HI_PROCINST.PROC_DEF_ID_` 保持启动时的定义；迁移事实查
  `GET /api/runtime/process-instances/{id}/migrations`（`ACT_HI_MIGRATION`）。
- 支持回退迁移（再指回旧版本）；禁止用 SQL 直接改 `PROC_DEF_ID_` / `ACT_ID_`。
- `ACT_HI_MIGRATION` 由初始化脚本（`db/<dialect>/netbpm_init.sql`）建表；已有库重复执行该脚本会自动补齐缺失表（幂等，见 [`db/README.md`](db/README.md)）。

### 流程模型治理（草稿 / 版本 / 回滚 / 发布）

设计器的服务端模型（`bpmn_model`）是**草稿**，与部署结果（`ACT_RE_PROCDEF`）分离：

| 动作 | 接口 | 语义 |
|---|---|---|
| 新建 | `POST /api/admin/models` | 服务端生成 id，`201` 返回 `revision`；同一租户 `process_key` 唯一（重名 409） |
| 保存草稿 | `PUT` / `PATCH` `/api/admin/models/{id}` | PUT 全量 upsert；**PATCH 只覆盖提交的字段**（设计器走 PATCH，不会清空描述/SVG） |
| 复制 / 导入 / 反建 | `POST /api/admin/models/{id}/duplicate`、`/models/import`、`/models/from-definition/{defId}` | 副本自动换 key（`{key}_copy`）并同步改写 XML 的 `process/@id` |
| 并发控制 | 保存 / 回滚 / 发布 + `If-Match: "<revision>"` | 版本不一致 **409** + 当前 `currentRevision`；不带该头保持旧行为（兼容旧客户端） |
| 版本历史 | `GET /api/admin/models/{id}/history` | 分页（默认 20、上限 200，`?envelope=true` 返回信封）；每次保存/回滚/发布追加一版 |
| 版本详情 | `GET /api/admin/models/{id}/history/{revisionId}` | 取该版 XML/SVG（用于对比） |
| 回滚 | `POST /api/admin/models/{id}/rollback/{revisionId}` | 把旧版内容作为**新版本**写回（历史只增不删） |
| 发布前预检 | `POST /api/admin/models/{id}/validate` | 与发布同口径的解析 + 结构/表达式校验，返回 `errors/warnings`，不落库 |
| 发布 | `POST /api/admin/models/{id}/publish` | 用当前 XML 走引擎部署：**部署期校验强制拦截**，成功返回定义 id/版本；`{"skipIfUnchanged":true}` 时内容没变直接回放线上版本（`skipped=true`），不再多造定义版本 |
| 发布历史 | `GET /api/admin/models/{id}/publications` | 每次发布的 `revision` / 定义 id / 版本 / 部署 id / 时间 / 操作人 |
| 清理历史 | `DELETE /api/admin/models/{id}/history?keepRevisions=` | 按保留版本数裁剪（需要 `netbpm.admin.model.write`） |

- 保存与历史写入在**同一事务**内，不会出现「模型更新了、历史没记」。
- 草稿可含阻断级问题（方便保存半成品），但**发布会 400 并逐条返回问题**。
- 「模型是否已发布 / 有无未发布修改」由版本水位计算：最新 revision 与最近一次 `action=publish` 快照比较，列表接口一并返回。
- 同一租户下 `process_key` 唯一（`ux_bpm_model_key`）：由初始化脚本建表 / 建索引，已有库重复执行脚本会自动补齐；
  新建 / 改 key / 导入 / 复制撞名都会 409，并指出占用该 key 的模型 id。
- `bpmn_model_history`（模型版本历史）同样由初始化脚本建表，重复执行幂等。

### 管理治理：细粒度授权 / 审计 / 指标与归档

**① 细粒度管理 scope**

`netbpm.admin` 仍是超级范围（隐含全部子权限，旧令牌不受影响）；另可单独授予：

| scope | 覆盖的动作 |
|---|---|
| `netbpm.admin.model.read` | 读模型 / 版本历史 / 流程类型；设计器定义列表、部署列表、回显 XML |
| `netbpm.admin.model.write` | 保存 / 删除 / 回滚 / 发布模型，增删改流程类型；部署定义、定义级挂起 / 激活、删部署 |
| `netbpm.admin.task.moderate` | **越权**转办、改任务字段、增删候选关系 |
| `netbpm.admin.instance.jump` | 跳转节点、迁移到其它定义 |
| `netbpm.admin.instance.delete` | 删除（撤销）运行中的实例，含批量删除 |
| `netbpm.admin.instance.suspend` | 挂起 / 激活实例 |
| `netbpm.admin.variables.write` | **越权**直写 / 删除实例变量与任务局部变量 |
| `netbpm.admin.metrics.read` | 管理指标 |
| `netbpm.admin.redis.read` | Redis 作业队列诊断（连接状态 / 队列键条目数 / 服务端 INFO） |
| `netbpm.admin.audit.read` | 管理动作审计查询（与平台面 `workflow:admin:audit:read` **互认**：`/api/admin/audit` 与 `/admin/v1/audit` 同源） |
| `netbpm.admin.audit.purge` | 清理审计记录（可抹除历史痕迹，单独授权） |
| `netbpm.admin.history.archive` | 历史归档（执行与查询） |

- 判定口径：未启用凭据（开发）放行；API Key 代表可信服务放行；JWT 必须持有对应 scope 或 `netbpm.admin`。
- 两类动作的约束不同：
  - **纯管理动作**（`/api/admin/**`、删除实例、挂起 / 激活实例、跳转、迁移、设计器写操作）：**只有**管理 scope 能通过，
    普通同租户用户一律 **403**；
  - **业务动作 + 管理员越权**（转办、改任务、候选关系、变量直写）：办理人 / 候选人 / 发起人依业务规则本就能做，
    不需要管理 scope（否则会把正常办理挡在门外）；**越权**时才要求上表对应 scope。
- `GET /api/admin/info` 只含版本、方言、结构版本等运维信息，保持公开（与就绪探针同口径）。
- 设计器端点：
  - **读**（`/definitions`、`/definitions/{key}/versions`、`/definition/{id}/xml`、`/deployments`、
    `/deployments/{id}/resources`）需要 `netbpm.admin.model.read`；
  - **写**（`/deploy`、`/definitions/{id}/suspend|activate`、`DELETE /deployments/{id}`）需要
    `netbpm.admin.model.write`；**`?cascade=true` 的删部署额外要求 `netbpm.admin.instance.delete`**
    （它会连带删除运行中的实例）；
  - `POST /api/designer/validate` 只解析校验、不落库，保持开放。
- 设计器前端从 `localStorage['netbpm.admin.credential.v1']`（由管理控制台登录页写入，bearer / apiKey）
  自动附加凭据；403 时提示缺少的具体 scope。凭据无权限时**不再静默回退 localStorage 草稿**，
  避免误以为「数据丢了」。

**② 管理动作审计**

| 动作 | `action` | 触发点 |
|---|---|---|
| 转办 | `task.reassign` / `task.batch_reassign` | `POST /api/runtime/tasks/{id}/assignee`、批量转办 |
| 改任务 / 候选关系 | `task.update` / `task.candidate_add` / `task.candidate_remove` | `PATCH /tasks/{id}`、`/candidate-users`、`/candidate-groups` |
| 跳转 / 迁移 | `instance.jump` / `instance.migrate` | `.../jump`、`.../migrate`（预演不记） |
| 活动 modify | `instance.activity_cancel` / `instance.activity_start` | `.../activities/{activityId}/cancel|start-before`（成功与失败都记） |
| 模型 / 发布 | `model.create` / `model.delete` / `model.publish` / `model.history_prune` | `/api/admin/models/**` 的写操作 |
| 流程类型 | `process_type.create` / `update` / `delete` | `/api/admin/process-types/**` |
| 定时作业 | `job.reschedule` / `job.execute` / `job.retry` / `job.delete` | `/api/admin/jobs/{id}/due-date|execute|retry`、`DELETE /api/admin/jobs/{id}`（仅死信） |
| 异步作业 | 同上（`job.execute` / `job.retry` / `job.delete`） | `/api/admin/jobs/async/{id}/execute|retry`、`DELETE /api/admin/jobs/async/{id}` |
| outbox | `outbox.retry` / `outbox.delete` | `/api/admin/outbox/{id}/retry`、`DELETE /api/admin/outbox/{id}` |
| 删除实例 | `instance.delete` / `instance.batch_delete` | `DELETE /process-instances/{id}`、`.../batch/delete`（逐项记） |
| 挂起 / 激活 | `instance.suspend` / `instance.activate` | `.../suspend`、`.../activate` |
| 变量直写 | `variable.write` / `variable.delete` / `task_variable.*` | 实例变量与任务局部变量的 PUT / DELETE |
| 部署 | `deployment.create` | `POST /api/designer/deploy`（只记成功，避免编辑期失败刷表） |
| 删部署 | `deployment.delete` | `DELETE /api/designer/deployments/{id}`（明细含 `cascade`） |
| 定义挂起 / 激活 | `definition.suspend` / `definition.activate` | `/api/designer/definitions/{id}/suspend|activate` |
| 历史归档 | `history.archive` | `POST /api/admin/history/archive`（预演不记） |

- 记录字段：租户、操作人、鉴权方式、动作、目标、关联实例、明细（JSON）、成功/失败、错误、UTC 时间。
- **不记录变量值**（可能是敏感数据，也会让审计表膨胀），只记变量名与 `valueType`；任务描述同理，只记「是否改动」。
- 被 **403 拒绝**的越权请求不会写审计（未进入执行阶段）；审计只记录真正执行过的管理动作及其结果。
- 运行时端点与设计器端点共用同一套「执行 → 按结果留痕」封装（`AuditedAction`），不会出现两套口径。
- 审计写入失败**不影响业务**（只记 warning），但初始化脚本未执行（`admin_audit_log` 缺表）时审计会丢失。
- 查询：`GET /api/admin/audit?action=&actor=&processInstanceId=&success=&since=&until=&firstResult=&maxResults=`。

**保留策略**（审计只增不删会持续膨胀）：

- 手工清理：`DELETE /api/admin/audit?before=<UTC>&tenantId=` → `{ removed, before, tenantId }`；
  `before` 必填且不能晚于当前时间；需要 `netbpm.admin.audit.purge`。
- 清理动作本身写一条 `audit.purge`（晚于 `before`，不会被同一次清理删掉），「谁清了多少」始终可查。
- 自动清理：配置 `NetBpm:Admin:AuditRetentionDays`（默认 `0` = 关闭），开启后每 6 小时删除过期记录
  （跨租户统一口径；启动后延迟 5 分钟首轮执行，失败只告警）。
- 内存宿主同样支持清理（队列重建），且本身只保留最近 1 万条。
- 管理控制台提供「审计日志」页（`/audit`）：按操作人 / 动作 / 实例 / 结果 / 时间筛选，
  展开行看明细 JSON（自动格式化）；「清理历史」对应 `DELETE /api/admin/audit`，
  缺 `netbpm.admin.audit.purge` 时后端 403、前端提示所需 scope。
- 管理控制台提供「管理指标」页（`/metrics`）：运行中 / 待办 / 逾期 / 今日到期 / 历史 / 已结束 /
  已删除实例、流程定义、归档量九张统计卡，待办按流程 key 的占比分布，窗口内审计动作计数；
  「不可用」明确标注为宿主无法统计（内存宿主），与 0 区分。
- 管理控制台提供「Redis 诊断」页（`/redis`）：队列实现（`redis` / `memory`）、目标地址与逻辑库、
  PING 时延、三个队列键（`netbpm:job:delay` / `job:ready` / `job:inflight`）的条目数、服务端 INFO 摘要；
  连接不可用或配置没生效（还在走内存队列）都会直接给出结论 —— 「Redis 里看不到 netbpm 键」先看这里。
  只读、不消费；口令永不回传。页面每 10 秒自动刷新。
  「服务端信息」依赖 StackExchange.Redis 的 admin 模式（`NetBpm:Jobs:Queue:Redis:AllowAdmin`，默认 `true`）
  —— 它把 `INFO` 归为 admin 命令，关掉这个开关页面就只剩连接与键统计；连接串里显式写 `allowAdmin` 时以连接串为准。
  客户端只下发 `PING` / `LLEN` / `ZCARD` / `INFO` / Lua，从不执行 `FLUSH*` / `CONFIG` / `SHUTDOWN`，
  真正的权限边界仍在服务端（Redis 6+ 可用 ACL 单独收紧）。
  注意：队列只存「作业到期提示」，**三个键没有数据是正常状态**（作业执行完即删除，空键由 Redis 回收）。

**③ 指标与历史归档**

- `GET /api/admin/metrics`：运行中 / 历史 / 已结束 / 已删除实例数、流程定义数、待办总数 / 逾期 / 今日到期、
  按流程 key 的待办分布、窗口内管理动作计数、归档量。
  `pendingTasks` 系列在**内存宿主为 `null`**（没有 `ACT_RU_TASK` 可聚合）；`null` 表示「不可用」，不是 0。
- `POST /api/admin/history/archive`（`before` 必填，`dryRun` 预演）：把已结束实例的
  `ACT_HI_PROCINST / ACT_HI_ACTINST / ACT_HI_TASKINST / ACT_HI_VARINST / ACT_HI_IDENTITYLINK` 行
  序列化为 JSON 快照写入 `admin_history_archive`，并在**同一事务**内删除原行（含 longString 字节行）。
- 归档行对 `process_instance_id` 建唯一键：重复归档幂等跳过；`batchSize` 单次上限 1000（默认 200）。
- 查询：`GET /api/admin/history/archives`（列表，不含 payload）、`GET /api/admin/history/archives/{processInstanceId}`（含快照）。
- 内存宿主没有持久化历史：归档相关接口返回 **501**，指标里的归档量为 `null`。
- 已有库重复执行初始化脚本（`db/<dialect>/netbpm_init.sql`）会自动补上 `admin_audit_log` / `admin_history_archive`。

**④ 可观测性（OpenTelemetry 指标）**

管理治理三件套（RBAC / 审计 / 清理）各自埋了标准 OTel 计数器（Meter 名 `NetBpm.Admin`，
零 SDK 依赖的 `System.Diagnostics.Metrics`）。引擎侧另有独立 Meter `NetBpm.Engine`
（`netbpm.command.executed` / `netbpm.command.duration` / `netbpm.command.retry` /
`netbpm.timer_job.failure` / `netbpm.async_job.failure` / `netbpm.outbox.failure`）与 ActivitySource `NetBpm.Engine`（每个最外层命令一条
`netbpm.command <CommandName>` span，可与 HTTP span 串成完整链路）：

| 指标 | 标签 | 含义 |
|---|---|---|
| `netbpm.admin.authorization.denied` | `netbpm.admin.scope`、`netbpm.auth.mode` | RBAC 拒绝（越权尝试被 403）——「谁在撞门」 |
| `netbpm.admin.audit.recorded` | `netbpm.admin.action`、`netbpm.audit.success`、`netbpm.auth.mode` | 审计留痕条数（成功 / 失败都计） |
| `netbpm.admin.audit.purged` | — | 保留策略清理删除的条数 |

- 埋点位置：`AdminAuthorization.Ensure`（拒绝）、`AdminActionAuditor.RecordAsync`（审计；动作发生即计数，
  不依赖审计存储是否写成功）、`DELETE /api/admin/audit`（清理）。
- **默认关闭**：未接线时只是无人订阅的静默计数。启用（满足其一）：
  配置 `NetBpm:Otel:Enabled=true`，或设置 OTel 标准环境变量 `OTEL_EXPORTER_OTLP_ENDPOINT`
  （默认 `http://localhost:4317`，gRPC）。
- 启用后经 OTLP 导出（`OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.OpenTelemetryProtocol`），
  Resource 服务名 `NetBpm`；只订阅本引擎的 Meter，HTTP / 运行时指标由宿主自行决定。

### BPMN XML 整体要求

- 必须保留 **Activiti 扩展命名空间 `http://activiti.org/bpmn`**，assignee、candidateUsers、class 等扩展属性均按此命名空间读取。
- `targetNamespace` 可选，导出为空时自动回填默认命名空间。
- 子集外的 BPMN 元素（子流程、中间事件、泳道等）会**显式拒绝部署**（提示「不在本子集支持范围内」）；仅 `extensionElements`、`documentation`、`textAnnotation`、`association` 等被容忍并忽略。
- 图形信息（`bpmndi:BPMNDiagram`）**可选**，没有也能部署；XML 必须格式合法（禁止 DTD），否则解析失败。

---

## 目录结构

```
NetBpm/
├── src/                          # 引擎核心 + 两个宿主（11 个项目）
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
│   ├── mysql/netbpm_init.sql     # 初始化脚本（建库 + 建表 + 初值，单一数据源生成，请勿手改）
│   └── sqlserver/netbpm_init.sql # 同上（两方言结构一一对应，仅语法差异）
├── docs/
│   ├── process-design-guide.md   # 流程设计操作指引（以请假 / 订单流程为例）
│   └── integration-checklist.md  # 新项目引入注意事项：组合根 / 事务 / 委托 / 运行期 / 部署 / 自检清单
├── tools/
│   ├── NetBpm.DbInit/            # 数据库初始化器（生成脚本 → 初始化结构 + 数据 → 校验）
│   ├── NetBpm.DbMigrate/         # 数据库运维工具（migrate / check / probe / purge / apply）
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
# 推荐 dotnet test（标准方式）；dotnet run --project 是原生控制台入口，执行同一测试程序集。
# ⚠️ dotnet test 不要带 --nologo：那是 VSTest 时代的参数，MTP 会把它透传给测试宿主并以
#    「unknown option」拒绝，表现为「运行了零个测试」（exit code 5）—— 去掉即可正常。
# 单元测试含「全语料内存回归」（B1.3）：210 条对拍语料在纯内存宿主上跑通，约 0.5 秒，
# 日常改引擎先跑它；行级真值仍由差分对拍裁决
dotnet test tests/NetBpm.UnitTests -c Release
dotnet test tests/NetBpm.DifferentialTests -c Release

# 代码格式检查
dotnet format NetBpm.slnx --verify-no-changes --no-restore
```

### 初始化数据库

```bash
# 一步初始化：生成脚本 → 建结构 → 灌种子数据 → 校验 schema.version（幂等）
dotnet run --project tools/NetBpm.DbInit -- \
  --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;" \
  --create-database
```

脚本由单一数据源 `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` 生成（内容分层见 [`db/README.md`](db/README.md)）：
每方言一份 `db/<dialect>/netbpm_init.sql`（建库 + 建表 + 索引外键 + 初值数据，**全幂等可重复执行**），
引擎表为 13 张基础 `ACT_*` 表（外加 `ACT_RU_TIMER_JOB`、`ACT_HI_MIGRATION`、`ACT_RU_JOB` /
`ACT_EVT_OUTBOX` 引擎扩展表，以及 `bpmn_model_history` 模型版本表），与 Activiti 表结构对齐。
运维动作（探库 / 清库 / 校验 / 单脚本执行）用
`NetBpm.DbMigrate` 的 `probe` / `purge` / `check` / `apply` 子命令，`--provider` 可选 `mysql` 或 `sqlserver`。

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
    "MaxOperationsPerCommand": 10000,
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
- 账号密码（控制台登录）：`POST /api/auth/login` 用 `admin_user` 表里的账号换一个 JWT，
  见下面「控制台账号密码登录」。它是**新增的第三种凭据来源**，上面两种方式照旧可用。

**宿主安全基线（REST / 管理端一致）**：两个宿主共用同一套组合根
（`src/NetBpm.Rest/Hosting/NetBpmHost.cs`），因此下列保护一次配置、两处生效：

- **限流**（`NetBpm:RateLimit`，固定窗口，**按调用方分区**）：已注册客户端按 `clientId` 分区并用该客户端的
  `quota_per_minute`（未配置则 `ClientPerMinute`，默认 600）；JWT 用户按（租户, 操作人）分区（`ApiPerMinute`）；
  无身份时才按 IP 分档——登录 `LoginPerMinute`（10）/ 凭据探针 `ProbePerMinute`（30）/ 其余 `ApiPerMinute`（600）。
  响应带 `RateLimit-Limit` / `RateLimit-Remaining` / `RateLimit-Reset`，超限 **429** + `Retry-After` + RFC 9457 错误体。
  `/health` 与静态资源不限流。管理端旧配置节 `NetBpm:Admin:RateLimit:*` 仍然兼容（新节存在时以新节为准）。
  ⚠️ 计数器在进程内：多实例部署时总配额 = 实例数 × 配置值。
- **请求体上限**（`NetBpm:MaxRequestBodySizeBytes`，默认 10 MB）：BPMN XML 与变量字典都走请求体，
  没有上限等于允许单请求把进程内存打满。
- **异常映射**：唯一约束冲突 → **409**；其余数据库异常 → **503**（不带 SQL / 连接串，细节只进日志）。

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
「转办 / 修改任务字段 / 增删候选关系」时，必须是当前办理人、所有者或流程实例发起人
（或持有 `netbpm.admin.task.moderate` 的管理员）——这些动作会改变办理人 / 候选关系，
若不校验就能把别人待办改给自己后以办理人身份完成，从而绕过上面的边界。
API Key 模式代表可信服务，不做任务级校验。令牌支持 `groups` / `group`（空格、逗号或 JSON 数组）与
`scope` / `scp` 声明。

**变量读写授权**：JWT 用户读**流程实例变量**时，必须是发起人，或该实例运行中任务的办理人 / 所有者 /
候选人（含候选组）；读写**任务局部变量**时，必须是该任务的办理人 / 所有者 / 候选人，或流程实例发起人。
不满足一律 **403**。越权**写 / 删**需要 `netbpm.admin.variables.write`（或超级 `netbpm.admin`），
越权**读**需要 `netbpm.admin`；API Key 模式不做参与人校验。

两者都留空则保持开放（仅限开发环境，此时请求头声明的身份被直接信任）。

**控制台账号密码登录**（`/api/auth/**`，管理宿主与 REST 宿主都注册）：

| 端点 | 说明 |
|---|---|
| `POST /api/auth/login` | 请求 `{ username, password, tenantId? }` → `{ token, expiresAt, user, displayName, tenant, scopes }`；**匿名可达**（它本身就是换凭据的入口） |
| `GET /api/auth/me` | 回显当前凭据的身份： `{ user, tenant, authMode, scopes, isAdmin }`；**凭据探针** —— 启用鉴权后无凭据 / 无效凭据 401，未启用鉴权（开放模式）返回 `authMode="none"` |

- 账号存在 `admin_user` 表（初始化脚本已含，老库重跑一次幂等脚本即补齐）：
  只存 **PBKDF2-HMACSHA256** 散列（21 万次迭代，格式 `pbkdf2$<迭代>$<salt>$<hash>`），明文口令不落库、不进审计日志。
- 签发的令牌复用 `NetBpm:Security:Jwt:*` 同一套密钥，因此能直接被鉴权中间件接受：
  身份取 `sub`，范围取 `scope`，租户取 `tenant`。**未配置 `SigningKey` 时登录返回 503 `jwt_not_configured`** ——
  宁可明确失败，也不让登录「看起来成功」却拿到一个谁都不认的令牌。
- 失败语义（前端按 `code` 分支，文案统一为「登录名或口令错误」以免探测账号是否存在）：

| code | 状态 | 含义 |
|---|---|---|
| `invalid_credentials` | 401 | 登录名或口令错误（账号不存在同样返回它，并做一次同等强度的散列计算抹平耗时差） |
| `account_locked` | 429 | 连续失败达阈值被临时锁定（锁定先于口令校验：口令再对也不放行） |
| `account_disabled` | 403 | 账号已停用 |
| `tenant_mismatch` | 403 | 请求里的租户与账号绑定的租户不一致 |
| `jwt_not_configured` | 503 | 未配置签名密钥 |

- 可调参数（`NetBpm:Security:Login`）：`AccessTokenMinutes`（默认 120）、
  `MaxFailedAttempts`（默认 5，0 = 不锁定）、`LockoutMinutes`（默认 10）。
  管理宿主另对 `/api/auth/login` 单独限流：`NetBpm:Admin:RateLimit:LoginPerMinute`（默认 10，防口令爆破）。
- **建账号**用运维工具（口令至少 8 位；不传 `--password` 会交互式读取且不回显）：

```bash
dotnet run --project tools/NetBpm.DbMigrate -- users add \
  --username admin --display-name 管理员 --scopes netbpm.admin \
  --provider sqlserver --connection "Server=localhost;Database=netbpm;User ID=sa;Password=***;TrustServerCertificate=True"

dotnet run --project tools/NetBpm.DbMigrate -- users list        --provider ... --connection "..."
dotnet run --project tools/NetBpm.DbMigrate -- users reset-password --username admin --provider ... --connection "..."
dotnet run --project tools/NetBpm.DbMigrate -- users disable     --username admin --provider ... --connection "..."
```

- **无库（内存）模式**没有 `admin_user` 表，可用 `NetBpm:Security:DevUsers` 配置开发账号
  （口令写在配置里，仅本地开发；生产请用 `admin_user`）：

```json
"NetBpm": { "Security": { "DevUsers": [ { "Username": "admin", "Password": "NetBpm@2026", "Scopes": "netbpm.admin" } ] } }
```

- 控制台前端：登录页默认把令牌写到 `localStorage['netbpm.admin.credential.v1']`
  （**跨标签页共享**，设计器 `web/designer` 读同一个键，因此新开标签页免登录）；
  取消「保持登录」只写 `sessionStorage`（仅本标签页，关闭即失效，顶栏会标「仅本标签页」）。
  令牌过期时本地先判一次并回登录页，服务端 401 同样会跳登录页。

**错误码约定**：`400` 参数 / 业务规则拒绝、`403` 身份或租户与令牌不一致、`404` 对象不存在（含跨租户）、
`409` 状态冲突（已认领 / 仍有运行实例 / 乐观锁失败）、`501` 明确未实现、`500` 只返回 traceId，
不回传异常原文。错误体带机器可读 `code`（`validation_error` / `forbidden` / `not_found` /
`optimistic_lock_conflict` / `conflict` / `engine_rule_violation` / `internal_error` …），
调用方应按 `code` 程序化分支，而不是匹配中文文案。

**幂等**：改变状态的请求可带 `Idempotency-Key`。同键同请求回放首次响应（`Idempotency-Replayed: true`），
同键不同请求返回 409。数据库宿主使用 `ACT_IDEMPOTENCY` 表跨实例去重；过期记录由宿主每小时清理一次
（`IdempotencyCleanupService`，启动后延迟 1 分钟首轮执行）。`ACT_IDEMPOTENCY` 表由初始化脚本建，
已有库重复执行脚本会自动补上（脚本幂等）。

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

### 管理控制台前端（web/admin）

```bash
cd web/admin
npm install
npm run dev       # 开发（vite 代理 /api 到 localhost:5081，可用 NETBPM_API_TARGET 覆盖）
npm run build     # 类型检查 + 构建，产物输出到 src/NetBpm.Admin/wwwroot/
npm run lint      # ESLint
npm test          # vitest 单元测试
```

控制台页面与主要能力：

| 页面 | 能力 |
|---|---|
| 流程模型 | 新建 / 导入 / 复制 / 反建、关键字搜索、设计器跳转、发布前预检、发布（内容未变自动跳过）、发布历史、版本历史与回滚、If-Match 并发冲突提示 |
| 部署与定义 | 部署列表（定义数 / 最新版本 / 挂起数）与关键字过滤、定义版本、挂起 / 激活、删除部署、下载 XML |
| 接入管理 | 租户登记 / 启用停用（整租户止血）、接入系统注册、scope 与流程白名单、配额、密钥轮换（明文只显示一次）与注销 |
| 审批字典 | 按流程列出「定义里声明的选项」，逐项补语义（同意 / 拒绝 / 退回 / 转办 / 自定义）、必填意见、目标环节限制；未配置项如实显示为「流程自定义」 |
| 事件订阅 | 订阅增删改 / 启停 / 密钥轮换、回调地址与事件类型白名单、限速与重试次数、投递记录、死信列表与重投 |
| 配额总览 | 租户默认配额 + 接入系统实际配额（含是否继承）、限流开关状态；两个表都支持「加载更多」（游标分页） |
| 监控 / 实例 | 多条件筛选、挂起 / 激活 / 删除、跳转节点、**取消活动 / 活动前启动**、变量直写（JSON 合并写 + 审计提示）、流程图与历史 |
| 监控 / 任务 | 改到期时间 / 优先级、转办与取消认领、候选人 / 候选组增删、委派与解决、批量转办、**批量改到期时间**、表单元数据（formKey + formProperty） |
| 监控 / 作业 | 边界定时作业查询与统计、改期、**立即触发** |
| 审计 / 指标 | 分页查询（动作 / 操作人 / 实例 / 成功失败）、筛选项、CSV 导出、保留策略清理、总量与待办分布 |

控制台所有变更类操作都会复用后端审计（`/api/admin/audit` 可回放；`/admin/v1/*` 的写操作落同一张审计表）；
发布 / 发起 / 完成带 `Idempotency-Key`，重复点击或代理重试不会产生重复版本 / 重复实例。
「接入管理」页走平台面 `/admin/v1`（租户 / 客户端），密钥签发**不做幂等**，页面以「提交中禁用 + 明文只显示一次」保证安全。
管理台的写操作全部走 `/admin/v1/*`（`/api/admin/*` 的同类路径仅保留兼容）；
`/admin/v1` 的错误体是 RFC 9457，控制台会把 `detail` 直接展示成可读文案，而不是「Forbidden」。

⚠️ `src/NetBpm.Admin/wwwroot/` 是**构建产物，不入库**（设计器与管理台共用该目录，
分别由 `web/designer` 与 `web/admin` 的 `npm run build` 产出）。首次拉取代码或清理后，
需先在两个前端目录各执行一次 `npm run build` 再启动宿主，否则页面 404。
CI（`.github/workflows/ci.yml`）会在 Build 前完成两段前端构建。

### 被 OPMS 反向代理到子路径（可选）

两个前端都支持「整站被 OPMS 挂到 `/netbpm/` 下」而非独立域名的部署形态：

```bash
cd web/admin && npm run build:opms        # base = /netbpm/
cd web/designer && npm run build:opms     # base = /netbpm/designer/
```

资源路径、路由 basename、接口前缀都从 Vite `base` 派生（`web/*/src/base.ts`、`src/api/base.ts`），
一次构建即三者一致：管理端资源 `/netbpm/assets/**`、设计器 `/netbpm/designer/**`、
接口 `/netbpm/api/**`。特殊环境可用 `NETBPM_WEB_BASE` / `NETBPM_DESIGNER_BASE` 覆盖。

⚠️ 反代需**剥离** `/netbpm` 前缀后转发到 `NetBpm.Admin`（`/netbpm/api/x` → `/api/x`、`/netbpm/assets/x` → `/assets/x`），
后端不需要改配置；若代理必须保留前缀，则需在宿主加 `UsePathBase("/netbpm")`（当前未内置）。

### 引擎启动步骤（其它 .NET 项目接入）

1. **建库**：执行 `db/mysql/netbpm_init.sql` 或 `db/sqlserver/netbpm_init.sql`
   （一份脚本搞定建库 + 建表 + 初值，幂等可重复执行；内容分层见 [`db/README.md`](db/README.md)。
   只接引擎、不要业务表的项目忽略 ② ③ 段建出来的表即可；
   库名默认 `netbpm`，改脚本顶部「⓪ 建库 / 选库」两行，或用 `NetBpm.DbInit --create-database` 按连接串建库）；
2. **引用引擎包**：`dotnet pack` 产出见下节「打包分发」，或直接引用 `src/` 各项目；
3. **组合根注册**（详见 [`samples/NetBpm.Demo/README.md`](samples/NetBpm.Demo/README.md)）：

```csharp
builder.Services.AddNetBpm(builder.Configuration);          // 引擎（连接串 / 执行树模式 / schema 校验）
builder.Services.AddNetBpmWorkflow<MySqlWorkflowDataStore>(); // 业务数据端口（五块数据落库）
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly); // 收口委托扫描
```

4. **启动即部署**：`WorkflowProcessBootstrapper.DeployAsync(...)` 把 BPMN 定义灌进引擎
   （`NetBpm.Rest` 宿主亦可 `AutoMigrate=true` 在启动时执行同一份初始化脚本）；
5. **验证**：`/openapi/v1.json` 查看端点；`ACT_*` 表与业务表开始产生数据。

### 打包分发

```bash
powershell -File tools/pack.ps1 -Configuration Release
```

产物（`artifacts/`，已 gitignore）：

- `nuget/*.nupkg` —— 9 个引擎包（接入入口 `NetBpm.Workflow`，依赖链自动带上；
  `NetBpm.Persistence` 包内含两方言初始化脚本）；
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

### 在自己的宿主接入审批流（公共类 · ⚠️ 仅维护既有宿主）

> ⚠️ **新接入不要从这里开始**：`MapWorkflowEndpoints` / `AddNetBpmWorkflow` 这套应用层已标记
> `[Obsolete]`（替代路径见 `src/NetBpm.Workflow/Hosting/Deprecation.cs`）。新宿主按下面两种形态之一接入：
> 1. **平台面（推荐）**：`NetBpmHost.MapNetBpmHostEndpoints()` 直接暴露 `/workflow/v1/**` + `/admin/v1/**`，
>    调用方用 `sdk/`（.NET / TypeScript / Java）或任何 HTTP 客户端；
> 2. **进程内**：需要复用引擎时用 `NetBpm.Persistence` + 引擎门面，业务侧仍通过平台面的 DTO 语义对接。
>
> 下面这套「公共类」保留给**已经在用**的宿主升级到平台面的过渡期。

流程相关功能已封装为 `NetBpm.Workflow` 的公共类，宿主六步接入：

```csharp
builder.Services.AddNetBpm(builder.Configuration);                  // ① 引擎
builder.Services.AddNetBpmUserGroupManager<MyUserGroupManager>();   // ② 身份适配器（候选组 → 人）
builder.Services.AddNetBpmWorkflow<MyWorkflowDataStore>();          // ③ 应用层 + 业务数据端口（IWorkflowDataStore）
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly); // ④ 服务任务委托（类上标 [ServiceTaskName]）

app.MapWorkflowEndpoints("/api/workflow", new WorkflowEndpointOptions // ⑤ 通用流程 HTTP 端点（⚠️ 已废弃，新宿主见上）
{
    ProcessKey = _ => "my_process",
    // TransformStartRequest / DecorateInstanceView / DecorateInstanceSummary / ErrorResponseFactory
    // 是业务定制点：单号生成、DTO 摊平、错误响应形状

    // ⚠️ 必配：转办 / 跳转属于管理动作，未配置时一律 403（fail-closed）。
    // 生产宿主请在这里校验管理 scope，无鉴权的演示宿主可显式放开。
    AuthorizeAdminAction = (http, action, ct) =>
        ValueTask.FromResult(http.User.HasClaim("scope", "netbpm.admin")),
});

await WorkflowProcessBootstrapper.DeployAsync(app.Services, new WorkflowDeployOptions // ⑥ 启动即部署
{
    ResourcePath = "processes/my-process.bpmn20.xml",
    DeploymentName = "我的流程",
    // 委托预解析（ServiceTaskDelegateGuard）与候选组空实现告警已默认执行，不用再挂
    VerifyAsync = async (_, engine, definitionId, logger, ct) =>
        await ApprovalFormGuard.VerifyAsync(engine, definitionId, logger, ct),   // 业务自检钩子（可选）
}, app.Logger);
```

⚠️ ② 不能省：`AddNetBpm` 默认注册的是空实现（任何组都没有成员），忘了替换时候选组会展开成
0 个人，**待办静默消失且不报错**。接入时逐条对照 [`docs/integration-checklist.md`](docs/integration-checklist.md)。

核心类型一览：

| 公共类 | 职责 |
|---|---|
| `IWorkflowService` / `WorkflowService` | 发起申请 / 获取环节信息 / 提交审批 / 审批历史四件事的应用层门面 |
| `WorkflowEndpoints` + `WorkflowEndpointOptions` | 通用流程 HTTP 端点（路由约定统一，业务语义经选项注入） |
| `WorkflowProcessBootstrapper` + `WorkflowDeployOptions` | 启动即部署 BPMN 资源 + 默认自检（委托预解析、候选组空实现告警）+ 业务自检钩子 |
| `AddNetBpmUserGroupManager<T>()` | 用业务身份适配器**替换**默认空实现（`Replace` 语义，注册顺序无关） |
| `WorkflowRuntimeContext` / `WorkflowNodeRegistry` | 部署后运行期上下文：流程定义 id + 环节名 ↔ 环节 id 对照表（随 ② 自动注册） |
| `IWorkflowDataStore` | 应用层与宿主之间唯一的缝：实例登记 / 审批结论 / 流程参数 / 审批记录 / 当前待办 |
| `JsonWorkflowDataStore` | 端口的**参考实现**：五个区块直接落一个 JSON 文件（零落库跑通全链路，换真库时数据形状不变） |
| `WorkflowCompletionDelegate` | 「结束收口」委托基类：派生一个类只需声明 `[ServiceTaskName]` 与结论名 |
| `IOrgDirectory` / `InMemoryOrgDirectory` | 组织架构查询（部门 / 员工 / 按候选组找审批人）：内置演示实现（SqlServer / JSON 模式）+ MySQL 真表实现 `MySqlOrgDirectory`（Demo 存储层，读 `sys_dept` / `sys_emp` 等四张 `sys_*` 表，随业务存储按方言切换） |
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

> **生命周期**：委托可以注册为 `Scoped`（依赖同样 Scoped 的服务，例如仓储 / `DbContext`）。
> 引擎在**每条命令**自己的 DI 作用域内解析它，命令结束（含失败、含乐观锁重试换事务）即释放 —— 同一命令内的多次解析共用一份，
> 跨命令不共享。没注册到 DI 的 `activiti:class` 仍按 Activiti 语义反射创建。

---

## REST API

对外有两组面，**变更纪律不同**：`/workflow/v1/**` 是平台业务面（对外契约，路径与错误体长期稳定，
错误体是 RFC 9457），`/api/**` 是引擎原语面（随引擎演进，不承诺向后兼容）。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/workflow/v1/processes` | 流程目录（`keyword` / `category` / `status` / `cursor` / `limit`；已按客户端白名单过滤） |
| GET | `/workflow/v1/processes/{key}` | 流程名片（名称 / 分类 / 入口 / 能力声明 / 定义校验和） |
| GET | `/workflow/v1/processes/{key}/definition` | 当前定义（`?includeXml=true` 才返回 BPMN 原文） |
| GET | `/workflow/v1/processes/{key}/versions` | 版本列表（标记 `isCurrent`） |
| GET | `/workflow/v1/processes/{key}/diagram.svg` | 静态流程图（`?version=` 取历史版本） |

> 目录读端点需要 `workflow:process:read`；未授权的流程不出现，按 key 访问返回 **404**（不是 403）。
> 名片在「什么都没配置」时也能用：名称与入口从部署的 BPMN 派生，`nb_process_catalog` 只做精修覆盖。

**业务面其余端点**（都在 `/workflow/v1`，错误体同为一律 RFC 9457）：

| 方法 | 路径 | 所需 scope |
|---|---|---|
| GET | `/workflow/v1/processes/{key}/parameters/schema`、`/processes/{key}/form/schema` | `workflow:process:read` |
| GET | `/workflow/v1/instances`、`/instances/{id}`、`/instances/{id}/history`、`/instances/{id}/parameters`、`/instances/{id}/parameters/history`、`/instances/{id}/diagram.svg` | `workflow:instance:read` |
| POST | `/workflow/v1/instances` | `workflow:instance:start` |
| POST | `/workflow/v1/instances/{id}/terminate` | `workflow:instance:terminate`（高危，单独授予） |
| GET | `/workflow/v1/tasks`、`/tasks/{id}`、`/tasks/{id}/form`、`/tasks/{id}/attachments` | `workflow:task:read`（`scope=DONE` 另需 `workflow:history:read`；`scope=ALL` 另需 `workflow:history:read:all`，缺权限 **403**，不降级） |
| POST | `/workflow/v1/tasks/{id}/submit` | `workflow:task:submit`（代他人提交另需 `workflow:task:submit:onbehalf`） |
| POST | `/workflow/v1/tasks/{id}/claim`、`/unclaim`、`/transfer` | `workflow:task:assign` |

**管理面 `/admin/v1`**（管理能力，非对外契约；错误体同为 RFC 9457）：

| 方法 | 路径 | 所需 scope |
|---|---|---|
| GET / PUT | `/admin/v1/tenants`、`/tenants/{tenantId}`、`/tenants/{tenantId}/status` | `workflow:admin:client` |
| GET / POST / PATCH / DELETE | `/admin/v1/clients[/{clientId}]`、`POST /clients/{clientId}/rotate`（创建与轮换**返回一次性明文密钥**） | `workflow:admin:client` |
| GET / PUT / DELETE | `/admin/v1/processes[/{processKey}]`（流程名片精修覆盖） | `workflow:admin:process` |
| GET | `/admin/v1/dictionaries`、`/dictionaries/{processKey}/declared` | `workflow:admin:process` |
| PUT / DELETE | `/admin/v1/dictionaries/{processKey}/options/{optionId}` | `workflow:admin:process` |
| GET | `/admin/v1/audit`、`/audit/facets` | `workflow:admin:audit:read`（**不再**借用 `admin:client`） |
| GET | `/admin/v1/subscriptions`、`/events/deadletters`、`/events/stats` | `workflow:event:subscribe` |
| GET / POST / PATCH / DELETE | `/admin/v1/subscriptions[/{id}]`、`/subscriptions/{id}/status`、`/subscriptions/{id}/rotate`、`/subscriptions/{id}/deliveries`、`/events/{eventId}/deliveries/{subscriptionId}/retry` | `workflow:event:subscribe` |
| GET | `/admin/v1/quotas`（`?tenantCursor=&clientCursor=&max=`；两个列表各自游标分页，默认 50 / 上限 500，响应带 `tenantsNextCursor` / `clientsNextCursor`） | `workflow:admin:client` |

租户边界（**新面一律按此口径，旧面行为一致**）：

- 业务面：凭据绑定租户后只能看到本租户；未授权流程按 key 访问、跨租户实例按 id 访问都是 **404**（不给枚举空间），跨租户写 **403**。
- 管理面：**未绑定租户**的平台凭据才拥有跨租户运营视角；**绑定了租户**的凭据即使传 `?tenantId=` 也只能看到自己（403 `tenant.forbidden`）。
- `/api/admin/*` 的**客户端注册 / 租户 / 事件订阅写路径已废弃**，仅保留给既有运维脚本（错误体、租户口径、审计与新面一致）。新接入一律走 `/admin/v1/*`。

实例列表（`GET /workflow/v1/instances`）的过滤与分页都在库里完成，不是「拉回来再筛」：

- 租户边界（`TENANT_ID_ in (本租户, '')`）、流程白名单（key 展开成定义 id 白名单）、发起人、业务主键、时间窗、
  `status` 的「运行 / 结束」粗筛与 `limit` **全部下推到 SQL**；两个数据源（运行态、历史）各取 `limit+1` 条再合并，
  排序与游标共用同一个全序（`START_TIME_` + `ID_` 升序 tie-break），所以翻页不重不漏。
- 只有两种查询会在平台侧做二次过滤：`involvedUser`（「我参与过」需要按人展开实例集合）与 `status=失败`（引擎不产出，查一次确认为空）。
- 单页上限 100；每行还要补「当前办理人 / 结论」，属于有界 N+1（并发 4），翻页不是导数据通道。

**平台 scope 一览**（`workflow:*` 通配只覆盖**非** `:admin`、**非** `:onbehalf` 的项；`workflow:task:*` 这类前缀通配不生效）：

| scope | 覆盖 |
|---|---|
| `workflow:process:read` | 流程目录 / 定义原文 / 版本 / 参数与表单 schema / 流程图 |
| `workflow:instance:read` | 实例视图 / 参数快照 / 实例流程图 / 实例时间线 |
| `workflow:instance:start` | 发起流程（配合 `Idempotency-Key` 防重复开单） |
| `workflow:instance:terminate` | 终止实例（高危） |
| `workflow:task:read` | 待办列表与详情 / 表单 / 附件 |
| `workflow:task:submit` | 提交审批（结论必须落到真人主体） |
| `workflow:task:submit:onbehalf` | 代他人提交（需 token exchange 的 `act.sub`） |
| `workflow:task:assign` | 认领 / 取消认领 / 转办 |
| `workflow:history:read` | 审批记录 / 已办 |
| `workflow:history:read:all` | 跨人查看全部审批历史（审计角色） |
| `workflow:admin:process` | 部署 / 挂起 / 激活定义，流程名片与审批语义字典维护 |
| `workflow:admin:instance` | 跳转节点 / 实例迁移 / 批量干预 |
| `workflow:admin:variables` | 越过参数 schema 直写变量（默认不授予任何客户端） |
| `workflow:admin:client` | 租户 / 客户端注册 / 流程授权 / 配额 |
| `workflow:admin:audit:read` | 管理动作审计与筛选项 |
| `workflow:event:subscribe` | 事件订阅读写、投递记录、死信重投 |
| `workflow:external:task` | 外部任务领取与回执（`activiti:topic`） |

> 幂等：变更型端点支持 `Idempotency-Key`（同键回放、同键异体 409）。**密钥签发类端点（客户端创建 / 轮换、订阅创建 / 轮换）明确不做幂等**——
> 把响应体缓存下来等于把明文密钥写进 `ACT_IDEMPOTENCY`；丢了响应请调 `/rotate`，旧密钥在宽限期内仍有效。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| GET | `/health/live` | 存活探针（只判断进程） |
| GET | `/health/ready` | 就绪探针（数据库不可用返回 503） |
| GET | `/api/admin/info` | 运维信息（版本 / 方言 / 历史开关 / 结构版本 / 运行时长） |
| GET | `/api/admin/audit` | 管理动作审计（`action`/`actor`/`processInstanceId`/`success`/时间 + 分页） |
| DELETE | `/api/admin/audit` | 审计保留策略清理（`before` 必填，需要 `netbpm.admin.audit.purge`） |
| GET | `/api/admin/metrics` | 管理指标（实例 / 定义 / 待办 / 审计动作 / 归档量） |
| GET | `/api/admin/redis` | Redis 作业队列诊断（实现 / 目标地址 / PING / 队列键条目数 / 服务端 INFO，需要 `netbpm.admin.redis.read`） |
| POST | `/api/admin/history/archive` | 历史归档（`before` 必填，`dryRun` 预演） |
| GET | `/api/admin/history/archives` | 归档记录列表（不含 payload） |
| GET | `/api/admin/history/archives/{processInstanceId}` | 归档详情（含历史行 JSON 快照） |
| GET | `/openapi/v1.json` | OpenAPI 文档 |
| GET | `/api/admin/models` | 模型列表（`keyword` + 分页 + `envelope`；含发布状态与「有未发布修改」） |
| POST | `/api/admin/models` | 新建模型（服务端生成 id；`process_key` 重名 409） |
| GET | `/api/admin/models/{id}` | 模型详情（XML / SVG / `revision` / 发布状态） |
| PUT / PATCH | `/api/admin/models/{id}` | 保存草稿（PUT 全量 / PATCH 局部）；支持 `If-Match` 乐观并发（409） |
| DELETE | `/api/admin/models/{id}` | 删除模型（连同版本历史，不影响已部署定义） |
| POST | `/api/admin/models/{id}/validate` | 发布前预检（errors / warnings，不落库） |
| POST | `/api/admin/models/{id}/publish` | 发布：部署期校验强制拦截；`skipIfUnchanged` 内容未变时跳过 |
| GET | `/api/admin/models/{id}/publications` | 发布历史（revision / 定义 id / 版本 / 部署 id / 时间 / 操作人） |
| GET | `/api/admin/models/{id}/history` | 版本历史（分页；不含 XML/SVG 正文） |
| GET | `/api/admin/models/{id}/history/{revisionId}` | 版本详情（含 XML/SVG） |
| POST | `/api/admin/models/{id}/rollback/{revisionId}` | 回滚为**新版本**（历史只增不删） |
| DELETE | `/api/admin/models/{id}/history` | 裁剪历史（按保留版本数） |
| POST | `/api/admin/models/{id}/duplicate` | 复制模型（自动换 key 并改写 XML process id） |
| POST | `/api/admin/models/import` | 导入 BPMN XML 为模型（`text/xml`，process id 作为 key） |
| POST | `/api/admin/models/from-definition/{processDefinitionId}` | 从已部署定义反建模型 |
| GET / POST | `/api/admin/process-types` | 流程类型列表 / 新建 |
| GET / PUT / DELETE | `/api/admin/process-types/{id}` | 流程类型详情 / 修改 / 删除 |
| GET | `/api/admin/jobs` | 定时作业列表（实例 / 定义 / 到期前 + 分页 + `envelope`） |
| GET | `/api/admin/jobs/stats` | 作业统计（总数 / 已到期 / 一小时内到期 / 最近到期） |
| POST | `/api/admin/jobs/{jobId}/due-date` | 改期（只改到期时间，触发仍由宿主轮询负责） |
| POST | `/api/admin/jobs/{jobId}/execute` | 立即触发（与轮询同一抢占语义；未推进 → 409） |
| GET | `/api/admin/jobs/async` | 异步作业列表（实例 / 死信 + 分页 + `envelope`） |
| POST | `/api/admin/jobs/async/{jobId}/execute` | 立即执行异步作业 |
| POST | `/api/admin/jobs/async/{jobId}/retry` | 重试异步死信作业 |
| DELETE | `/api/admin/jobs/async/{jobId}` | 删除异步死信作业 |
| GET | `/api/admin/outbox` | outbox 事件列表（实例 / 已发布 / 死信 + 分页） |
| POST | `/api/admin/outbox/{eventId}/retry` | 重试 outbox 死信事件 |
| DELETE | `/api/admin/outbox/{eventId}` | 删除 outbox 事件 |
| GET | `/api/admin/audit/options` | 审计筛选项（distinct action / targetType / actor） |
| GET | `/api/admin/audit/export` | 审计导出（CSV，与查询同过滤条件） |
| POST | `/api/designer/deploy` | 部署 BPMN XML 定义（`?resourceName=` / `?deploymentName=` / `?tenantId=`） |
| POST | `/api/designer/validate` | 试校验 BPMN XML（不落库，返回 errors / warnings） |
| GET | `/api/designer/definitions` | 定义列表（`tenantId`/`key` 过滤 + 分页） |
| GET | `/api/designer/definitions/{key}/versions` | 按 key 查全部版本（版本倒序） |
| GET | `/api/designer/deployments` | 部署列表（按部署聚合定义，`keyword` / `tenantId` + 分页；含 `definitionCount` / `latestVersion` / `suspendedCount`） |
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
| POST | `/api/runtime/process-instances/{id}/activities/{activityId}/cancel` | 取消该活动上的活动实例（任务 / 定时作业 / 历史一并收口；无活动执行 → 409；取消最后一个分支后实例结束） |
| POST | `/api/runtime/process-instances/{id}/activities/{activityId}/start-before` | 在活动之前新开一条令牌（目标活动真正开始执行；现有多分支不动） |
| POST | `/api/runtime/process-instances/{id}/migrate` | 迁移到其它定义版本（`validateOnly` 预演；活动映射 + 待办重建） |
| GET | `/api/runtime/process-instances/{id}/migrations` | 实例迁移记录 |
| GET | `/api/runtime/tasks` | 查询任务列表（`assignee`/`candidateUser`/`candidateGroup`/实例 + `taskDefinitionKey`/`suspended`/`dueBefore` + 分页 + `sortBy`/`sortOrder`） |
| GET | `/api/runtime/tasks/{taskId}` | 任务详情（含描述 / 分类 / 业务主键 / 委派状态 / 身份关系） |
| GET | `/api/runtime/tasks/{taskId}/identity-links` | 任务身份关系（候选人 / 候选组） |
| GET | `/api/runtime/process-instances/{id}/identity-links` | 流程实例身份关系（参与者 / 发起人 / 候选组） |
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
| POST | `/api/runtime/tasks/batch/due-date` | 批量改到期时间 / 清空到期（逐项结果，非原子） |
| POST | `/api/runtime/tasks/batch/complete` | 批量完成（逐项授权与结果，非原子） |
| POST | `/api/runtime/process-instances/batch/delete` | 批量删除实例（逐项结果，非原子） |
| POST | `/api/runtime/process-instances/batch/suspend` `/activate` | 批量挂起 / 激活实例（逐项结果） |
| POST | `/api/runtime/process-instances/batch/migrate` | 批量迁移到同一目标定义版本（逐项结果） |
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
- 新项目引入注意事项（组合根顺序 / 同库事务 / 委托与候选组 / 定时器单位 / 子路径部署 / 发布前自检清单）：`docs/integration-checklist.md`
- 开发计划：`doc/NetBpm-子集流程引擎-开发计划.md`（同目录另有 HTML 版）

---

## 说明

产品命名 `NetBpm` 用于避开 "Activiti" 商标。本项目为独立实现，与 Alfresco / Activiti 官方无隶属关系。
