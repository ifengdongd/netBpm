# NetBpm —— 参照 Activiti 定义的 .NET 轻量子集流程引擎 · 开发计划

> 工作代号 **NetBpm**（产品正式命名建议避开 "Activiti" 商标，见 §13）
> 目标平台 **.NET 10 LTS / C# 14** ｜ 数据库 **SQL Server + MySQL**
> 依据源码：`E:\DataCode\Activiti-9.0.0`（Activiti 9.0.0，3,060 个 Java 文件 / 344,267 行）
> 编制日期：2026-10-12

---

## 0. 一句话方案

**用 Activiti 的"定义格式 + 解析行为 + 运行时语义"三件套，只实现其中"顺序 / 分支合并 / 并行同步"这一子集，配一个基于 bpmn-js 的浏览器设计器，做成一个 .NET 原生、体积小、可维护的流程引擎。**

它不是"移植"，也不是"从零发明语义"，而是**以 Activiti 为行为字典的受控子集实现** —— 架构与命名向 Activiti 对齐（便于比对、便于招人、便于查资料），代码从零写（干净、异步原生、无 Java 包袱）。

---

## 1. 范围边界（这一节比后面所有内容都重要）

### 1.1 三个层次的"照搬"

| 层次 | 照搬内容 | 具体含义 |
|---|---|---|
| **L1 定义格式** | BPMN 2.0 XML + Activiti 扩展命名空间 | 同命名空间 URI、同元素名、同属性名、同 `bpmndi` 图形信息结构。→ 同一份 `.bpmn20.xml` 两端都能跑 |
| **L2 解析行为** | 转换器分层 + 校验规则 | 同"每个元素一个 Converter / 一个 ParseHandler"的分层，同校验红线（无出流节点、排他网关无默认流等） |
| **L3 运行时语义** | ActivityBehavior + Agenda 调度 | 同行为类划分、同执行树模型、**尤其同两条 Activiti 特有语义**（见 §5.4） |

### 1.2 支持的 BPMN 元素（子集定义）

| 元素 | XML 标签 | 关键属性 / 子元素 | Activiti 行为类（照搬依据） | 本方案行为类 |
|---|---|---|---|---|
| 开始事件（空） | `startEvent` | `id` `name` | `NoneStartEventActivityBehavior` | `NoneStartEventActivityBehavior` |
| 结束事件（空） | `endEvent` | `id` `name` | `NoneEndEventActivityBehavior` | `NoneEndEventActivityBehavior` |
| 顺序流 | `sequenceFlow` | `sourceRef` `targetRef`；子元素 `conditionExpression` | `TakeOutgoingSequenceFlowsOperation` | `TakeOutgoingSequenceFlowsOperation` |
| **排他网关** | `exclusiveGateway` | `default` → 分支 / 合并 | `ExclusiveGatewayActivityBehavior` | `ExclusiveGatewayActivityBehavior` |
| **并行网关** | `parallelGateway` | → 分叉(Fork) / 同步合并(Join) | `ParallelGatewayActivityBehavior` | `ParallelGatewayActivityBehavior` |
| 用户任务 | `userTask` | `activiti:assignee` `activiti:candidateUsers` `activiti:candidateGroups` `activiti:formKey` `activiti:dueDate` `activiti:priority` | `UserTaskActivityBehavior` | `UserTaskActivityBehavior` |
| 服务任务 | `serviceTask` | `activiti:class` / `activiti:delegateExpression` / `activiti:expression` | `ServiceTask*ActivityBehavior`（3 个） | `ServiceTaskDelegateBehavior`（1 个，三种写法统一路由） |
| 手工任务 | `manualTask` | `id` | `ManualTaskActivityBehavior` | 同 |
| 脚本任务（可选） | `scriptTask` | `scriptFormat` `activiti:autoStoreVariables` | `ScriptTaskActivityBehavior` | Roslyn 实现，**建议二期** |
| 流程 | `process` | `id` `name` `isExecutable` | `ProcessParser` | `ProcessParser` |
| 图形信息 | `bpmndi:BPMNDiagram` 等 | `BPMNShape` / `BPMNEdge` / `dc:Bounds` / `di:waypoint` | `BpmnShapeParser` / `BpmnEdgeParser` | 同（设计器必需） |
| 执行监听器（可选） | `activiti:executionListener` | `event="start|end"` | `ExecutionListener` 链 | 同 |
| 任务监听器（可选） | `activiti:taskListener` | `event="create|assignment|complete"` | `TaskListener` 链 | 同 |

**行为类规模：Activiti 58 个 → 本方案 8 个 + 3 个基类。**

### 1.3 明确不做（Out of Scope）

> 这 12 项占 Activiti 全部复杂度的 **70% 以上**，全部砍掉是本方案成立的前提。

| # | 不做 | Activiti 对应 | 备注 |
|---|---|---|---|
| 1 | 子流程 / 调用活动 | `subProcess` `callActivity` | 语料中 221 / 97 个文件命中，但语义长尾极重 |
| 2 | 边界事件 | `boundaryEvent` | 语料 245 个文件命中 —— 最高频的排除项 |
| 3 | 定时器 / 消息 / 信号 / 错误 / 补偿 / 终止事件 | `*EventDefinition` | 需要作业调度器（job executor），二期再说 |
| 4 | 事件子流程 / 事件网关 | `eventSubProcess` `eventBasedGateway` | |
| 5 | 多实例（会签 / 串签） | `multiInstanceLoopCharacteristics` | **若业务需要"会签"，必须提前说明 —— 这是最可能被追加的一项** |
| 6 | 包容网关 / 复杂网关 | `inclusiveGateway` `complexGateway` | |
| 7 | 异步作业 / 作业重试 / 死信 | `asyncBefore` `ACT_RU_JOB` 族 | → 直接省掉 4 张表 |
| 8 | 泳道 / 协作池 / 消息流 | `laneSet` `collaboration` `participant` | 设计器不提供，但**解析时容忍并忽略** |
| 9 | 动态流程修改 | `DynamicBpmnService` | |
| 10 | 表单引擎 | `formKey` + 表单渲染 | 只透传 `formKey`，由业务系统自己渲染 |
| 11 | 图形 PNG 生成 | `activiti-image-generator` | 改为前端 bpmn-js 只读渲染，省一个模块 |
| 12 | 8 种数据库方言 | 8 个 DDL 目录 | 只做 SQL Server + MySQL |

### 1.4 「同步执行」的口径确认

本方案把「同步执行」理解为 **并行网关（AND Gateway）的分叉与同步合并** —— 即经典三要素：

```
顺序执行   →  sequenceFlow 串接
分支/合并  →  exclusiveGateway（异或，取一条路）
同步执行   →  parallelGateway（与，多条路并行 + 同步等待全部到达）
```

如你的「同步」另有所指（例如"多分支完成后回调一个汇总服务"或"外部系统同步回调等待"），需在 P0 阶段澄清 —— 这会改变 §5.5 的合并判定逻辑。

---

## 2. 实测依据（本计划的数据底座）

对仓库 1,292 个 BPMN 测试文件做了一次分类扫描（脚本见 `docs/tools/scan-corpus.py`）：

| 指标 | 实测值 |
|---|---|
| BPMN 文件总数 | **1,292** |
| **仅含本方案子集元素（可直接做对拍）** | **554（42.9%）** |
| 含子集外元素（需改造或弃用） | 738（57.1%） |

### 子集语料的元素覆盖（554 个文件内）

| 元素 | 文件数 | 占比 |
|---|---:|---:|
| `startEvent` | 550 | 99% |
| `sequenceFlow` | 547 | 99% |
| `endEvent` | 535 | 97% |
| `userTask` | 382 | **69%** |
| `serviceTask` | 154 | 28% |
| `exclusiveGateway` | **49** | **9%** |
| `parallelGateway` | **48** | **9%** |
| `scriptTask` | 31 | 6% |
| 带 `conditionExpression` | 55 | 10% |
| 带 `default` 默认流 | 12 | **2%** |

### 结构组合

| 结构 | 文件数 |
|---|---:|
| 仅顺序流（无网关） | **457** |
| 含排他网关（分支/合并） | 49 |
| 含并行网关（同步） | 48 |
| **同时含两种网关** | **0** |

### 🔴 由此得到的重要结论

**现成语料能覆盖"顺序流"，但几乎覆盖不了"网关" —— 而这恰恰是本方案的全部难点所在。**

- 457 个纯顺序流用例：作为**回归基线**非常充足（保证基础路径不退化）；
- 网关语义只有 49 + 48 个，且**没有一个是"排他 + 并行"混合结构**，条件表达式仅 10%、默认流仅 2%；
- 因此 **网关语义的验证语料必须自建**（见 §9.3，建议自建 120 个）。这是本计划里最容易被人忽略、却最可能导致"上线才发现问题"的一处。

排除主力（738 个不可直接用语的 top 原因）：`boundaryEvent` 245、`subProcess` 221、`multiInstanceLoopCharacteristics` 161、`timerEventDefinition` 142、`messageEventDefinition` 104、`callActivity` 97、`signal/signalEventDefinition` 86/82。

---

## 3. 技术选型

| 领域 | Activiti (Java) | 本方案 (.NET) | 选型理由 |
|---|---|---|---|
| 运行时 | Java 25 | **.NET 10 LTS** (至 2028-11) | .NET 8/9 均于 2026-11 EOL，不可作基线 |
| 语言 | Java | **C# 14** | file-scoped namespace、集合表达式、主构造函数 |
| XML 解析 | StAX `XMLStreamReader` | **`System.Xml.XmlReader`** | 保持**流式**读取，不整树加载 —— 大流程文件关键 |
| XML 写入 | `XMLStreamWriter` | **`XmlWriter`** | |
| 数据访问 | MyBatis XML Mapper | **Dapper**（手写 SQL） | 贴近 MyBatis 的心智模型：SQL 完全可控、无隐式行为、贴近表结构。**不建议 EF Core**（跟踪/延迟加载会给"单命令单事务"语义引入不确定性） |
| 表达式语言 | JUEL (jakarta) | **自研 JUEL 子集**（`NetBpm.El`） | 见 §5.2，不引第三方 |
| JSON | Jackson | **System.Text.Json** | |
| 依赖注入 | Spring | **Microsoft.Extensions.DependencyInjection** | |
| 日志 | SLF4J | **Microsoft.Extensions.Logging** | |
| 脚本 | Groovy / Nashorn | **Roslyn Scripting**（可选） | 仅 `scriptTask`，二期 |
| 图形 | Java AWT | **不做** —— 前端 bpmn-js 渲染 | 省掉整个 `activiti-image-generator` 模块 |
| 设计器 | bpmn-js (bpmn.io) | **bpmn-js + 自定义 moddle 扩展** | 同源方案，见 §6 |
| 测试 | JUnit + H2 | **xUnit + Testcontainers** | Testcontainers 起真 SQL Server / MySQL |
| ORM 迁移 | Liquibase | **DbUp / 手工 SQL 脚本** | DDL 直接抄 Activiti 的 `activiti.mssql.create.*.sql` |

**目标框架说明**：锁定 **.NET 10 LTS**。若你的组织尚未升级，退到 .NET 8 意味着 2026-11-10 起失去支持（就在下个月），不可接受。

---

## 4. 解决方案结构

```
NetBpm.slnx
├── src/
│   ├── NetBpm.Bpmn.Model/          # BPMN 模型对象  (~25 类，对照 Activiti 的 94 个)
│   ├── NetBpm.Bpmn.Converter/      # XML ⇄ Model   (~20 类，对照 31+17 个)
│   ├── NetBpm.Bpmn.Validation/     # 流程校验       (~8 规则，对照 27 个)
│   ├── NetBpm.El/                  # 表达式语言      (JUEL 子集)
│   ├── NetBpm.Engine/              # 引擎运行时      (Agenda/Operation/Behavior/Execution/Command)
│   ├── NetBpm.Persistence/         # Dapper 仓储 + ACT_* 表映射 + ID 生成器
│   ├── NetBpm.Identity/            # 用户/组/候选关系（最小实现，可替换）
│   ├── NetBpm.Runtime/             # 组合根：配置、DI 扩展、生命周期
│   └── NetBpm.Rest/                # Minimal API 端点
├── web/
│   └── designer/                   # bpmn-js 设计器（TypeScript + Vite）
├── tests/
│   ├── NetBpm.UnitTests/
│   ├── NetBpm.IntegrationTests/    # Testcontainers 起真库
│   ├── NetBpm.DifferentialTests/   # 对拍（Java 预言机）
│   └── corpus/
│       ├── from-activiti/          # 554 个现成语料
│       └── authored/               # 120 个自建语料（网关语义）
└── tools/
    ├── Oracle/                     # Java 版跑批工具（产出预期快照）
    └── scan-corpus.py
```

### 模块职责与"照搬对照"

| 本方案模块 | 对照 Activiti 模块 | 代码量级（Java → .NET 估算） |
|---|---|---|
| `Bpmn.Model` | `activiti-bpmn-model` (94 类) | ~25 类 / 2,000 行 |
| `Bpmn.Converter` | `activiti-bpmn-converter` (48 类) | ~20 类 / 3,500 行 |
| `Bpmn.Validation` | `activiti-process-validation` (27 类) | ~8 类 / 900 行 |
| `El` | `activiti-juel-jakarta` (101 类) | ~15 类 / 3,000 行 |
| `Engine` | `activiti-engine/impl` (1,140 类) | ~90 类 / 14,000 行 |
| `Persistence` | `impl/persistence` (239 类) | ~25 类 / 4,000 行 |
| `Rest` + `Runtime` | `activiti-spring-boot-starter` | ~25 类 / 2,500 行 |
| `designer` | —（Activiti 9 无内置设计器） | ~40 文件 / 6,000 行 |

**净代码量估算：约 3.7 万行 C# + 6,000 行 TS ≈ Java 基线的 11%。**

---

## 5. 引擎运行时设计（核心，必须精确照搬）

### 5.1 执行树模型

引擎唯一的持久化状态是**执行树**，落在 `ACT_RU_EXECUTION`。三个布尔标志位是流程正确性的根基（对应 DDL 中的 `IS_ACTIVE_` / `IS_CONCURRENT_` / `IS_SCOPE_`）：

```csharp
public interface IDelegateExecution
{
    string Id { get; }
    string ProcessInstanceId { get; }          // 根执行的 Id 即流程实例 Id
    string ProcessDefinitionId { get; }
    string? ParentId { get; }
    string? CurrentActivityId { get; }
    IFlowElement? CurrentFlowElement { get; set; }   // 可为 SequenceFlow 或 FlowNode

    bool IsActive { get; }        // 是否处于"可推进"状态
    bool IsConcurrent { get; }    // 是否是分支产生的子执行
    bool IsScope { get; }         // 是否是作用域（能容纳子执行）
    bool IsEnded { get; }
    bool IsProcessInstanceType { get; }

    void Inactivate();            // 并行网关 Join 前会调用
    IVariableScope Variables { get; }
}
```

**语义要点**：`IsScope=true` 的执行才能持有子执行；流程实例根执行 `IsScope=true && ParentId=null`；分支产生的子执行 `IsConcurrent=true`。

### 5.2 表达式语言（`NetBpm.El`）

只实现 BPMN 条件与任务属性需要的那一部分，**以 JUEL 语义为基准**：

| 能力 | 支持 |
|---|---|
| 语法 | `${expr}` / `#{expr}`、字面量 `${true}`、纯文本原样返回 |
| 属性访问 | `${order.amount}`、`${user.dept.name}`、索引 `${list[0]}`、`${map['key']}` |
| 运算符 | `+ - * / div % mod`、`== != eq ne`、`< > <= >= lt gt le ge`、`&& and`、`\|\| or`、`! not`、`? :` |
| 关键字 | `empty`、`null`、`true`、`false`、字符串转义 |
| 方法调用 | `${user.hasRole('admin')}`、`${list.contains(x)}` |
| 隐式转换 | 数值提升、`null` 比较、`"1" == 1` 的行为**必须与 JUEL 逐条对齐** |

```csharp
public interface IExpressionManager
{
    IExpression CreateExpression(string expression);
}
public interface IExpression
{
    ValueTask<object?> GetValueAsync(IVariableScope scope, CancellationToken ct);
    ValueTask SetValueAsync(object? value, IVariableScope scope, CancellationToken ct);
}

// 条件判定的入口（对照 Activiti ConditionUtil.hasTrueCondition）
public static class ConditionUtil
{
    public static async ValueTask<bool> HasTrueConditionAsync(
        SequenceFlow flow, IDelegateExecution execution, CancellationToken ct)
    {
        // 无条件的顺序流 ⇒ 恒为 true（这是 Activiti 的关键行为）
        if (string.IsNullOrEmpty(flow.ConditionExpression)) return true;
        var expr = _exprManager.CreateExpression(flow.ConditionExpression!);
        var v = await expr.GetValueAsync(execution.Variables, ct);
        return v is bool b && b;
    }
}
```

> ⚠️ **不要用第三方表达式库**（Flee / NCalc / DynamicExpresso）。它们在**隐式类型转换、`null` 比较、方法调用解析**上的语义与 JUEL 差异很大，而这些差异恰好是流程分支出错的高发区。自研 15 个类、3,000 行，换来确定性。

### 5.3 Agenda + Operation 调度（**不要写成递归**）

Activiti 用"操作队列"（Agenda）而非递归调用来推进流程。**这不是设计偏好，而是正确性要求** —— 递归写法在并行 Join 场景下会因为"谁先到达"的时序不确定而产生错误结果。

```
命令入口
  └─ Agenda 队列 → 循环取出并排空 (drain)
       ├─ ContinueProcessOperation           执行当前元素的 ActivityBehavior
       ├─ TakeOutgoingSequenceFlowsOperation 选出口流、建并行子执行
       └─ EndExecutionOperation              清理执行、父执行继续 / 结束流程实例
```

**照搬要点**：一个命令 = 一个事务，事务内把 Agenda **排空**；不要把 agenda 的循环放到事务外，也不要在循环里 `await` 外部 IO（否则事务时长不可控）。

```csharp
public interface IEngineAgenda
{
    void PlanContinueProcessOperation(IExecutionEntity execution);
    void PlanTakeOutgoingSequenceFlowsOperation(IExecutionEntity execution, bool evaluateConditions);
    void PlanEndExecutionOperation(IExecutionEntity execution);
    ValueTask DrainAsync(CancellationToken ct);   // 排空队列
}
```

### 5.4 🔑 两条必须照搬的 Activiti 特有语义

这两条**不是 BPMN 规范原文**，是 Activiti 的实现选择。照搬它们，才能保证同一份流程在两端行为一致。

#### （1）排他网关：取"第一个条件为真"的流，而非"所有为真"的流

源码依据：`ExclusiveGatewayActivityBehavior.leave()`

```csharp
public override async ValueTask LeaveAsync(IDelegateExecution execution, CancellationToken ct)
{
    var gw = (ExclusiveGateway)execution.CurrentFlowElement!;
    SequenceFlow? chosen = null, defaultFlow = null;
    var defaultId = gw.DefaultFlow;

    // 取"第一个"满足条件且不是 default 的流 —— 语义与 BPMN 规范不同！
    foreach (var sf in gw.OutgoingFlows)
    {
        if (chosen is null
            && await ConditionUtil.HasTrueConditionAsync(sf, execution, ct)
            && (defaultId is null || defaultId != sf.Id))
        {
            chosen = sf;
        }
        if (defaultId is not null && defaultId == sf.Id)
            defaultFlow = sf;                    // 先记下来，后面可能用
    }

    _history.RecordActivityEnd(execution, null);

    execution.CurrentFlowElement = chosen
        ?? defaultFlow
        ?? throw new EngineException(
            $"No outgoing sequence flow of the exclusive gateway '{gw.Id}' " +
            "could be selected for continuing the process");

    await base.LeaveAsync(execution, ct);
}
```

**注意**：Activiti **没有**"多个条件同时为真则报错"的检查（BPMN 规范建议报错）。不要"顺手加个校验" —— 那会导致两端行为不一致。

#### （2）并行网关：按"入流条数"计数，而不是按"每条入流一个 token"

源码依据：`ParallelGatewayActivityBehavior.execute()`，其类注释里明确写了这是对规范 p.436 的有意偏离：

> *"We only check the number of incoming tokens to the number of sequenceflow. So if two tokens would arrive through the same sequence flow, our implementation would activate the gateway."*

```csharp
public override async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct)
{
    execution.Inactivate();                       // ① 先把自己置为非活动
    var gw = (ParallelGateway)execution.CurrentFlowElement!;
    LockFirstParentScope(execution);

    // ② 查"当前活动"为同一个 gateway 的非活动执行 —— 这是 Join 的判定基础
    var joined = await _execMgr.FindInactiveExecutionsByActivityIdAndProcessInstanceIdAsync(
        execution.CurrentActivityId!, execution.ProcessInstanceId, ct);

    int toJoin = gw.IncomingFlows.Count;          // ③ 期望值 = 入流条数
    _history.RecordActivityEnd(execution, null);

    if (joined.Count == toJoin)                   // ④ 全部到齐 → Fork
    {
        if (toJoin > 1)
            foreach (var j in joined.Where(j => j.Id != execution.Id))
                await _execMgr.DeleteExecutionAndRelatedDataAsync(j, null, ct);

        // ⑤ 注意：并行网关出口不做条件判定（evaluateConditions: false）
        _agenda.PlanTakeOutgoingSequenceFlowsOperation(execution, evaluateConditions: false);
    }
    // 否则：什么都不做，静默等待后续执行到达
}
```

**两个必须照搬的细节**：⑤ 并行网关出口**不校验条件**；④ 判定用"**总数相等**"而非"每条流都到过"。

### 5.5 分支产生与流程结束

```csharp
// TakeOutgoingSequenceFlowsOperation.leaveFlowNode —— 分支产生的唯一位置
// 关键：第一条流出流沿用当前执行（reuse），其余每条流新建一个子执行
SequenceFlow sf0 = outgoing[0];
execution.CurrentFlowElement = sf0;               // 复用现有执行
execution.IsActive = true;
var outgoingExecutions = new List<IExecutionEntity> { execution };

for (int i = 1; i < outgoing.Count; i++)          // 其余流：新建并发子执行
{
    var parent = execution.ParentId is not null ? execution.Parent! : execution;
    var child = _execMgr.CreateChildExecution(parent);
    child.CurrentFlowElement = outgoing[i];
    await _execMgr.InsertAsync(child, ct);
    outgoingExecutions.Add(child);
}

foreach (var e in outgoingExecutions)             // 全部建完后才统一推进
    _agenda.PlanContinueProcessOperation(e);
```

```csharp
// EndExecutionOperation —— 「合并」的收口判定
// 流程实例是否结束 = 是否还存在 isActive 的子执行
int activeChildren = await CountActiveChildExecutionsAsync(processInstanceId, ct);
if (activeChildren == 0)
    await _execMgr.DeleteProcessInstanceExecutionEntityAsync(processInstanceId, ...);
else
    /* 还有并行分支未完成，流程实例继续存活 */
```

> **并发分支的收口判定就是"合并"的本质** —— 这也是 §1.4 需要澄清"同步执行"口径的原因。

### 5.6 命令与拦截器

```csharp
public interface ICommand<T> { ValueTask<T> ExecuteAsync(ICommandContext ctx, CancellationToken ct); }

// 拦截器链（对照 Activiti 的 CommandInterceptor）
ICommandExecutor
  ├─ LogInterceptor          操作日志
  ├─ TransactionInterceptor  事务边界（一个命令 = 一个事务）
  └─ CommandContextInterceptor  命令上下文（Agenda / 各 Manager 的载体）
```

### 5.7 相对 Activiti 的三处"现代化"改动

| 改动 | Activiti | 本方案 | 理由 |
|---|---|---|---|
| 异步模型 | 同步阻塞 | **全链路 `ValueTask`** | .NET 原生；但**必须保持"单命令内同步排空 Agenda"**，否则语义漂移 |
| 上下文传递 | `ThreadLocal` + `Context` 静态类 | **`AsyncLocal<CommandContext>`** | .NET 下 `ThreadLocal` 在 async 续体间会丢/串。**加分析器规则禁止 `[ThreadStatic]`** |
| 变量序列化 | Java 原生序列化 → `ACT_GE_BYTEARRAY` | **JSON (System.Text.Json)** | Java 序列化产物 .NET 读不了。JSON 也带来可读性与跨语言性 |

---

## 6. 浏览器设计器方案

### 6.1 选型：bpmn-js + 自定义 moddle 扩展（不自研画布）

**理由（按权重排序）**：

1. **"照搬 Activiti 定义"这件事几乎零成本**：bpmn-js 是 bpmn.io 出品、BPMN 2.0 的参考实现级工具链，它输出的 `BPMN 2.0 XML + BPMNDI` **正好就是 Activiti 转换器的输入契约**。自己画布反而要重新对齐一遍格式。
2. **与 Activiti 同源**：Activiti 6 之后官方即采用 bpmn-js 替代早期基于 Oryx 的 Activiti Modeler。选它等于复刻官方路线。
3. **省 3–4 人月**：拖拽、连线、吸附、对齐、撤销重做、导入导出、缩放平移、键盘操作全自带。

### 6.2 需要自己做的五件事

| # | 工作 | 说明 |
|---|---|---|
| 1 | **`activiti-moddle.json` 扩展描述符** | 声明 `xmlns:activiti="http://activiti.org/bpmn"` 下的属性：`activiti:assignee` `activiti:candidateUsers` `activiti:candidateGroups` `activiti:formKey` `activiti:dueDate` `activiti:priority` `activiti:class` `activiti:delegateExpression` `activiti:expression` `activiti:skipExpression`，以及 `activiti:executionListener` / `activiti:taskListener` / `activiti:field` 等子元素。**没有它，bpmn-js 会在往返过程中丢掉这些属性** |
| 2 | **调色板裁剪** | 只保留 `startEvent` `endEvent` `exclusiveGateway` `parallelGateway` `userTask` `serviceTask` `manualTask` + 连线工具；移除子流程/边界事件/中间事件/泳道/数据对象等全部按钮 |
| 3 | **属性面板** | 按元素类型渲染：<br>· 顺序流 → 条件表达式编辑器 + "设为默认流"开关<br>· 用户任务 → 办理人 / 候选用户 / 候选组 / 表单 Key / 到期时间 / 优先级<br>· 服务任务 → 实现方式三选一（class / delegateExpression / expression）+ 字段注入<br>· 流程 → 流程 ID / 名称 / 是否可执行 |
| 4 | **即时校验** | 前端实时高亮：无出流节点、排他网关未设默认流且条件不全覆盖、节点 ID 重复、孤立节点、并行网关"分叉口数 ≠ 合并口数"、必填属性缺失。**后端再用 `Bpmn.Validation` 复校一次**（前端校验只是体验，不能作为可信边界） |
| 5 | **流程实例只读视图** | 复用 bpmn-js 的 `Viewer`，用 `ACT_HI_ACTINST` 数据着色已完成/当前/未到达节点 —— 替代 Activiti 的 `image-generator` |

### 6.3 技术栈与交付形态

```
TypeScript + Vite
bpmn-js@18
bpmn-js-properties-panel
diagram-js-minimap (可选)
```

- 设计器打包为**独立 Web Component / 可 iframe 嵌入**，便于嵌进客户业务系统；
- 与后端契约只有两个：`POST /api/designer/deploy`（提交 BPMN XML）、`GET /api/designer/definition/{id}/xml`（回显）；
- 设计器**不直接操作数据库**，一切经后端解析 + 校验 + 落库。

---

## 7. 数据库设计

### 7.1 核心决策：沿用 `ACT_*` 表名与列名

| 收益 | 说明 |
|---|---|
| **零设计成本** | DDL 直接取 `activiti.mssql.create.*.sql` / `activiti.mysql.create.*.sql` 的子集，列名、索引、字符集全部现成 |
| **对拍可行** | Java 版与 .NET 版**读写同一张表结构** → 可以直接做"**库级 dump 差异比对**"，这是最强的验证手段（见 §9.2） |
| **灰度可行** | 同一套库，理论上可 Java 跑存量、.NET 跑新增，或反向 |
| **生态复用** | 基于 `ACT_*` 的报表 / 监控 / BI 工具可直接用 |

### 7.2 表的保留与裁剪（25 张 → 13 张）

**保留 · 运行时（8 张）**

| 表 | 用途 |
|---|---|
| `ACT_GE_PROPERTY` | 库版本号（`schema.version`）+ ID 生成器状态（`next.dbid`） |
| `ACT_GE_BYTEARRAY` | 部署资源（`.bpmn20.xml` 原文、流程图） |
| `ACT_RE_DEPLOYMENT` | 部署记录 |
| `ACT_RE_PROCDEF` | 流程定义（含 `VERSION_` 版本号） |
| `ACT_RU_EXECUTION` | **执行树**（引擎唯一运行态） |
| `ACT_RU_TASK` | 待办任务 |
| `ACT_RU_IDENTITYLINK` | 任务候选关系（人 / 组） |
| `ACT_RU_VARIABLE` | 流程变量 |

**保留 · 历史（5 张）**

| 表 | 用途 |
|---|---|
| `ACT_HI_PROCINST` | 流程实例历史 |
| `ACT_HI_ACTINST` | 活动实例历史（**流程图着色数据源**） |
| `ACT_HI_TASKINST` | 任务历史 |
| `ACT_HI_VARINST` | 变量历史 |
| `ACT_HI_IDENTITYLINK` | 候选关系历史 |

**裁剪（12 张）**：`ACT_RE_MODEL`、`ACT_RU_JOB`、`ACT_RU_TIMER_JOB`、`ACT_RU_SUSPENDED_JOB`、`ACT_RU_DEADLETTER_JOB`、`ACT_RU_EVENT_SUBSCR`、`ACT_EVT_LOG`、`ACT_PROCDEF_INFO`、`ACT_RU_INTEGRATION`、`ACT_HI_DETAIL`、`ACT_HI_COMMENT`、`ACT_HI_ATTACHMENT`

> 裁掉 `ACT_HI_DETAIL` 的前提是**历史级别取 `AUDIT`**（Activiti 默认级别，只记活动/任务/变量实例，不记变量更新的明细）。若需要"变量变更流水"，需保留此表。

### 7.3 ⚠️ ID 生成器必须逐行对齐（易被忽略的兼容点）

Activiti 9 默认使用 **`DbIdGenerator`**（`ProcessEngineConfigurationImpl.initIdGenerator()`），机制是：

1. 从 `ACT_GE_PROPERTY` 读 `next.dbid`；
2. **一次性占走一个区块**（`idBlockSize`，默认 **2500**），把 `next.dbid` 更新为 `值 + 2500`；
3. 本次进程内从内存区块里自增发放（`2501`、`2502`、…）；
4. 区块用尽再去数据库占下一块。

**照搬要求**：本方案的 `IIdGenerator` 必须实现同样的"区块分配 + `REV_` 乐观锁更新"语义。若改成 UUID 或数据库自增，两端 ID 空间不同，对拍的库级 diff 会全线失败。

对应 DDL 初值：`ACT_GE_PROPERTY` 中 `('schema.version','8.1.4',1)`、`('next.dbid','1',1)`。

### 7.4 流程定义版本号

`ACT_RE_PROCDEF.VERSION_` 规则：同一 `KEY_` 下 `max(VERSION_) + 1`，每次 `deploy` 递增；`ID_` 形如 `{key}:{version}:{idGeneratorId}`。**格式必须一致**，否则跨端查询对不上。

---

## 8. 分阶段开发计划

> 人月 = 1 人 × 1 个月。合计 **27.5 人月（核心）** / **31–35 人月（含生产硬化、文档、示例工程）**。

### 阶段总览

| 阶段 | 名称 | 人月 | 依赖 | 交付物 | 验收标准 |
|---|---|---:|---|---|---|
| **P0** | 工程底座 | 0.5 | — | SLNX 骨架、集中包版本、`Directory.Build.props`、CI（构建+测试+格式检查）、日志/异常/DI 扩展、分析器规则（禁 `[ThreadStatic]`） | `dotnet build` 全绿；CI 在 PR 上跑通；空引擎能通过一个 `NoOpCommand` |
| **P1** | BPMN 模型层 | 1.0 | P0 | `NetBpm.Bpmn.Model` ~25 类（`BpmnModel`/`Process`/`FlowElement`/`FlowNode`/`SequenceFlow`/`Gateway`/`ExclusiveGateway`/`ParallelGateway`/`StartEvent`/`EndEvent`/`UserTask`/`ServiceTask`/`ManualTask`/`Activity`/`GraphicInfo`…） | 模型序列化/反序列化单测全绿；与 Activiti 的类名/属性名对照表评审通过 |
| **P2** | BPMN 转换层 | 3.0 | P1 | `BpmnXMLConverter`、`BaseBpmnXMLConverter`、10 个元素转换器、6 个解析器（Definitions/Process/BpmnShape/BpmnEdge/ExtensionElements/PotentialStarter）、子转换器（条件表达式/字段/监听器）、反向导出 | **往返保真测试**：XML → Model → XML 与原文语义一致（DI 坐标、扩展属性零丢失）；554 个现成语料**全部解析成功** |
| **P3** | 表达式语言 | 2.5 | P0 | `NetBpm.El`：词法/语法分析、AST、求值器、类型转换、方法调用解析 | 与 JUEL 的行为对拍：**≥200 条表达式用例**，隐式转换/`null` 比较/短路求值逐条一致。✅ 已实现，见 **§15.9**（223 条用例；优先级表、转换与运算逐条照搬 `activiti-juel-jakarta`） |
| **P4** | 校验层 | 0.5 | P1 | 8 条规则（无出流、排他网关默认流、孤立节点、ID 唯一、并行网关入出流、必有开始/结束事件、`sequenceFlow` 引用完整性、必填属性） | 每条规则配正/反用例；错误信息可定位到元素 ID |
| **P5** | 持久化层 | 3.0 | P0 | Dapper 仓储（13 张表）、`IIdGenerator`（DbIdGenerator 等价）、会话/缓存（一级缓存 + 脏检查 + flush 顺序）、事务拦截器、DDL 脚本（SQL Server + MySQL） | **对拍 `ACT_*` 行长一致**；并发 50 线程取 ID 无重复无空洞；`schema.version` 校验生效 |
| **P6** | **引擎运行时** | **5.0** | P2 P3 P5 | `ExecutionEntity`/执行树管理、`Agenda` + 3 个 Operation、8 个 `ActivityBehavior`、`CommandContext` + 拦截器链、143 个 Command → **~35 个**、事件分发器 | **本阶段是全部难点所在**：顺序/排他/并行三类语义对拍一致率 ≥99.5%；执行树快照逐节点比对通过 |
| **P7** | 任务与身份 | 1.5 | P6 | `UserTaskActivityBehavior` 全生命周期（create / assignee / candidate / claim / complete / delegate 可选）、`IdentityLink`、任务查询 | 382 个含 `userTask` 的语料全跑通；候选组解析与 Activiti 一致 |
| **P8** | 设计器前端 | 3.5 | P1（元素契约） | bpmn-js 集成、moddle 扩展描述符、调色板裁剪、属性面板、即时校验、导入导出、只读实例视图 | 画出 §1.2 全部元素并导出 XML，后端解析成功率 100%；**往返不丢属性** |
| **P9** | REST + 宿主 | 2.5 | P6 P7 P8 | Minimal API（部署 / 启动 / 查待办 / 完成任务 / 查变量 / 查历史 / 流程图数据）、认证授权钩子、`NetBpm.Runtime` 组合根 | Swagger 全端点可用；一条完整业务流（申请→审批→并行会签→归档）端到端跑通 |
| **P10** | 对拍验证与测试体系 | 2.5 | P6 起滚动 | Java 预言机跑批工具、对拍执行器、三维度比对器（API 返回值 / `ACT_*` 行快照 / 事件序列）、554 现成 + 120 自建语料、CI 集成 | **一致率 ≥99.5%**，差异清单 100% 有结论（修复或有意偏离并记录） |
| **P11** | 硬化与文档 | 2.0 | 全部 | 并发压测、长流程性能、崩溃恢复、日志与诊断、部署文档、开发指南、示例工程（控制台 + Web） | 压测：单实例 ≥500 流程实例/秒启动；P99 命令耗时达标；文档可让新人 1 天上手 |
| | **合计** | **27.5** | | | |

### 8.1 关键路径与并行安排

```
关键路径：P0 → P1 → {P2 | P3 | P5} → P6 → P7 → P9 → P10 → P11

可并行：
  · P2（转换器）  ┐
  · P3（表达式）  ├─ P1 完成后三线并行，互不依赖
  · P5（持久化）  ┘
  · P8（设计器）  —— 只需 P1 交付的"元素契约"，可与 P2/P3/P5 同时开工
  · P10（对拍）   —— P6 启动后即可滚动搭建
```

**人员配置建议（5 人）**

| 角色 | 主责 | 兼责 |
|---|---|---|
| 引擎架构师（1） | **P6 引擎运行时**（关键路径，不可分担） | P0、P5 评审 |
| 后端 A（1） | P2 转换层 → P4 校验 | P1 |
| 后端 B（1） | P3 表达式语言 → P10 对拍 | P5 |
| 后端 C（1） | P5 持久化 → P9 REST/宿主 | P7 |
| 前端（1） | P8 设计器 → 只读实例视图 | P9 端点联调 |

### 8.2 排期（5 人团队，约 7 个月）

| 月份 | 里程碑 | 主要产出 |
|---|---|---|
| **M1** | 底座就绪 | P0 完成；P1 完成；P8 脚手架 + moddle 描述符 |
| **M2** | 解析就绪 | P2 完成（554 语料全解析）；P3 完成 60%；P5 完成 40%；P8 调色板 + 属性面板可用 |
| **M3** | **POC 门禁** ⚠️ | P3 完成；P5 完成；P8 完成设计器主功能；**端到端 POC**：设计器画→部署→顺序流跑通（见 §11） |
| **M4** | 引擎骨架 | P6 完成 50%：命令/拦截器/Agenda/执行树/顺序流 + 排他网关语义对拍通过 |
| **M5** | **引擎完成** | P6 完成：并行网关同步语义对拍通过；P7 完成；P9 完成 50% |
| **M6** | 全链路贯通 | P9 完成；P10 对拍一致率达标；P4 完成校验规则 |
| **M7** | 收尾硬化 | P11：压测、恢复、文档、示例、**发布 1.0** |

**4 人团队**（去掉一个后端）→ 顺延至 **9–10 个月**。**不建议低于 3 人**：P6 与 P10 无法被压缩，人再少只是延期。

---

## 9. 测试与对拍验证策略

### 9.1 为什么必须要对拍

本方案是"照搬语义"，那么**判定正确的唯一客观标准就是：与 Activiti 的输出一致**。而 Activiti Java 版就是现成的、免费的、权威的"**预言机（Oracle）**"。这比自建基准便宜一个数量级 —— 这是"照搬"策略最核心的红利。

### 9.2 三维度对拍（本方案的最大优势）

```
        ┌──────────────────────────────┐
        │  同一份 BPMN + 同一操作脚本   │
        └───────────┬──────────────────┘
                    │
        ┌───────────┴───────────┐
        ▼                       ▼
  ┌───────────┐          ┌───────────┐
  │ Java 版   │          │ .NET 版   │
  │ (预言机)  │          │ (被测)    │
  └─────┬─────┘          └─────┬─────┘
        │                      │
        └──────────┬───────────┘
                   ▼
      ┌─────────────────────────────────┐
      │ ① API 返回值 / DTO 结构          │
      │ ② ACT_* 行快照（表结构相同！）   │
      │ ③ 事件序列（ACTIVITY_*/SEQ_TAKEN）│
      └─────────────────────────────────┘
```

**②是杀手锏**：因为 §7.1 决定沿用 `ACT_*` 表名与列名，两端跑完后可以直接 `dump` 出所有行做逐列比对。这能捕获 API 层看不出的执行树内部差异 —— 例如 `IS_CONCURRENT_` / `IS_SCOPE_` 标志位错、子执行多建/少建、Join 时该删的执行没删。

> 若当初选了"自定义表结构"，这一整套验证手段都不可用，只能退回到靠 API 返回值猜内部状态。这正是 §7.1 那个决定的真正价值。

### 9.3 语料策略（基于 §2 实测）

| 来源 | 数量 | 用途 | 状态 |
|---|---:|---|---|
| **现成 · 纯顺序流** | 457 | 回归基线 —— 保证基础路径不退化 | ✅ 直接可用 |
| **现成 · 含排他网关** | 49 | 分支/合并语义 | ✅ 直接可用 |
| **现成 · 含并行网关** | 48 | 同步语义 | ✅ 直接可用 |
| **自建 · 网关组合** | ~60 | **排他 + 并行混合结构**（现成 0 个）、嵌套分支、菱形合并 | ⚠️ 必须自建 |
| **自建 · 条件表达式** | ~40 | 多条件全假走 default、表达式边界（`null`/类型转换/短路） | ⚠️ 必须自建 |
| **自建 · 阻抗用例** | ~20 | 并行网关同一入流多 token、Join 部分到达即挂起、单入单出网关 | ⚠️ 必须自建 |
| | **~674** | | |

**自建语料用脚本生成**（不要手画），保证组合覆盖是穷举的而不是"想起来的"。生成器放 `tests/corpus/generator/`。

### 9.4 一致率目标

| 维度 | 目标 | 说明 |
|---|---|---|
| API 返回值 | ≥ 99.5% | 结构性差异（字段名/格式）需 100% 消除 |
| `ACT_*` 行快照 | ≥ 99.5% | 允许的差异仅限时间戳、ID 具体值（若区块分配时序不同） |
| 事件序列 | ≥ 99% | 事件顺序比事件集合更重要 |
| **整体门禁** | **≥99.5%，且差异清单 100% 有结论** | 每个差异要么修，要么在文档里记录为"有意偏离" |

---

## 10. 风险登记表

| # | 风险 | 概率 | 影响 | 等级 | 对策 |
|---|---|---|---|---|---|
| **R1** | **并行网关同步语义实现偏差**（计数逻辑、`inactivate` 时机、Join 后删除哪些执行） | 高 | 高 | 🔴 **极高** | 唯一对策：**死磕对拍**。把 §5.4(2) 的 5 个步骤 + `ACT_RU_EXECUTION` 行快照作为硬门禁，不通过不进入下一阶段 |
| **R2** | **表达式语言与 JUEL 语义漂移**（隐式转换 / `null` 比较 / 方法调用） | 高 | 高 | 🔴 **极高** | 自研不引第三方（§5.2）；建 ≥200 条表达式对拍语料；P3 必须在 P6 之前完成并验收 |
| **R3** | **执行树标志位组合错误**（`isActive`/`isConcurrent`/`isScope`） | 高 | 高 | 🔴 高 | 不用"最终状态"验收，改用**树形快照对拍**（每步都 diff）；写"标志位不变量"断言（如：`IsConcurrent ⇒ ParentId != null`） |
| **R4** | **async/await 与 Agenda 排空的交错** | 中高 | 高 | 🟠 高 | 硬约束："单命令内同步排空 Agenda"；分析器规则禁止 `[ThreadStatic]`；`AsyncLocal` 上下文写专项测试（含 `ConfigureAwait(false)` 场景） |
| **R5** | **设计器往返丢属性/丢 DI** | 中 | 中 | 🟠 中 | `activiti-moddle.json` 描述符必须先行；P8 验收含"往返保真"断言；设计器导出的 XML 全部纳入 P2 解析回归集 |
| **R6** | **范围蔓延** —— 有人要加子流程 / 多实例 / 会签 | **高** | 高 | 🔴 **高（最大管理风险）** | §1.3 的 12 条"不做"写成**书面签字基线**；任何追加走变更评审并重算人月。**引擎项目最常见的死因就是这个**，不是技术 |
| **R7** | 存量 Java 数据/流程迁移 | 中 | 中 | 🟠 中 | 表结构同构（§7.1）已大幅缓解；变量序列化差异（Java 序列化 vs JSON）需在迁移时用工具转换；若确需迁移，单独立项 |
| **R8** | DbIdGenerator 对齐不当导致 ID 空间冲突 | 中 | 中 | 🟡 中 | §7.3 逐行对齐 + 并发专项测试；若两端同库并行，务必确认区块分配互斥 |
| **R9** | 商标/命名合规 | 低 | 中 | 🟡 低 | 见 §13 |
| **R10** | 设计器与后端元素契约不同步 | 中 | 低 | 🟡 低 | 元素契约以 `Bpmn.Model` 为单一事实源；CI 中加"设计器 moddle 描述符 ↔ 模型类"一致性检查 |

---

## 11. 首个 8 周行动清单（M1–M2）

> 核心原则：**前面 90 天不追求功能数量，而是把 R1/R2 两个最大风险打穿、把工程底座焊死。**

| 周 | 目标 | 具体动作 | 完成标志 |
|---|---|---|---|
| **W1** | 立项与基线冻结 | §1.3 的 12 条"不做"形成书面基线并签字；确认 §1.4「同步执行」口径；确认是否需要会签（多实例） | 基线文档签字 |
| **W1–W2** | P0 工程底座 | `NetBpm.slnx`、`Directory.Build.props`、集中包版本、CI（build + test + format）、异常体系、DI 扩展、分析器规则 | CI 全绿；空命令跑通 |
| **W2–W3** | P1 模型层 | 25 个模型类 + 与 Activiti 类名/属性名对照表 | 模型单测全绿，对照表评审通过 |
| **W3** | 语料准备 | 跑 `docs/tools/scan-corpus.py` 抽出 554 个现成语料；确认纯顺序流 457 个全部可解析 | 语料目录就位（`tests/corpus/from-activiti/`） |
| **W3–W6** | **P2 转换层（重点）** | 流式 XML 读取 + 元素转换器 + DI + 扩展属性 + 反向导出 | **554 个语料 100% 解析成功**；往返保真零丢失 |
| **W4–W7** | **P3 表达式语言（重点）** | 词法/语法/AST/求值器 + 类型转换 | 与 JUEL 对拍 ≥200 条用例通过（含隐式转换边界） |
| **W5–W7** | P5 持久化 | Dapper 仓储 + `DbIdGenerator` 对齐 + DDL 双库 | 13 张表读写通；并发取 ID 无重复无空洞 |
| **W6** | 预言机工具 | Java 版跑批工具：输入 BPMN + 操作脚本 → 输出三维度快照 JSON | 工具输出可复现、可入库 |
| **W8** | **POC 门禁** ⚠️ | 端到端最小闭环：**设计器画一个"开始→排他网关→两条分支→并行合并→结束"的流程 → 导出 XML → 后端解析校验部署 → 启动实例 → 用 457+97 语料对拍** | **判定点**：若 EL 一致率 <95%，或执行树快照出现系统性偏差 → **回到方案层重新评估，不要继续堆代码** |
| **W8** | 设计器脚手架 | bpmn-js 集成 + `activiti-moddle.json` + 调色板裁剪 | 能画出 §1.2 全部元素并导出正确 XML |

---

## 12. 与既有结论的衔接

本计划对应此前分析中的「**第三条路：受控子集移植**」：

| 方案 | 工程量 | 说明 |
|---|---:|---|
| 全量移植 Activiti | 35–48 人月 | 语义等价 + 数据库兼容 + 完整交付 |
| 全新开发（同功能集） | 50–65 人月 | 需自行定义语义，无预言机 |
| **本方案（受控子集 + 照搬 Activiti 定义）** | **27.5 人月（核心）/ 31–35（含硬化）** | 借鉴 Activiti 的语义正确性 + .NET 原生架构 + 只做需要的子集 |

**为什么这个数字落在"受控子集"区间**：砍掉了 58 个行为类中的 50 个、25 张表中的 12 张、143 个 Command 中的 108 个、8 种数据库方言中的 6 种，同时保留了**最贵的两样东西** —— Activiti 沉淀的语义正确性，以及可对拍的预言机。

---

## 13. 命名与合规

- **代码许可**：Activiti 采用 **Apache License 2.0**，允许衍生与商业闭源使用，需保留版权声明与 NOTICE。**建议在本方案中仅"参考实现语义"，代码从零撰写**，规避任何复制粘贴带来的许可与审计负担。
- **商标**：Activiti 商标已归属 Hyland Software。**产品名请勿使用 "Activiti"**（如 `ActivitiNet`）。工作代号建议 `NetBpm`，正式命名由产品侧决定。
- **XML 命名空间保留 `http://activiti.org/bpmn`**：这是**技术必需** —— 只有沿用该命名空间，才能与 Activiti 的流程定义文件互相解析。命名空间是格式约定，不是商标使用，此处可以且应当保留 `xmlns:activiti`。
- **本方案不复制 Activiti 源码**，只对齐公开的格式规范（BPMN 2.0 OMG 标准）与行为语义。

---

## 14. 需要你确认的 4 个问题

1. **「同步执行」的口径**：是否就是并行网关（AND）的分叉 + 同步合并？若有"外部回调等待"含义，需追加语义设计（§1.4）。
2. **是否需要「会签 / 多实例」**：`multiInstanceLoopCharacteristics` 被列入不做，但它是审批场景最高频的追加需求。若确定需要，需 **+2.5 人月**并提前到 P6 一并设计。
3. **是否沿用 `ACT_*` 表结构**：这是本方案对拍能力的基础（§7.1）。若你的团队更倾向"自定义表结构 + EF Core"，需 **+1.5 人月** 做表设计，且**永久失去库级对拍能力**（§9.2）。
4. **设计器宿主形态**：独立 Web 应用，还是必须嵌入现有业务系统（Web Component / iframe）？后者需在 P8 追加约 0.5 人月做集成适配。

---

*附：本计划的数据依据来自仓库实测（`docs/tools/scan-corpus.py`）与源码逐行阅读（`ExclusiveGatewayActivityBehavior` / `ParallelGatewayActivityBehavior` / `TakeOutgoingSequenceFlowsOperation` / `ContinueProcessOperation` / `EndExecutionOperation` / `ConditionUtil` / `BpmnActivityBehavior` / `ProcessEngineConfigurationImpl.initIdGenerator` 等）。*

---

## 15. 实施勘误与已核实事实（持续更新）

> ⚠️ **2026-10-12 事故与重建说明**：本节原文曾因一次误用"整文件写入"的工具调用被覆盖，
> §0–§14 已从 `E:\DataCode\Activiti-9.0.0\docs\` 下的同期副本完整还原；
> §15 按当次会话的上下文重建。**§15.1–15.3 与 §15.7 未能逐字还原**（上下文只保留了部分条目），
> 已保留可核实条目并把缺口显式标为"待补"——不编造内容。§15.4 起的内容为逐字重建。

### 15.1 与本文档前文不一致的更正

| # | 前文的说法 | 更正 |
|---|---|---|
| 1 | "分支产生的子执行 `IS_CONCURRENT_ = 1`" | Activiti 9 中 `IS_CONCURRENT_` **永远不会被置为 1**（`setConcurrent` 在整个 `activiti-engine` 内没有任何调用点，字段默认 `false`）。库级对拍会在该列上全线出现系统性差异，必须按"恒为 false"实现 |
| 2 | "初始执行树由流程实例根执行直接推进" | 初始执行树是「**根执行**（`IS_SCOPE_=1`、`PARENT_ID_=null`、`ACT_ID_` 恒为 null，**不参与流程推进**）+ **一个访问全部流程元素的子执行**」。若把根执行也放进推进路径，并行网关 Join 的"删除其余非活跃执行"会误删流程实例根执行 |

### 15.2 需要照搬但前文未写明的行为细节

| # | 主题 | 事实 |
|---|---|---|
| 10 | 候选值切分 | 候选用户 / 候选组按 `str.split("[\\s]*,[\\s]*")` 切分（不是简单 `Split(',') + Trim`）—— 尾随空白保留与否会影响对拍 |
| 11 | 用户任务触发前置校验 | 执行上若还有未删除的任务，`UserTaskActivityBehavior.trigger` 抛 `UserTask should not be signalled before complete`（错误文案也参与对拍，§9.4） |
| 12 | 变量作用域 | `setVariable(x, v)` 等价于 `setVariable(x, v, getParent() != null ? getParent() : this)` —— **变量落在最上层变量作用域**（本子集即流程实例根执行）。否则并行兄弟分支互相看不到对方写入的变量 |
| 13 | `dueDate` | 走业务日历解析；`activiti:businessCalendarName` 不在 §1.2 属性清单内，故本子集只接受 ISO-8601 日期串，其余取值**响亮失败**（不猜） |
| 14 | 跨命令恢复等待型节点 | 执行实体只带 `ACT_ID_` 字符串，必须靠流程定义解析回元素（Activiti 在 `ExecutionEntityImpl.getCurrentFlowElement()` 里懒解析 + 缓存）。本方案抽成 `IProcessDefinitionResolver` 端口，宿主必须注册；否则"完成任务"这类跨命令操作无法继续流程 |

> 待补：第 1–9 条（身份关系行写入规则、任务行 `REV_`、`ACT_TYPE_` 取值、`END_ACT_ID_` 等）在原文档被覆盖时一并丢失，需要时按 Activiti 源码重列。

### 15.3 有意偏离与对拍忽略项

| # | 项 | 说明 |
|---|---|---|
| 15 | `ENGINE_VERSION_` 列 | 写入 `netbpm-{版本}`，必然与 Activiti 的 `"9.0.0"` 不同 ⇒ 已列入 `SnapshotComparer` 的默认忽略列 |
| 16 | 时间戳列 | `START_TIME_` / `END_TIME_` / `CREATE_TIME_` / `CLAIM_TIME_` / `DEPLOY_TIME_` 等列入默认忽略列（执行时刻必然不同） |
| 17 | 查询语句省略 `SUPER_EXEC_` 的 `LEFT JOIN` | 该 JOIN 只为取"父流程实例 id"（调用活动场景）而存在；本子集不支持调用活动，省略不改变选取的列与行内容 |
| 18 | `documentation`（任务描述） | §1.2 的 `userTask` 属性清单未含 `documentation`，故不采集，`ACT_RU_TASK.DESCRIPTION_` 恒为 `null`（解析时按 §1.3 第 8 条容忍并忽略） |
| 19 | `activiti:owner` | §1.2 的 `userTask` 属性清单未列出，但 Activiti 的 `handleAssignments` 会处理它、且 `OWNER_` 列存在，故**已实现**（模型 / 转换器 / 行为三处） |
| 20 | 任务行不做脏检查 | Activiti 靠 `DbSqlSession` 的脏检查在 flush 时统一写库；本实现对任务采用**即时写**，因此"只改了 `CLAIM_TIME_`"的场景需显式调用 `UpdateTaskAsync`（内部 API，已注明） |
| 21 | 清表顺序 | `ACT_RU_IDENTITYLINK.TASK_ID_ → ACT_RU_TASK.ID_`（`ACT_FK_TSKASS_TASK`）要求**身份关系先于任务删除** —— 开发/测试用清表工具必须遵守 |

### 15.4 变量持久化与历史写入（P5 后半段）

**A. 必须照搬的事实（前文未写明）**

| # | 主题 | 事实 |
|---|---|---|
| V1 | 变量的持有者 | `ACT_RU_VARIABLE.EXECUTION_ID_` 指向**实际持有该变量的作用域执行**（不是「触发设置的执行」）；`setVariable` 会尽量上溯到最高的已有作用域，到顶仍无才新建 —— 所以流程变量通常挂在<b>流程实例执行</b>上 |
| V2 | 同作用域读取 | 查询语句带 `TASK_ID_ is null` 过滤（`selectVariablesByExecutionId`）—— 任务局部变量不算作作用域变量 |
| V3 | 变量懒加载 | 第一次访问作用域时把该作用域<b>全部</b>变量一次查出来（`ensureVariableInstancesInitialized`）；未命中再沿父作用域上溯；跨命令读回时父执行要能懒加载（`ExecutionEntityImpl.getParent()`） |
| V4 | `REV_` | 数据管理器 `create()` 显式置 **0**，INSERT 语句写死 **1**，之后每次更新 +1（乐观锁） |
| V5 | 类型与列布局 | `string`→`TEXT_`；`integer`/`long`→`LONG_` **和** `TEXT_`（数字的字符串副本）；`double`→只写 `DOUBLE_`；`boolean`→只写 `LONG_`（1/0）；`date`→只写 `LONG_`（UTC 毫秒，即 Java `Date.getTime()`）；`null`→`TYPE_='null'` 且值列全空。`TEXT2_` **不是** 4000 截断列，而是 json 类型的「类全名」辅助列 |
| H1 | 历史行 id 的来源 | `ACT_HI_PROCINST.ID_ = PROC_INST_ID_ =` 流程实例 id；`ACT_HI_TASKINST.ID_ = ACT_RU_TASK.ID_`；`ACT_HI_VARINST.ID_ = ACT_RU_VARIABLE.ID_`；`ACT_HI_IDENTITYLINK.ID_ = ACT_RU_IDENTITYLINK.ID_`；**只有 `ACT_HI_ACTINST.ID_` 是新生成的 id** |
| H2 | 历史级别门槛 | `ACTIVITY` 及以上：实例起止、活动起止、变量增改删、`recordTaskAssignment`/`recordTaskId`（回填 `ACT_HI_ACTINST`）；`AUDIT` 及以上：任务创建/结束/字段变更、身份关系；**只有 `FULL` 才写 `ACT_HI_DETAIL`**（本子集已裁掉该表，故永不写） |
| H3 | `ACT_TYPE_` | 取元素**类名首字母小写**（`userTask`/`serviceTask`/`exclusiveGateway`…），不是 `getProperty("type")` |
| H4 | 顺序流不产生活动行 | 执行到顺序流时收口的是<b>源节点</b>那一行（`findActivityInstance` 对 `SequenceFlow` 取 `sourceFlowElement`），因此 `ACT_HI_ACTINST` 里没有 `sequenceFlow` 行 |
| H5 | 历史行的回填 | 任务落库后回填 `ACT_HI_ACTINST.TASK_ID_`；指派/变更办理人后回填 `ACT_HI_ACTINST.ASSIGNEE_`（都发生在建行之后） |
| H6 | `END_ACT_ID_` / `DELETE_REASON_` | `deleteProcessInstanceExecutionEntity(processInstanceId, currentFlowElementId, deleteReason, …)`：`END_ACT_ID_` 取<b>操作自身执行</b>的当前元素（`EndExecutionOperation` 有专门注释），正常结束路径 `DELETE_REASON_` 为 `null` |
| H7 | 历史关系只增不删 | `deleteIdentityLinksByTaskId` 传 `cascadeHistory = false`（`IdentityLinkEntityManagerImpl:341`）⇒ 任务完成/实例结束后 `ACT_HI_IDENTITYLINK` 行<b>保留</b> |
| H8 | 变量历史行的生命周期 | 创建：与运行行<b>同 id</b> 插入，`CREATE_TIME_ = LAST_UPDATED_TIME_ =` 当前时间；更新：**同一行 UPDATE** 且 `REV_` 跟运行行自增；删除：历史行被删除（本子集未实现删变量） |
| H9 | 中止判据 | 查找未结束的历史活动行按 `EXECUTION_ID_ + ACT_ID_ + END_TIME_ is null`；`markEnded` 只在 `END_TIME_` 为空时写（可重复调用） |

**B. 有意偏离与当前限制**

| # | 项 | 说明 |
|---|---|---|
| V6 | 历史即时写 | Activiti 的历史实体进 `DbSqlSession` 一级缓存、flush 时统一落库；本实现在<b>同一事务内即时写</b>，提交后可见结果一致 |
| V7 | 变量更新的写入次数 | 类型变化时 Activiti 会先清空旧值列再写新值；本实现一次 UPDATE 写全部值列（最终行内容一致） |
| V8 | 超长字符串与复杂类型 | `>4000` 字符的字符串（`longString`）、`bytes`/`serializable`/`json` 需要 `ACT_GE_BYTEARRAY` 参与且字节布局是 Java 序列化 —— 本子集**响亮失败**，绝不截断 |
| V9 | 内存宿主不写变量历史 | 变量历史由变量实例仓储写入；内存宿主没有变量仓储 ⇒ 不写。因此 `EnableHistory = true` 与 `UseInMemoryExecutionTree = true` 的组合被<b>配置校验直接拒绝</b>（不静默忽略） |
| V10 | 任务字段变更历史 | Activiti 有九个 `recordTaskXxxChange`；本子集收敛为一个 `RecordTaskFieldsChangedAsync`（同步 `ACT_HI_TASKINST` 的可变列），行内容一致 |
| V11 | 变量 `date` 的时区 | `DateTime` 按本地时区解释后写 UTC 毫秒；`Kind = Utc` 的输入直接取用 |
| V12 | 尚未实现 | 删变量（`removeVariable`）、任务局部变量（`TASK_ID_` 非空）、变量历史级联删除、`ACT_HI_DETAIL` |

### 15.5 P9 实施记录（REST 宿主与端到端）

**端点清单（P9 原始 13 个；2026-10-19 的 B2.7 又补齐 11 个产品化端点，见任务清单与 README 的完整清单）**
> 全部端点都有 OpenAPI 描述，由 `RestEndToEndTests.OpenApi_ShouldExposeEveryEndpoint` 锁死。
> 新增部分：实例列表 / 任务详情 / 身份关系 / 转办 / 跳转 / 变量单写与批写 / 历史实例列表 / 跨实例已办 / 历史变量 / 定义分页与版本 / 候选组查询；
> 以及 P1 批次：任务字段修改 / 候选用户与候选组增删 / 委派与解决 / 表单元数据 / BPMN 试校验 / 身份只读查询；
> 以及 P2：部署列表与资源 / 待办统计 / 批量转办与批量删除 / live+ready 探针 / 运维信息 / `expectedRevision` 乐观并发（409）/
> API Key 鉴权与身份租户透传 / `Idempotency-Key`（内存 + `ACT_IDEMPOTENCY`）/ `?envelope=true` 分页信封。

| 端点 | 说明 |
|---|---|
| `POST /api/designer/deploy` | 提交 BPMN XML（设计器契约 ①） |
| `GET /api/designer/definition/{id}/xml` | 回显 BPMN XML（设计器契约 ②） |
| `POST /api/runtime/process-instances` | 启动实例（可带变量） |
| `GET /api/runtime/process-instances/{id}` | 运行中实例（结束后 404，改查历史） |
| `GET /api/runtime/process-instances/{id}/variables` | 流程变量 |
| `GET /api/runtime/process-instances/{id}/diagram` | **流程图数据**：BPMN XML + 活动高亮 |
| `GET /api/runtime/tasks?assignee=&candidateUser=` | 待办查询 |
| `POST /api/runtime/tasks/{id}/claim` | 认领 / 取消认领 |
| `POST /api/runtime/tasks/{id}/complete` | 完成任务（可带变量） |
| `GET /api/runtime/history/process-instances/{id}` | **查历史**：历史流程实例 |
| `GET /api/runtime/history/process-instances/{id}/activities` | 历史活动（着色 / 审计） |
| `GET /api/runtime/history/process-instances/{id}/tasks` | 历史任务（已办查询） |
| `GET /health` | 健康检查 |

**实施中抓到的两个坑（都已在代码内固化说明）**

| # | 现象 | 根因与处置 |
|---|---|---|
| R1 | 任何「带变量的启动/完成任务」REST 调用都 400：引擎报「变量类型 `System.Text.Json.JsonElement` 不支持」 | System.Text.Json 把 `object?` 反序列化成 `JsonElement`。契约改为 `IDictionary<string, JsonElement>` 并在 **REST 边界**用 `JsonVariableReader` 还原成 `string/long/double/bool/null`（数值优先 `long`，使 `{"days":3}` 落成 `LONG_=3` 而非 `DOUBLE_=3.0`）。**这是端到端测试才暴露的缺陷**，引擎单测与仓储测试都测不到 |
| R2 | REST 端到端测试「全绿」但数据库一行没写：测试实际跑在内存宿主上 | 宿主 `Program.cs` 在**服务注册阶段**就读了配置（`AddNetBpm(builder.Configuration)`），而 `WebApplicationFactory.ConfigureAppConfiguration` 的回调要等 `Build()` 才生效 ⇒ 注入被静默忽略。必须用 `IWebHostBuilder.UseSetting`（宿主设置参与 `WebApplicationBuilder.Configuration` 构造，注册阶段可见） |

> ⚠️ R2 是通用陷阱：**任何"启动时读配置"的宿主**都无法用 `ConfigureAppConfiguration` 覆盖，测试会以"看起来通过、其实没连库"的方式通过。集成测试必须断言**库里真的有了行**（本项目由 `MySqlVariableAndHistoryTests` 的行级断言兜底）。

### 15.6 P8 实施记录（设计器属性面板与即时校验）

**属性面板白名单（即 §6.2 第 3 项的全部内容）**

| 元素 | 面板可编辑的属性 |
|---|---|
| 流程 | id、name（`isExecutable` 只读：后端导出恒写 `true`） |
| 所有元素 | id、name |
| 用户任务 | assignee、owner、candidateUsers、candidateGroups、formKey、dueDate、priority、category、skipExpression、默认流 |
| 服务任务 | 实现方式（class / delegateExpression / expression **三选一，选中即清空其余两项**）、取值、resultVariableName、默认流 |
| 脚本任务 | scriptFormat、resultVariable、autoStoreVariables、脚本正文 |
| 手工任务 / 开始 / 结束事件 | id、name |
| 排他网关 | 默认流（选项来自出流 id） |

### 15.7 P10 实施记录（对拍机器）

> 待补：本节的原文（比对器自测、语料体检、三分桶执行器、`--check` 漂移校验的完整表格）在覆盖事故中丢失。
> 可从代码还原：`tests/NetBpm.DifferentialTests/Differential/`（`DifferentialEngine` / `DifferentialRunner` /
> `DimensionComparers` / `RowOrdering` / `SnapshotComparer` / `TraceRecordingHistoryManager` / `DifferentialReport`）。

### 15.8 现成语料接入（P10 语料侧）

**来源与工具**：`tests/corpus/importer`（纯 C#，`--dry-run` 看统计、`--check` 查漂移）。
收录 `E:\DataCode\Activiti-9.0.0` 下全部 `*.bpmn20.xml` / `*.bpmn`，脚本自动生成为 40 步 `completeAnyTask`。

**收录口径（四道闸门，缺一不可）**：只含子集元素 → 能被本引擎解析 → 能过本引擎部署校验 → 至少一个用户任务。

**结果（2026-10-12，源码树 1288 个 BPMN）**

| 结果 | 数量 | 说明 |
|---|---:|---|
| **收录** | **199** | 178 纯顺序流 / 5 排他网关 / 16 并行网关 / **0 混合**（印证 §9.3 的判断：现成语料里没有"排他+并行"混合结构） |
| 剔除：服务任务 | 226 | 见下方"口径修正" |
| 剔除：子流程 / 事件定义 / 边界事件 / 调用活动 / 接收任务 / 中间事件 / 多实例 | 146 / 106 / 76 / 58 / 48 / 46 / 45 | 确属子集外 |
| 剔除：脚本任务 | 63 | 同上（本引擎尚未实现脚本执行） |
| 剔除：表达式依赖 Bean / POJO | 5 | 静态判定：`${fakeLdapService.findManagerForEmployee(emp)}`、`${userMap.assignee}` 这类 |
| 剔除：缺 id | 23 | 见"保留的严格项" |
| 剔除：排他网关全条件且无默认流 | 23 | 同上 |
| 剔除：解析失败 / 无用户任务 | 36 / 56 | 前者如抽象任务 `<task>`（子集外）；后者脚本无事可做 |

**⚠️ 对 §9.3「554 个现成语料可直接做对拍」的口径修正**

554 的口径是"**仅含子集元素**"，而"可直接做对拍"还多一条隐含要求：**两端对同一元素的行为必须可比**。
`serviceTask` / `scriptTask` 虽在 §1.2 元素表内，但对拍需要两端各有一份**语义相同**的委托与脚本实现 ——
否则比出来的是"实现差异"而不是"引擎语义差异"（服务任务 226 + 脚本任务 63，共 289 个）。
因此**实际可直接对拍的是 199 个**，不是 554。要让那 289 个进对拍，必须先定"相同空委托 / 相同脚本引擎"的口径（未做）。

**收紧与放宽的三处判定**

| 判定 | 处置 | 依据与代价 |
|---|---|---|
| 用户任务必须指定办理人 | **放宽**（规则 8 删掉这一条） | Activiti 允许无人办理的用户任务（任务照常创建），这是**我们的过度限制**；它当初的理由"无人办理查不出来"已被 `TaskQuery.processInstanceId` 消解。代价：不收就是 147 个语料被挡在门外。前端即时校验同步降为**告警** |
| 元素必须有 id、排他网关不能用"全条件且无默认流" | **保留**（各挡住 23 个语料） | 这两条是**有意的严格**：前者两端"缺 id 自动生成"的规则不同（会产生假差异），后者在 Activiti 是**运行期**抛异常而我们提前到部署期 |
| 表达式依赖 Bean / POJO | **导入期剔除**（5 个语料） | 这些在真实部署里靠 Spring 容器与业务对象提供，对拍脚手架不该伪造。**不能在运行期把它们当成"不适用"** —— 那会把真实的解析器缺陷一并掩盖掉 |

**接入过程抓到的两个真实缺陷（都已修）**

| # | 现象 | 根因与处置 |
|---|---|---|
| C1 | `from-activiti/parallel/ParallelGatewayTest.testNestedForkJoin` 抛 `ArgumentNullException: parent` | **嵌套 fork/join** 分支时 `TakeOutgoingSequenceFlowsOperation` 用 `execution.Parent` 建子执行，而数据库宿主从库里读回的执行只带 `PARENT_ID_`，`Parent` 对象为空（Activiti 的 `getParent()` 是**同步**懒加载，本移植版是异步仓储）。**内存宿主不会触发 ⇒ 两个宿主行为分叉**。处置：新增 `ResolveParentAsync`（已挂载 → 按 `PARENT_ID_` 读回 → 兜底自身）并回填 |
| C2 | 9 个语料抛 `ElNotSupportedException` | **EL 是第一短板**：表达式只支持最简形式。已按 §15.9 完整实现；剩余 4 个语料在导入期按上表剔除 |

**执行器改进（本轮）**

| 改进 | 原因 |
|---|---|
| 单例执行失败不再中止整批，转为报告条目 | 语料是资产，一个跑不动不能拖垮整批 |
| 三分桶：**跑通 / 跑不动（执行失败）/ 不适用** | 跑不动 ≠ 一致（门禁把"跑不动"算作失败）；"不适用"是脚本口径限制（同 key 多待办、名称超出 `varchar(255)` 列宽），必须**列出来**而不是静默丢弃 |
| 待办视图改按 `TaskQuery.processInstanceId` 取全部待办 | 比"逐个办理人查"更完整，两端同一条 API 语义 |
| 失败时抛出的异常带语料名 | 200+ 个语料里找一个失败者不能靠猜 |

**当前语料盘面（212 = 199 导入 + 13 自建）**

| 分类 | 数量 |
|---|---:|
| 跑通且两次结果一致 | **210** |
| 跑不动（执行失败） | **0** |
| 不适用（脚本口径 / 列宽） | 2 |

### 15.9 表达式语言完整实现（P3）

**模块结构（`src/NetBpm.El`，对照 `activiti-juel-jakarta` 逐条照搬）**

| 文件 | 对应 JUEL | 职责 |
|---|---|---|
| `Scanner.cs` / `ElParser.cs` | `tree.impl.Scanner` / `Parser` | 双模式扫描（`\$\#` 转义、只允许 `\\` 与转义引号）、递归下降，**优先级表逐条照搬** |
| `Ast/ElNode.cs` | `AstText` / `AstComposite` / `AstNested` | 纯文本、复合拼接、括号 |
| `Ast/ElPropertyNodes.cs` | `AstIdentifier` / `AstProperty` / `AstDot` / `AstBracket` / `AstMethod` / `AstParameters` | 标识符、属性、索引、方法调用 |
| `Ast/ElOperatorNodes.cs` | `AstBinary` / `AstUnary` / `AstConditional` | 运算符（逻辑短路）、一元、三目 |
| `TypeConverter.cs` / `BooleanOperations.cs` / `NumberOperations.cs` | `misc/TypeConverterImpl` / `BooleanOperations` / `NumberOperations` | 隐式转换、相等比较、算术 |
| `JavaFormat.cs` | （Java 内建 `toString`） | Java 形态的 `Double/Float/Boolean/集合/Map/Date` 字符串 |
| `ElResolver.cs` | `VariableScopeElResolver` + `Map/List/Array/BeanELResolver` | 标识符 → 属性 → 索引 → 方法调用 |

**必须照搬、且与直觉相反的语义（照搬错了会直接走错分支）**

| # | 事实 |
|---|---|
| E1 | **`+` 从不拼接字符串**：`"1" + "2"` 是 **3**，`"a" + 1` **抛异常**（`NumberOperations.add` 的 Long 分支） |
| E2 | **`/` 恒为浮点除法**：`5 / 2` 是 **2.5**，除零得 `Infinity`/`NaN` **不抛**；只有 BigDecimal/BigInteger 参与才走 HALF_UP 定点除 |
| E3 | **`"abc" == 1` 抛异常而不是 false**：相等判定的整数分支排在字符串分支**之前**，按 `Long.valueOf` 解析失败即报错 |
| E4 | **比较运算遇 null 一律 false**（`null < 1`、`null > 1`、`null <= 1` 全 false，不抛异常）；只有两端都非 null 且不可比才抛 |
| E5 | **数值提升顺序**（比较与算术一致）：BigDecimal → Float/Double → BigInteger → Byte/Short/Integer/Long → String → Comparable；`isDotEe` 判定**大小写敏感**：`"kermit"` 含小写 `e` ⇒ 走浮点分支 |
| E6 | **`toBoolean`**：`null`/`""` → false；`Boolean` → 自身；`String` → 仅忽略大小写等于 `"true"`；**其它类型一律抛异常** |
| E7 | **`toString`**：`null` → `""`；`Double(1.0)` → `"1.0"`；`Double(1e7)` → `"1.0E7"`；集合 → `[a, b]`、Map → `{k=v}`（集合内 `null` 元素打印 `null` 而非空串） |
| E8 | **`&&`/`||` 短路**，三目两分支**惰性**且**右结合** |
| E9 | **解析失败 vs 静默 null 的边界**：顶层标识符解析不出、`a.b` 属性解析不出 → **响亮抛**；基为 null、Map 缺键、List/数组越界、`m[null]` → **静默 null** |
| E10 | `${}` 与 `#{}` 求值语义相同但**不可混用**（解析期报错）；字符串转义只允许 `\\` 与转义引号，`"\n"` 是**词法错误** |
| E11 | 整串**先 trim 再解析**（Activiti 的 `createExpression` 行为），因此 `"Hello ${name} "` 的尾随空格会丢失 |

**有意偏离（§15 登记的 C# 侧限制）**

| # | 项 | 说明 |
|---|---|---|
| E12 | 属性名不区分大小写 | Java 用 `getProcessInstanceId()`（小写开头）、.NET 用 `ProcessInstanceId`（PascalCase）；照抄 Java 的区分大小写会让 `${execution.processInstanceId}` 全部解析失败。先精确匹配、再忽略大小写 |
| E13 | 方法调用垫片 | Java 的 `length()`/`size()`/`contains()`/`substring(a,b)` 在 .NET 上没有同名成员，对 `String` 与 `Collection`/`Map` 做了显式垫片；`IVariableScope` 的 `getVariable` 一族方法是异步的，反射调不动，也垫了一层 |
| E14 | 返回 `Task` 的异步方法不参与匹配 | 求值器走同步反射 |
| E15 | `DateTime` 的 `toString` | 无法复刻 Java 的时区缩写（Java 输出 `CST`，本实现输出 `GMT+08:00`）；只影响"把日期拼进字符串"，不影响条件判定 |
| E16 | 不支持 | EL 命名空间函数 `${fn:length(x)}`、裸函数 `${myFunc(x)}`、`instanceof` 运算符 —— 解析期**响亮失败**，不静默当变量 |
| E17 | `decimal` 的除法 | Java `BigDecimal.divide` 的"结果 scale = 被除数 scale"用 C# `decimal` + `decimal.GetBits` 取 scale 近似（28 位精度） |
| E18 | `decimal` 的 `%` | JUEL 的 `mod` 不把 BigDecimal 单独分组（走 double），照搬 |

**验收**：`tests/NetBpm.UnitTests/El/ExpressionEvaluationTests` 共 **223 条**用例（193 条求值 + 30 条响亮失败），
每条期望值都注明取自 JUEL 源码的哪个分支；含 3 个专门钉死"反直觉语义"的用例组。

**语料侧的配套（否则 EL 修好了语料还是跑不动）**

| 机制 | 说明 |
|---|---|
| 变量播种 | Activiti 的测试在启动前会把变量塞进流程（`startProcessInstanceByKey(key, vars)`），而自动脚本不会替它们编数据。导入器从条件与任务属性表达式里**静态提取顶层标识符**，按确定值播种（普通值 `"1"`，`dueDate` 表达式给 ISO-8601 串）。少了它，`${input == 1}` 会按 JUEL 语义抛"无法解析标识符"——那是**正确**行为，不是缺陷 |
| 播种值为什么是字符串 | 字符串既可被当数字强转（`"1" > 100` 不报错），又不会让 `==` 落到布尔分支抛异常 |
| 不漏属性 | 播种必须覆盖 assignee/owner/formKey/dueDate/priority/category/skipExpression/candidate* 全部可表达式属性 |

### 15.10 Java 预言机首次跑批（P10 真值来源打通）

**日期**：2026-10-12。**结果**：212 个语料中 **204 个产出真值**，三维度一致率
① 99.68% / ② 96.13% / ③ 83.06% / **整体 92.89%**（门禁 99.5%，未达线；888 条差异全部列在
`artifacts/differential/report.md`，待逐条结论）。

**A. 让预言机跑起来攻克的五道关卡**

| # | 关卡 | 处置 |
|---|---|---|
| O1 | Activiti 9.0.0 不在任何仓库；本地源码构建要求 **JDK 25**，本机是 JDK 21 | 源码里 4 个文件共 38 处 `IO.println`（JEP 512，JDK 25 才有 `java.lang.IO`）**全部是纯日志** ⇒ 本地替换为 `System.out.println` 后以 JDK 21 编译安装。已确认无其它 24/25-only API |
| O2 | `activiti-engine` 的 `resource-dependencies` 要解包 `activiti-engine:7.2.0`（取 DDL），仓库里没有 | 引擎自带同一套 `org/activiti/db/create/**` ⇒ 用 9.0.0 的 DDL 打成桩 jar 装进本地仓库 |
| O3 | 引擎要 9 张我们按 §1.3 裁掉的表（`ACT_RU_TIMER_JOB` 等） | `tools/Oracle/sql/missing-tables.mysql.sql`：从 Activiti 自带 DDL **原样抽取**（只改 `if not exists`）。维度②只导固定 12 张表，这些表恒空，不影响快照 |
| O4 | Activiti 9 的 `mysql.properties` 写死 `ROW_NUMBER() OVER (...)`（假定 MySQL 8），本机 5.7 没有窗口函数；且 `HistoricProcessInstance.xml` 把 `ROW_NUMBER` **硬编码**在映射里 | 两处都绕开：① 在预言机自己的 resources 里放同名 `org/activiti/db/properties/mysql.properties`（classpath 上排在 jar 前 ⇒ 覆盖引擎那份，只改分页 SQL，不动引擎语义）；② "实例是否结束"改用**运行时**查询判定（语义等价），绕开硬编码那段 |
| O5 | 事件监听器注册、动作语义、清表口径 | ① `setEventListeners` 必须在 `buildProcessEngine()` **之前**（字段默认 null）；② `completeAllTasks` 不得要求待办唯一（与 C# 同规则）；③ **每个语料开跑前清表**（与 C# 的 `Reset` 同清单、同顺序）—— 不清则维度②快照会累积前面语料的行；④ 单例失败不中止整批 + 成功/失败汇总 |

**B. 真值比对逼出来的口径修正（两侧各改一遍，不是登记台账）**

| # | 现象 | 根因与处置 |
|---|---|---|
| D1 | 维度②一致率只有 52% | **ID 具体值进了比对与排序键**。§9.4 原文本就允许"ID 具体值（若区块分配时序不同）"这一类差异（Activiti 用 UUID、我们用计数器）。两侧忽略列补齐 ID 族 + `DURATION_` 后 ⇒ 52% → 94% |
| D2 | 维度③一致率只有 27% | ① **`SEQUENCEFLOW_TAKEN` 整段缺失**：`ContinueProcessOperation` 走顺序流时根本没有历史调用。新增 `IHistoryManager.RecordSequenceFlowTakenAsync` 端口并在**改写 `CurrentFlowElement` 之前**调用（端口靠"当前元素是顺序流"取流 id）；② **变量事件应早于 `PROCESS_STARTED`**；③ 实例收口时为已无当前活动的执行又发了一条 `ACTIVITY_COMPLETED`（采集器去重 + 空活动不记录）；④ `PROCESS_*`/`TASK_*` 事件不填 `activityId`（Activiti 的这些事件类型不带该字段）⇒ 27% → 83% |
| D3 | `ACT_GE_BYTEARRAY.BYTES_` 204 处差异 | Activiti 存**原始 XML 字节**，我们的部署接口拿到的是已解析的模型，存的是重新序列化的 XML（内容保真但非字节一致）⇒ 登记为**有意偏离**并进忽略列。彻底修法：让部署接口接收原始字节（未做） |

**C. 第二轮修复（同日）：888 条 → 81 条，门禁达标**

第一轮跑批后的 888 条差异，按根因修复（**全部回写代码**，不是台账消化）：

| # | 差异 | 根因与处置 |
|---|---|---|
| R1 | 历史任务行 `ASSIGNEE_` 为空（期望 fozzie/user1/kermit） | **不是**"创建时没写办理人"，而是这些语料用 **BPMN 标准的 `<humanPerformer>`** 分配办理人（不是 `activiti:assignee`），我们的转换器不认识 ⇒ 实现 `humanPerformer`（assignee）与 `potentialOwner`（`user(x)`/`group(y)`/裸名 ⇒ 候选）解析 |
| R2 | 身份关系行乱序（TYPE_/GROUP_ID_ 错位） | **规范排序键的 null 表达不一致**：Java 的 JSON 省略 null 字段（键里没有该列），C# 拼成 `COL=`。两侧统一为"null ⇒ 不参与键"（`RowOrdering.CanonicalKey`） |
| R3 | `ACT_HI_VARINST.REV_` 期望 0、实际 1 | 历史行的 REV_ 是 Activiti `copyAndInsert` 复制的**运行时内存值**（创建时为 0，DB 的 1 是 INSERT 写死的），且更新时不碰 ⇒ 历史行 REV_ 恒 0（改 INSERT 为 0、UPDATE 去掉 REV_） |
| R4 | 部署表 `KEY_`/`CATEGORY_`（394 处） | 我们画蛇添足：Activiti 的部署默认**没有** key/category ⇒ 都写 null |
| R5 | 并行 Join 的事件顺序 | **"收口历史行"与"离开事件"被绑在一个端口上**。Activiti 里二者只在少数点成对（TakeOutgoing.handleActivityEnd、排他网关行为内 leave），并行网关每次到达、执行删除清理**只收口不发事件** ⇒ 新增 `RecordActivityEndQuietlyAsync`，`RecordActivityEndAsync` 收敛为"离开"语义（唯一调用点：节点路径） |
| R6 | 比对器**列并集** | 原实现只遍历预言机行的列 ⇒ 被测侧独有的列（`START_USER_ID_`）被静默跳过；改为两侧列并集（absent ≡ null） |
| R7 | `${initiator}`（7 个语料跑不动） | 实现 `activiti:initiator`（启动时把认证用户写进该变量）+ 隐式对象 `initiator`（`IVariableScope.StartUserId` ⇒ `ProcessInitiatorELResolver`）；对拍两侧统一为**匿名启动**（预言机无认证用户 ⇒ START_USER_ID_ 为 null） |
| R8 | 维度②单元格翻倍 | R6 的连带：被测侧的 null 列开始参与比对 ⇒ 一致率的分母更诚实 |

**修复后（2026-10-12 第二轮）**：① 99.68% / ② 99.51% / ③ 99.97% / **整体 99.59% —— §9.4 门禁达标**；
差异 81 条**全部登记进台账**（`tests/corpus/known-issues.json`），待结论 0 条。

**D. 复现方式（一条命令链）**

```powershell
# 0) 一次性：补 Activiti 需要的 9 张表（对 netbpm 与 netbpm_diff 各做一次）
& "C:\Program Files\mysql-5.7.44-winx64\bin\mysql.exe" -uroot netbpm_diff -e "source tools/Oracle/sql/missing-tables.mysql.sql"

# 1) 生成真值（每个语料旁写 <stem>.oracle.json；单例失败不中止整批）
mvn -f tools\Oracle\pom.xml compile exec:java "-Dexec.args=--corpus tests/corpus --connection jdbc:mysql://127.0.0.1:3306/netbpm_diff?useUnicode=true&characterEncoding=utf8 --user root"

# 2) 对拍（读真值，输出 artifacts/differential/report.md|json）
dotnet test tests\NetBpm.DifferentialTests\NetBpm.DifferentialTests.csproj
```

> ⚠️ 预言机跑在 `netbpm_diff`（与 C# 侧同一库）；它**自己会清表**（每个语料前），不再需要手工 purge。
> ⚠️ 8 个语料在 Java 侧失败且被 C# 侧同样判为不适用/子集外：同 key 多待办、名称超 `varchar(255)`、
> XSD 校验更严（3 个：Activiti 会拒绝我们能解析的 XML）、Spring Bean 表达式（2 个）、业务日历（1 个）。
