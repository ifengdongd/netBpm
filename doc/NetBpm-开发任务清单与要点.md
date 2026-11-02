# NetBpm 开发任务清单与要点

> **分析对象**：`E:\DataCode\NetBpm`（tag `V0.1.0`，41 次提交，2026-10-12 ~ 2026-10-15）
> **分析方式**：源码逐项目盘点（文件数 / 行数 / 类清单 / 测试数）+ 与《NetBpm-子集流程引擎-开发计划》§1–§15 交叉核对
> **用途**：① 交付清算与验收基线 ② 二次开发排期与交接 ③ 同类"照搬语义型"引擎的复用参考

---

## 0. 一页结论

### 0.1 做完了什么

| 计划阶段 | 内容 | 状态 |
|---|---|---|
| P0 工程底座 | SLNX 骨架 / CPM / 目录级构建属性 / 分析器红线 / 打包脚本 | ✅ |
| P1 BPMN 模型层 | 11 文件 / 573 行 模型对象 | ✅ |
| P2 BPMN 转换层 | 4 文件 / 1,293 行，XML ⇄ 模型 + DI + 反向导出 | ✅ |
| P3 表达式语言 | 16 文件 / 2,358 行，JUEL 子集，223 条用例 | ✅ |
| P4 校验层 | 5 文件 / 559 行，**9 条规则** | ✅ |
| P5 持久化层 | 20 文件 / 3,771 行，**13 张 `ACT_*` 表** × 2 方言 | ✅ |
| **P6 引擎运行时** | 61 文件 / 4,583 行，Agenda + 4 Operation + 9 行为 + 6 命令 | ✅ |
| P7 任务与身份 | 用户任务全生命周期 + 身份关系 | ✅ |
| P8 设计器前端 | bpmn-js + moddle 扩展 + 属性面板 + 即时校验 | ✅ |
| P9 REST 宿主 | 13 个端点 + OpenAPI | ✅ |
| **P10 对拍验证** | Java 预言机 + 三维度比对，**整体一致率 99.92%**（门禁 99.5%，差异 100% 有结论。维度① API 与维度③ 事件已 100% 一致，剩余 16 条集中在维度②的 `REV_` 与定义级候选发起人） | ✅ |
| P11 硬化与文档 | 打包分发（nupkg × 9 + 离线 zip）、README、设计指引、崩溃恢复演练 | ✅（2026-11-02：`tools/pack.ps1`、`docs/operations-runbook.md` §13（崩溃恢复演练）、`tools/drills/crash-recovery.ps1`、`deploy/observability/` 均已交付） |
| — 计划外新增 | **`NetBpm.Workflow` 应用层 + 合并 Demo**（审批流公共能力） | ✅ |

### 0.2 三个"贵资产"（砍范围换来的红利，别再丢掉）

1. **沿用 `ACT_*` 表名与列名** → 库级行快照对拍能力。这是全项目最强的验证手段：能抓到 API 层看不出的执行树内部差异（标志位错、子执行多建/少建、Join 该删的没删）。
2. **Java 预言机就是免费的权威 Oracle** → "判定正确的唯一客观标准 = 与 Activiti 输出一致"从口号变成可执行的 CI 门禁。
3. **`NetBpm.Workflow` 公共类** → 从"引擎"升维到"审批流产品"：宿主五步接入即获得发起 / 待办 / 审批 / 历史 / 流程图 / 改派 / 跳转全部能力，业务侧只实现 `IBusinessProcess`。

### 0.3 五个已踩过并固化的坑（最值钱的部分）

| # | 坑 | 一句话结论 |
|---|---|---|
| 1 | **内存宿主与真库宿主行为分叉** | 嵌套 fork/join 时 `execution.Parent` 在真库宿主为 null（只带 `PARENT_ID_`）→ 必须走异步 `ResolveParentAsync`；**内存模式跑通 ≠ 正确** |
| 2 | **宿主"启动时读配置"不可用测试覆盖** | `ConfigureAppConfiguration` 对注册阶段已读的配置无效 → 集成测试必须用 `UseSetting`，且必须断言"库里真的有了行" |
| 3 | **EL 语义与直觉大面积相反** | `"1"+"2"==3`、`5/2==2.5`、`"abc"==1 抛异常`、`null<1 恒 false` —— 照搬错了会直接走错分支 |
| 4 | **Agenda 不能写成递归** | 递归在并行 Join 场景下受"谁先到达"时序影响产生错误结果；必须队列 + 单命令内排空 |
| 5 | **同一条历史语义被绑在两个端口上** | "收口历史行"与"离开事件"必须拆成两个 API（`RecordActivityEndAsync` / `RecordActivityEndQuietlyAsync`），否则并行 Join 事件序列全错 |

### 0.4 还剩什么

- **必须补**（业务高频）：~~脚本任务执行（Roslyn）~~（✅ 已完成：`scriptFormat="csharp"`，Roslyn C# Scripting，部署期校验 + 单测钉死语义）、~~多实例会签~~（✅ 已完成，见 B1.2）。
- **可以不做**（有意出界）：子流程 / 调用活动、消息与错误事件、事件网关、泳道、指令式批量 modify、表单引擎渲染、图形 PNG 生成、其余 6 方言。
  > 口径校正（2026-11-02）：**定时边界事件与定时作业已实现**（`BoundaryEventRules` + `TriggerTimerJobCommand` + `DapperTimerJobManager`），不再属于「不做」；仍在拒绝名单里的是**消息 / 信号 / 错误**类事件与非 timer 边界事件。
- **工程硬化欠账**：~~并发压测数据、崩溃恢复演练、可观测性（指标/追踪）、CI 流水线落地~~
  ✅ 2026-11-02 已全部补齐（压测数据见 `docs/load-tests/results/`；崩溃恢复演练见
  `docs/operations-runbook.md` §13 与 `tools/drills/crash-recovery.ps1`；可观测性见
  `docs/operations-runbook.md` + `deploy/observability/`；CI 见 `.github/workflows/ci.yml`）。
- **补充完成（2026-10-16）**：超长字符串变量（longString，> 4000 字符降级 UTF-8 字节入 `ACT_GE_BYTEARRAY`，含删除级联与历史行引用）；「按实例 + 候选人」组合任务查询（两个已有查询取交集，候选人语义与单独查询同口径）。

---

## 1. 现状盘点（实测数据）

### 1.1 引擎与宿主源码（10 个项目 / 163 个 `.cs` / 约 18,700 行）

| 模块 | 文件 | 行数 | 关键内容 | 对应阶段 |
|---|---:|---:|---|---|
| `NetBpm.Bpmn.Model` | 11 | 573 | 元素树、网关族、扩展属性、DI 图形 | P1 |
| `NetBpm.Bpmn.Converter` | 4 | 1,293 | 流式读取 / 元素转换器族 / 反向导出 | P2 |
| `NetBpm.Bpmn.Validation` | 5 | 559 | 规则引擎 + 9 条红线 + 子集外显式拒绝 | P4 |
| `NetBpm.El` | 16 | 2,358 | 扫描器 / 递归下降 / AST / 求值 / Java 形态格式化 | P3 |
| `NetBpm.Engine` | 61 | 4,583 | Agenda、4 Operation、9 行为、6 命令、执行树、历史 | **P6** |
| `NetBpm.Persistence` | 20 | 3,771 | Dapper 仓储族、DbIdGenerator、迁移、清库 | P5 |
| `NetBpm.Identity` | 2 | 78 | 用户/组/候选关系端口（可替换） | P7 |
| `NetBpm.Runtime` | 11 | 877 | 组合根、DI 扩展、委托注册与解析、`IProcessEngine` | P9 |
| `NetBpm.Rest` | 6 | 662 | 13 个端点 + OpenAPI + 异常映射 + 变量边界还原 | P9 |
| `NetBpm.Workflow` | 27 | 3,967 | 审批流应用层 + Hosting 公共类 + 自检 Guard | 计划外 |
| **合计** | **163** | **≈18,721** | 对照 Java 基线约 11% 体量 | |

### 1.2 测试与语料

| 工程 | 文件 | 行数 | 用例方法 | 说明 |
|---|---:|---:|---:|---|
| `NetBpm.UnitTests` | 16 | 3,229 | 109 | 转换器 / 校验 / EL（223 条展开用例）/ 行为 / 调度 / 应用层 |
| `NetBpm.IntegrationTests` | 11 | 1,330 | 19 | 真实 MySQL：表结构、ID 区块、行级断言 |
| `NetBpm.DifferentialTests` | 13 | 2,226 | 27 | 三分桶执行器 + 三维度比对器 + 报告 |
| `tests/corpus` | 210 个 BPMN | — | — | 199 导入 + 自建，语料必须脚本生成 |

### 1.3 关键实现清单（可核对）

- **命令**：`StartProcessInstance` / `CompleteTask` / `ClaimTask` / `SetAssignee` / `MoveActivity` / `NoOp` + 3 拦截器（Log / Transaction / CommandContext）
- **操作**：`ContinueProcess` / `TakeOutgoingSequenceFlows` / `EndExecution` / `TriggerExecution`
- **行为**：空开始 / 空结束 / 排他网关 / 并行网关 / 包含网关 / 用户任务 / 服务任务 / 手工任务 / 脚本任务（占位）
- **校验规则 9 条**：流程 id 必填、恰好一个开始事件、入出流完整性、id 全局唯一、排他网关默认流、并行网关默认流禁用、事件网关显式拒绝、监听器配置、子集外能力显式拒绝
- **数据库**：MySQL / SQL Server 各 `0001`–`0005`；引擎表恰好 **13 张**
- **REST 端点 13 个** + `/health`，由 OpenAPI 存在性测试锁死
- **Demo**：单宿主承载 `order_process` + `leave_process`，七个页面

---

## 2. 开发任务清单（WBS）

> 编号规则：`WPn` 工作包 / `Tn.m` 任务。每个任务给出**交付物**与**要点**（要点 = 真正会卡住人的地方）。
> 「★」标记关键路径上的难点工作包。

---

### WP1 工程底座（P0）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T1.1 | 解决方案骨架 | `NetBpm.slnx` + 10 src / 4 tests / 1 sample 分层 | 用 `.slnx`（新格式）；测试工程**排除** `TreatWarningsAsErrors`，否则语料/临时断言写不下去 |
| T1.2 | 集中包与构建设置 | `Directory.Build.props` / `Directory.Packages.props` / `global.json` | `net10.0` + C# 14 + `Nullable`/`ImplicitUsings` 全开；SDK 用 `global.json` 锁死，避免机器漂移 |
| T1.3 | 分析器红线 | `BannedApiAnalyzers` + `BannedSymbols.txt` | 把 `[ThreadStatic]` 变成**编译期错误** —— 异步续体间线程静态状态会丢或串号，这是 P6 的隐形杀手 |
| T1.4 | 异常与约定 | `EngineException` / `EngineNotImplementedException(capability, planPhase, reason)` | 「未实现」必须**响亮失败**并把计划阶段写进消息，杜绝"看着能用其实没做" |
| T1.5 | 打包分发 | `tools/pack.ps1` → 9 个 nupkg + 离线 zip | 接入入口只暴露 `NetBpm.Workflow`，依赖链自动带全；`Persistence` 包内含两方言脚本（`Content` 必须改 `None`，否则 NU5118） |

### WP2 BPMN 模型层（P1）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T2.1 | 元素树基类 | `BaseElement` / `FlowElement` / `FlowNode` / `Activity` / `Process` / `BpmnModel` | 类名与属性名**逐条对齐 Activiti**（便于比对与查资料）；`CurrentFlowElement` 必须允许是 `SequenceFlow` 或 `FlowNode` |
| T2.2 | 网关族 | `Gateway` / 排他 / 并行 / 包含 / 事件（声明为不可用） | 不可用元素也要有模型类和友好错误，不能让它变成"解析到了但没人认识" |
| T2.3 | 扩展属性载体 | `FormProperty` / `EventListener` / 监听器契约 | `activiti:formProperty` 引擎**只透传不执行**，但模型必须保真以支撑设计器往返 |
| T2.4 | 图形信息 | `GraphicInfo`、`BPMNShape`/`BPMNEdge`/`Bounds`/`waypoint` | DI 可选（无也能部署），但**有则不可丢** —— 设计器往返与流程图着色都依赖它 |

### WP3 BPMN 转换层（P2）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T3.1 | 流式读取骨架 | `XmlReader` 驱动的主循环 + `BaseBpmnXmlConverter` | **必须流式**，不整树加载（大流程文件关键）；禁止 DTD |
| T3.2 | 元素转换器族 | 每元素一个 Converter（开始/结束/顺序流/三网关/用户/服务/手工/脚本） | 一个元素一个 converter 的分层照搬 Activiti，别合并成"万能 if-else" |
| T3.3 | 扩展命名空间解析 | `http://activiti.org/bpmn` 下全部属性 + `humanPerformer`/`potentialOwner` | **BPMN 标准的 `<humanPerformer>` 才是很多真实语料的办理人来源**，只认 `activiti:assignee` 会漏一大片（对拍第一轮就栽在这） |
| T3.4 | 反向导出 | 模型 → XML（含 DI 回填 `targetNamespace`） | 导出与解析必须构成**往返保真**；`isExecutable` 恒写真 |
| T3.5 | 往返与语料回归 | `BpmnXmlConverterTests` + `DesignerRoundTripTests` + 199 语料解析 | 验收口径：XML → 模型 → XML **语义零丢失**（DI 坐标、扩展属性、默认流） |

### WP4 表达式语言（P3）★

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T4.1 | 扫描器 + 递归下降 | `Scanner.cs` / `ElParser.cs` | 双模式（`${}` 与 `#{}` **不可混用**）；字符串转义**只允许** `\\` 与转义引号 —— `"\n"` 是词法错误 |
| T4.2 | AST 与求值 | `Ast/*` + `ExpressionManager` | 逻辑短路、三目**惰性且右结合**；整串**先 `trim` 再解析**（尾随空格会丢） |
| T4.3 | 类型转换与比较 | `TypeConverter` / `BooleanOperations` / `NumberOperations` | 数值提升顺序照搬：BigDecimal → Float/Double → BigInteger → 整数 → String → Comparable；遇 null 的比较**一律 false** 不抛 |
| T4.4 | 解析器族 | `ElResolver`（变量作用域 / Map / List / Array / Bean） | 属性名 .NET 与 Java 大小写不同 → **先精确匹配再忽略大小写**；`length()/size()/contains()/substring(a,b)` 必须做垫片 |
| T4.5 | JUEL 对拍用例 | 223 条（193 求值 + 30 响亮失败） | 每条期望值注明取自 JUEL 哪个分支，尤其把**反直觉语义**单独成组钉死 |

> **T4 的三条硬红线（写错必走错分支）**
> 1. `+` 从不拼字符串：`"1" + "2"` = **3**，`"a" + 1` **抛异常**。
> 2. `/` 恒为浮点除：`5 / 2` = **2.5**，除零得 `Infinity`/`NaN` **不抛**。
> 3. `"abc" == 1` **抛异常**而不是 false（整数分支排在字符串分支之前）。

### WP5 校验层（P4）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T5.1 | 规则引擎骨架 | `IProcessValidationRule` + `ProcessValidator` + `ValidationError` | 错误必须能定位到**元素 id**，否则设计器没法画红框 |
| T5.2 | 九条红线 | `ProcessValidationRules` | 排他网关"全条件且无默认流"**保留为部署期硬拒**（Activiti 是运行期才抛，提前到部署期是有意严格）；无人办理的用户任务**放宽**为告警 |
| T5.3 | 子集外显式拒绝 | 规则 9 + `EngineNotImplementedException` | 子集外元素**宁可拒部署也不静默忽略**；仅 `extensionElements`/`documentation`/`textAnnotation`/`association` 容忍 |

### WP6 持久化层（P5）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T6.1 | 双方言 DDL | `db/{mysql,sqlserver}/netbpm_init.sql`（单一数据源 `SchemaCatalog` 生成） | 引擎表**恰好 13 张** `ACT_*`；脚本不得出现被裁掉的 12 张表名（有测试守着）；**全幂等**（建表 / 索引 / 外键 / 注释都带方言守卫），可重复执行；表注释：MySQL 内联、SQL Server 用 `sp_addextendedproperty`（判重后再加） |
| T6.2 | ID 区块生成器 | `DbIdGenerator` | 从 `ACT_GE_PROPERTY.next.dbid` **一次占 2500**、`REV_` 乐观锁更新、进程内自增发放；改成 UUID/自增会**让库级对拍全线失败** |
| T6.3 | 仓储族 | `DapperExecutionManager` / `DapperTaskManager` / `DapperVariableInstanceManager` / `DapperHistoryManager` / `DapperProcessDefinitionStore` | **Dapper 不做下划线→驼峰映射** → 所有手工 SQL 显式 `as` 别名；保留字要反引号；别用 `select *` |
| T6.4 | 变量类型映射 | `VariableTypeMapper` | 列布局：`string`→`TEXT_`；整数→`LONG_` **和** `TEXT_`；`double`→只 `DOUBLE_`；`bool`→只 `LONG_`；`date`→`LONG_`（UTC 毫秒）；`null`→`TYPE_='null'` 且值列全空。`TEXT2_` **不是**截断列，是 json 类型类全名辅助列。超 4000 字符串 / bytes / serializable **响亮失败，绝不截断** |
| T6.5 | 会话与事务 | `DbSession` / `IDbSession` / `TransactionInterceptor` | 一个命令 = 一个事务；历史行**同事务内即时写**（与 Activiti 的一级缓存 flush 等效，提交后可见结果一致） |
| T6.6 | 迁移与校验工具 | `NetBpm.DbMigrate`（`migrate` / `check` / `probe` / `purge`）+ `DatabaseMigrator` / `EngineTableCleaner` | **无增量迁移记录表**（脚本曾短暂用过 `ACT_GE_SCHEMA_LOG` + 校验和，已由「单文件全幂等」取代）：每次 `migrate` 都完整执行 `netbpm_init.sql`，靠脚本内的方言守卫收敛 ⇒ 新库一次建成、老库只补缺失；初值只在缺失时插入，`next.dbid` 不会被重置。清库顺序：身份关系**先于**任务（外键约束） |

### WP7 引擎运行时（P6）★★ 全部难点所在

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T7.1 | 执行树实体 | `ExecutionEntity` / `IExecutionEntity` / `IDelegateExecution` | **根执行**：`IS_SCOPE_=1`、`PARENT_ID_=null`、`ACT_ID_` 恒 null、**不参与推进**；另有一个子执行走完全流程。把根执行放进推进路径会让并行 Join 的"删除其余非活跃执行"**误删流程实例根执行** |
| T7.2 | 三标志位 | `IsActive` / `IsConcurrent` / `IsScope` | `IS_CONCURRENT_` 在 Activiti 9 里**永远不会被置 1**（无调用点）—— 必须按"恒 false"实现，否则该列系统性差异。写"标志位不变量"断言（`IsConcurrent ⇒ ParentId != null`） |
| T7.3 | Agenda 与 4 Operation | `EngineAgenda` + `ContinueProcess` / `TakeOutgoingSequenceFlows` / `EndExecution` / `TriggerExecution` | **绝不写成递归**；一个命令一个事务、事务内**排空** Agenda；循环里不 `await` 外部 IO。分支产生：**第一条流出流复用当前执行**，其余每条新建子执行，全部建完才统一推进。结束判定 = `activeChildren == 0` |
| T7.4 | 网关行为 | 排他 / 并行 / 包含 | 排他：取**第一个**条件为真且非 default 的流，**不加**"多真报错"。并行：先 `Inactivate()` → 找同活动 id 的非活动执行 → 判定**总数相等**（不是"每条流入过"）→ 出口 `evaluateConditions: false`。包含：条件分叉 + 按入流数同步 |
| T7.5 | 任务行为 | 用户任务全生命周期 / 服务任务 / 手工任务 / 脚本任务占位 | 用户任务触发前置校验：执行上若还有未删任务 → 抛 `UserTask should not be signalled before complete`（**错误文案也参与对拍**）。候选值切分按 `[\s]*,[\s]*`，不是 `Split(',')+Trim` |
| T7.6 | 变量作用域 | `VariableInstance` / `IVariableScope` | `setVariable(x,v)` 落在**最上层变量作用域**（本子集 = 流程实例根执行），否则并行兄弟分支互相看不见对方的变量；作用域首次访问**一次查全**再沿父上溯 |
| T7.7 | 命令 + 拦截器链 | 6 命令 + Log / Transaction / CommandContext 拦截器 | `CommandContext` 用 `AsyncLocal` 而非 `ThreadLocal`；跨命令恢复等待型节点只带 `ACT_ID_` 字符串 ⇒ 必须靠 `IProcessDefinitionResolver` 端口回解析元素 |
| T7.8 | 事件分发与监听器 | `EngineEventDispatcher` + `ListenerInvoker` + 执行/任务/事件监听器 | 三选一实现（`class` / `delegateExpression` / `expression`）；`throwOnFailure=true` 才让监听器抛错失败，缺省只记警告；**流程级执行监听器 = 全局观测**，任何节点的 start/end 与任何流的 take 都通知 |
| T7.9 | 历史管理器 | `IHistoryManager` + 5 张历史表写入 | 见下方「历史语义 8 条」 |

> **历史语义 8 条（对拍重灾区）**
> 1. 历史行 id = 运行行 id，**只有 `ACT_HI_ACTINST.ID_` 是新生成的**。
> 2. `ACT_TYPE_` 取元素**类名首字母小写**，不是 `getProperty("type")`。
> 3. **顺序流不产生活动行** —— 执行到流时收口的是**源节点**那一行。
> 4. 历史身份关系**只增不删**：任务完成/实例结束后 `ACT_HI_IDENTITYLINK` 行保留。
> 5. 历史变量行 `REV_` **恒 0**（DB 里的 1 是 INSERT 写死的；更新时 `REV_` 不动）。
> 6. 任务落库后回填 `ACT_HI_ACTINST.TASK_ID_`；变更办理人后回填 `ASSIGNEE_`，都发生在建行之后。
> 7. "收口历史行"与"离开事件"必须拆成两个端口，并行网关每次到达**只收口不发事件**。
> 8. 变量事件必须**早于** `PROCESS_STARTED`；`PROCESS_*`/`TASK_*` 事件不填 `activityId`。

### WP8 任务与身份（P7）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T8.1 | 任务实体与查询 | `TaskEntity` / `ITaskManager` / `TaskQuery` | 任务行**不做脏检查**（即时写）⇒ "只改了 `CLAIM_TIME_`"必须显式 `UpdateTaskAsync` |
| T8.2 | 身份关系 | `IdentityLink`（人/组）、claim / 取消 claim | 候选组展开需要 `IUserGroupManager`，**只有应用层做得了**，别塞进引擎 |
| T8.3 | 身份端口可替换 | `EmptyUserGroupManager` / `IOrgDirectory` / `InMemoryOrgDirectory` | 端口设计要能被真库实现 —— **2026-10-20 已兑现**：Demo 的 `MySqlOrgDirectory` 读 `sys_dept` / `sys_emp` / `sys_emp_group` / `sys_emp_group_member` 四张真表（随 `AddDemoStores` 按方言切换；SqlServer / JSON 模式仍用内存版） |

### WP9 设计器前端（P8）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T9.1 | bpmn-js 集成 | Vite 6 + TS + bpmn-js 18 + Viewer | 打包为可 iframe 嵌入的独立页；与后端契约只有 deploy / 回显两个接口，**不直接碰库** |
| T9.2 | moddle 扩展描述符 | `activiti-moddle.json` | **必须先行** —— 没有它，bpmn-js 往返会静默丢掉 `activiti:*` 全部属性 |
| T9.3 | 调色板裁剪 | 只留开始/结束/三网关/用户/服务/手工任务 + 连线 | 子流程、边界事件、泳道等按钮**全部移除**，避免用户画出引擎不支持的图 |
| T9.4 | 属性面板 | 按元素类型白名单渲染（流程/用户任务/服务任务/脚本任务/网关） | 服务任务实现方式**三选一，选中即清空其余两项**；类型选择要用组件状态记住取值（切回来不能丢） |
| T9.5 | 即时校验 | 前端实时高亮 + 后端复校 | 前端校验**只是镜像**，以后端报错为准；两条有意严格项（缺 id、排他网关全条件）前端作警告 |
| T9.6 | 只读实例视图 | 用 `ACT_HI_ACTINST` 给 Viewer 着色（走过的节点/线标红） | 替代 Activiti 的 `image-generator`，省一个模块 |
| T9.7 | 别踩的坑 | 依赖去重 | **双 preact 实例会让 hooks 直接崩页**（加监听器后页面无响应就是这个） |

### WP10 REST 宿主与组合根（P9）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T10.1 | 组合根 | `AddNetBpm` / `NetBpmOptions` / `IProcessEngine` | 配置校验"响亮失败"：`EnableHistory + UseInMemoryExecutionTree` 组合直接拒（内存宿主没有变量仓储 ⇒ 不写变量历史） |
| T10.2 | 端点族 | 13 端点（部署/定义/启动/实例/变量/流程图/待办/claim/complete/历史三查） | **未实现的能力返回 501 而不是空数组**，避免"看着能用其实没数据" |
| T10.3 | JSON 边界还原 | `JsonVariableReader` | System.Text.Json 会把 `object?` 反序列化成 `JsonElement` → 引擎报"类型不支持"。边界必须还原成 `string/long/double/bool/null`，**数值优先 `long`**（否则 `{"days":3}` 落成 `DOUBLE_=3.0`）|
| T10.4 | 异常映射 | `GlobalExceptionHandler` | 引擎异常 → 4xx/5xx 的稳定形状 |
| T10.5 | 委托注册与自检 | `AddServiceTaskDelegates` + `ServiceTaskDelegateGuard` + `ApprovalFormGuard` | `activiti:class` 是 **DI 容器注册名**，不是 Java 式反射类名。解析顺序：keyed 名 → 已注册委托的类型全名/短名。`[ServiceTaskName]` + 程序集扫描是**推荐写法** |

### WP11 对拍验证体系（P10）★★ 正确性的唯一客观标准

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T11.1 | Java 预言机 | `tools/Oracle`（JDK 25 源码在 JDK 21 上编译 + 补 9 张表 + 绕开 MySQL 8 窗口函数） | 9.0.0 不在任何仓库；4 个文件的 `IO.println`（JEP 512）全是日志 ⇒ 换 `System.out.println` 即可。`mysql.properties` 里硬编码 `ROW_NUMBER()` ⇒ 用 classpath 前置覆盖，**不动引擎语义** |
| T11.2 | 语料接入 | `tests/corpus/importer`（四道闸门）+ 199 语料 | 口径修正：**"可直接对拍"是 199 个，不是 554** —— `serviceTask`/`scriptTask` 需要两端各有一份语义相同的实现，否则比的是"实现差异"不是"引擎语义差异" |
| T11.3 | 自建语料生成器 | `tests/corpus/generator` + `--check` 漂移校验 | **禁止手画**：组合覆盖要穷举而非"想起来的"；现成语料里"排他+并行"混合结构是 **0 个** |
| T11.4 | 三维度比对器 | API 返回值 / `ACT_*` 行快照 / 事件序列 + `SnapshotComparer` / `RowOrdering` | 忽略列：时间戳族、ID 具体值族、`ENGINE_VERSION_`、`BYTES_`（有意偏离）。规范化排序键必须**两侧统一 null 表达**（Java 省略 null 字段、C# 曾拼成 `COL=`）。比对器要取**两侧列并集**（absent ≡ null），否则被测侧独有列被静默跳过 |
| T11.5 | 一致性门禁 | 报告 `artifacts/differential/report.md` + `known-issues.json` | 三分桶：跑通 / 跑不动（**算失败**）/ 不适用（必须列出来）。单例失败不中止整批 |
| T11.6 | 差异收口纪律 | 888 → 81 条，全部**回写代码**而非记台账 | 达标线：整体 ≥99.5%，差异 100% 有结论 |

### WP12 应用层与 Demo（计划外，但价值最高的一块）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T12.1 | 应用层门面 | `IWorkflowService`：`StartAsync` / `FindInstanceAsync` / `SubmitAsync` / `FindHistoryAsync` / `ReassignTaskAsync` / `JumpToNodeAsync` | 只依赖 `Bpmn.Model`+`Engine`+`Runtime`，**不引 Hosting/REST**，保证可被任意宿主复用 |
| T12.2 | 数据端口 | `IWorkflowDataStore`（五块数据：实例登记 / 审批结论 / 流程参数 / 审批记录 / **当前待办**） | 端口要参考实现 `JsonWorkflowDataStore`（零落库跑通全链路），换真库时数据形状不变 |
| T12.3 | **提交顺序契约** | `WorkflowService.SubmitAsync` | 三处顺序**不能调换**：① 先落业务数据再推进流程 ② 参数先于结论落盘 ③ 实例登记先于参数表。固定顺序：校验 → 写业务登记 → 写参数表 → 完成引擎任务 → 替换当前待办 → 记审批记录 → 回视图 |
| T12.4 | 审批历史双源单出口 | `WorkflowHistoryBuilder` | `EngineHistory`（`ACT_HI_ACTINST`，权威）与 `BusinessApprovals`（业务记录 + 当前待办合成）；关联 `taskId` 优先 → 退化按 `ActivityName` **按序认领**。结论靠合成；`NextSteps` 由下一条流水反推 —— **引擎不认识审批意见** |
| T12.5 | 流程变量契约 | `BaseModel`（7 个扁平变量）/ `ParaDict` / `VariableValues` | 申请人变量名是 **`applyUser`**（BPMN `assignee="${applyUser}"`），不是 `starter`；ParaDict 按 **key 覆盖**语义（未传不改 / 传值覆盖 / 传 null 置空）；与 BaseModel 重名 → 400 |
| T12.6 | 表单契约 | `WorkflowFormMapper` + `ApprovalFormGuard` | `activiti:formProperty` 只透传不执行；「表单值 → 流程变量」由宿主按 `variable`（缺省 `id`）映射；枚举值须与 BPMN `<activiti:value id>` 一致，否则**拒绝启动** |
| T12.7 | Hosting 公共类 | `WorkflowEndpoints(+Options)` / `WorkflowProcessBootstrapper` / `WorkflowRuntimeContext` + `WorkflowNodeRegistry` | 多流程靠 `ProcessKey` / `ResourcePath` / `NodeRegistry`（按定义 id 分表缓存）参数化；宿主装饰器 `DecorateInstanceView/Summary` 为异步（`ValueTask`） |
| T12.8 | 端到端 Demo | `samples/NetBpm.Demo`（单宿主两条流程、MySQL、七个页面） | 三张业务表**关联键不同**：`instances` 用 `workflow_instance_id`、`instance_current_task`/`deal_history` 用 `apply_id`、`leave_bill` 用 `business_id`；业务单号必须正整数、留空自动发号 |
| T12.9 | 新流程接入契约 | `IBusinessProcess` + `ConstField` + `workflow_type` 插行 | 新增一条流程 = **三步**：实现 `IBusinessProcess` → ConstField 加常量 → `workflow_type` 插行 |

### WP13 打包、文档与硬化（P11）

| # | 任务 | 交付物 | 要点 |
|---|---|---|---|
| T13.1 | 打包分发 | `tools/pack.ps1`（nupkg ×9 + `netbpm-<版本>.zip` + README.txt） | 另产出 dll 目录（`dotnet publish NetBpm.Workflow` 的依赖闭包）供离线直接引用；剔除 publish 带入的重复建表脚本 |
| T13.2 | 文档体系 | README（定位/子集/红线/接入五步）+ `docs/process-design-guide.md` + `db/README.md` + Demo README | 设计指引必须**前置**业务接入（模板：请假/订单） |
| T13.3 | 硬化欠账 | 压测 / 长流程性能 / 崩溃恢复 / 可观测性 / CI | 目标：单实例 ≥500 流程实例/秒启动；P99 命令耗时可测；CI 跑 build+test+format+对拍 |

---

## 3. 横切要点精要（20 条，按主题）

### 3.1 执行树与调度
1. 根执行不参与推进；一旦放进去，并行 Join 会误删流程实例。
2. `IS_CONCURRENT_` **恒 false**（Activiti 9 事实，与计划原文相反）。
3. Agenda 必须队列 + 排空，**不能递归**；一个命令一个事务，循环内不 await 外部 IO。
4. 分支产生：第一条流出流**复用**当前执行，其余新建子执行；全部建完才统一推进。
5. 实例结束判定 = **不存在 active 子执行**（这就是"合并"的本质）。

### 3.2 网关语义
6. 排他 = 取**第一个**为真且非 default 的流；**不加**多真报错。
7. 并行 = 先置非活动 → 按**入流条数总数相等**判定 → 出口**不校验条件**。
8. 排他/包含网关"全条件且无默认流"在 NetBpm 是**部署期**拒（Activiti 是运行期抛）。

### 3.3 表达式
9. `+` 不拼串、`/` 恒浮点除、`"abc"==1` 抛异常、null 比较恒 false —— 四条反直觉语义必须逐条对齐。
10. 不用第三方表达式库：Flee / NCalc / DynamicExpresso 的隐式转换与 null 比较语义差异正好落在"流程分支出错的高发区"。

### 3.4 持久化
11. ID 生成器必须区块分配（2500）+ `REV_` 乐观锁，**否则库级对拍全线失败**。
12. 变量列布局与类型映射要对齐到列（含 `null` 型、`date` 的 UTC 毫秒、`TEXT2_` 的真实含义）。
13. `db 0001` 只建 13 张表，且**不得出现**被裁掉的 12 张表名（测试守着，注释也会被剥离后检查）。
14. 无迁移记录表：已初始化的库不会重跑脚本 → 现网变更必须手工 `ALTER`。

### 3.5 宿主与集成
15. "启动时读配置"的宿主**无法用 `ConfigureAppConfiguration` 覆盖** → 集成测试用 `UseSetting`，并断言"库里真有行"。
16. 内存宿主 ≠ 真库宿主：`Parent` 懒加载、变量历史写入等差异只在真库暴露，**内存模式跑通不代表正确**。
17. Dapper 不做下划线→驼峰映射：所有 SQL 显式别名，别用 `select *`。

### 3.6 应用层
18. `SubmitAsync` 的落库顺序是契约（业务 → 参数 → 结论 → 实例登记 → 待办 → 流水）。
19. 审批历史双源单出口：引擎侧权威，业务侧补意见；`taskId` 关联优先，退化按活动名**按序认领**。
20. 服务任务委托只用一句：`_workflow.CompleteAsync(execution, WorkflowOutcomes.Approved, ct)`。

---

## 4. 剩余任务清单（Backlog，按优先级）

### B1 建议尽快做（业务高频、成本可控）

| # | 任务 | 依赖 | 要点 |
|---|---|---|---|
| B1.1 | ~~**脚本任务执行**（Roslyn Scripting）~~ ✅ 已完成（2026-10-16） | 无 | `ScriptTaskEngine`（`src/NetBpm.Engine/Script/`）：只支持 `scriptFormat="csharp"`（缺省/其它格式部署期拒绝）；脚本顶层可用 `execution`（当前执行）与 `vars`（变量快照，赋值不写回）；`autoStoreVariables` 存脚本顶层变量、`resultVariable` 存最后一条顶层表达式的值；编译结果按脚本原文缓存 |
| B1.2 | ~~**多实例会签 / 串签**~~ ✅ 已完成（2026-10-16，无 DDL 变更；2026-10-20 消除历史行偏差：`ACT_HI_ACTINST` 每实例 1 行） | 引擎（执行树扩展） | 仅 `userTask` 支持（其它活动部署期拒绝，含解析进模型防静默丢弃）；串行 = 主执行逐实例建任务，并行 = N 个子执行各挂一任务（复用并行网关收口机制）；循环变量走流程变量跨命令恢复；`completionCondition` 为真时取消剩余实例。历史活动行已对齐 Activiti：每实例 1 行（串行在同一执行依次开合；并行每实例 1 行 + 主执行容器行，实例完成即收口、提前完成记取消原因）。**仍登记的偏差**：并行模式共享作用域的 loopCounter/elementVariable 只反映最后实例、实例级监听器不逐实例触发、多实例与 skipExpression 互斥 |
| B1.3 | ~~纯条件评测型测试（不改库）~~ ✅ 已完成（2026-10-16，`CorpusInMemoryTests`） | 无 | 全部 210 条对拍语料在**纯内存宿主**（TestEngine）上跑通操作脚本（动作语义与 `DifferentialRunner.ApplyAsync` 逐条对齐：completeAnyTask 确定性挑选 / completeTask 唯一性 / completeAllTasks+limit），0.4 秒回归不依赖 MySQL；断言部署校验干净 + 脚本可执行 + 执行树一致。**分工**：内存套件答「跑得动 + 推进正确」，差分对拍答「与 Activiti 行级一致」；语料脚本歧义（同 key 多待办）与差分执行器同口径跳过。语料读取为最小副本（避免测试工程互引导致 xunit 入口重复） |
| B1.4 | ~~CI 流水线~~ ✅ 已完成（2026-10-16，`.github/workflows/ci.yml`）；**差分对拍 Job** ✅ 已开启（2026-11-02，nightly + 手动触发） | 无 | build + 单测 + MySQL 服务容器跑集成测试（`DbMigrate --create-database` 建表）；⚠️ 测试执行用 `dotnet run --project`（MTP 原生）；`dotnet test` 亦可用，但不要带 `--nologo`（MTP 透传后拒绝 → 「运行了零个测试」/exit 5）。**对拍 Job 不需要 JDK/Maven**：204 份 `*.oracle.json` 真值已随语料入库，独立库 `netbpm_diff` 由测试自建；门禁断言 `report.WithinGate()` 已开（2026-11-02 补上 `<documentation>` 采集、`HAS_START_FORM_KEY_` 与**网关异步**后整体 99.92%，差异 100% 有结论）。配套的两道「可持续」机制（2026-11-02）：**真值新鲜度检查**（`OracleFreshnessTests` —— 语料的最后提交时间晚于真值即失败，先于跑批执行；用 git 提交时间而非 mtime，浅克隆会静默通过，故 CI 显式 `fetch-depth: 0`）与**门禁余量**（报告新增「门禁余量」与「差异构成」两节：已登记的差异仍计入一致率，故要盯「还剩几条额度」；CI 把它写进 job summary）。三轮收口后维度②的差异从 74 降到 16（余量 5 → 63）；维度①（API）与维度③（事件）已归零，不再有「余量 1」这种贴线维度。剩余 16 条：10 条 `REV_`（即时写 vs Activiti 脏检查，属设计取舍）、3 条候选发起人身份链接（定义级候选发起人未实现）、3 条单语料的行数差异。**双库功能矩阵（2026-10-16）**：B1/B2 全部功能 × MySQL（localhost）与 SqlServer（10.1.245.36，Windows 认证）各跑一遍（`DualDatabaseFeatureMatrixTests`，10 项功能 × 2 方言 = 20 行，库表自动创建迁移，方言不可用动态跳过；SqlServer 侧默认由 Testcontainers 拉起 `mssql/server:2022`（2026-10-19 起，此前默认连内网 10.1.245.36；仍可用 `NETBPM_SQLSERVER_CONNECTION` 指到已有实例；Docker 不可用则动态跳过） |

### B2 有条件再做

| # | 任务 | 要点 |
|---|---|---|
| B2.1 | ~~服务任务字段注入 `activiti:field`~~ ✅ 已完成（2026-10-16，`ServiceTaskFieldInjector`） | 注入到委托实例的同名可写公共属性/字段（stringValue 字面量 / expression 执行期求值），成员缺失或类型不兼容响亮失败；⚠️ 单例委托注意点已写入 README；规则 9 改为「拒绝监听器字段注入」（监听器字段仍属未实现，防静默忽略） |
| B2.2 | ~~表单引擎~~ 定稿：**有意不做**（2026-10-16） | 引擎侧维持「只透传 + 宿主映射」（`activiti:formProperty` 往返保真 + 应用层契约），渲染层属表单产品，需单独立项——见 README 表单章节 |
| B2.3 | ~~多租户~~ ✅ 已完成（2026-10-16） | 租户随部署指定 → 定义（ACT_RE_*）→ 实例自动继承（启动不单独收参）→ 执行/任务/历史行逐层透传；同 key 各租户版本独立；`FindProcessDefinitionsAsync(tenantId)` 过滤；REST 部署 `?tenantId=`。**已登记偏差**：变量行 DDL 无 TENANT_ID_ 列不携带租户；无租户全链路 `''`（对拍通过） |
| B2.4 | ~~变量删除、任务局部变量、变量历史级联删除~~ ✅ 已完成（2026-10-16） | `RemoveVariableAsync`（沿作用域链删，bytearray 级联）；任务局部变量（TASK_ID_ 行，`ITaskManager` 四方法 + 门面/REST 端点，随任务存亡）。**历史语义（定稿）**：`ACT_HI_VARINST` 只增不删（与历史身份关系同策略），真库测试钉死。顺带修复级联删除的 FK 顺序缺陷（先取字节行 id → 删变量行 → 按 id 删字节行） |
| B2.5 | ~~部署接口接收原始字节~~ ✅ 已完成（2026-10-16） | `ProcessEngine.DeployAsync` 先截下原始字节随命令透传；`IProcessDefinitionStore.DeployAsync` 增加 `rawResourceBytes` 参数（null 时退回重序列化，兼容内存仓储）；`BYTES_` 现存调用方原文，「有意偏离」消除 |
| B2.6 | ~~引擎侧"按实例 + 候选人"组合查询~~ ✅ 已完成（2026-10-16） | `ProcessEngine.FindTasksAsync`：实例查询 ∩ 候选人查询（组经 `IUserGroupManager` 展开），候选人语义与单独查询同口径（候选人/所属组可认领且未签收） |
| B2.7 | ~~REST API 产品化补齐（P0）~~ ✅ 已完成（2026-10-19） | 引擎新增：运行实例列表 `IExecutionManager.FindProcessInstancesAsync`、历史实例/跨实例历史任务/历史变量查询端口、候选组查询 `FindTaskCandidateGroup`、运行期写变量 `SetVariablesCommand`。门面新增 `FindProcessInstancesAsync` / `FindHistoricProcessInstancesAsync` / `FindHistoricVariablesAsync` / `SetVariable(s)Async` / `GetTaskAsync` / `FindTaskIdentityLinksAsync` / `ProcessDefinitionQuery`；`HistoricTaskQuery` 解除「实例 id 必填」（保留「至少一个条件」防全表扫描）。REST 新增 11 个端点：实例列表、任务详情、身份关系、转办、跳转、变量单写/批写、历史实例列表、跨实例已办、历史变量、定义分页/版本。列表统一 `firstResult/maxResults`（默认 20 / 上限 200）+ `X-Total-Count`。**顺带修复**：`JsonVariableReader` 条件表达式把 `long` 提升为 `double`，导致整数变量落库为 `VAR_TYPE_='double'` |
| B2.10 | ~~REST API 产品化补齐（P2 收尾）~~ ✅ 已完成（2026-10-19） | 鉴权：可选 `NetBpm:Security:ApiKey`（`X-Api-Key` / Bearer），`X-User-Id` / `X-Tenant-Id` 进入 `RequestContext`，启动实例 / 认领 / 列表默认租户与操作人回落到上下文（`RequestContext` + `AuthenticationMiddleware`）。用户级 JWT：`NetBpm:Security:Jwt:SigningKey`（HS256，≥32 字节，可选 `Issuer` / `Audience`），`Authorization: Bearer <jwt>`，`sub` → 操作人、`tenant` / `tenant_id` → 租户；与 API Key 并存，JWT 模式忽略身份请求头（防提权），幂等哈希改用解析后的身份（跨用户同 key 返回 409 而非回放）。幂等：`Idempotency-Key` 中间件，内存 / `ACT_IDEMPOTENCY` 双实现（新增 `0005_create_idempotency.sql`，MySQL + SqlServer），同键同请求回放、不同请求 409、5xx 删除占位；`IIdempotencyStore` 在组合根按宿主注册。分页：新增 `PagedResponse<T>` 与 `?envelope=true`，列表统一默认 20 / 上限 200（含 `/tasks`），保留裸数组 + `X-Total-Count` 兼容。测试：`SecurityAndIdempotencyTests` 覆盖 401/200、租户隔离、请求头认领、幂等回放/冲突、信封与钳制；`JwtAuthenticationTests` 覆盖伪造签名 / 过期 / 错受众 401、声明透传与请求头防伪、API Key 并存、幂等按身份隔离。**未做**：——（两项均已补齐，2026-11-02 复核）：**事件订阅已交付**（`/admin/v1/subscriptions` + `DapperEventSubscriptionStore` + `CloudEventOutboxHandler`，`NetBpmHost` 已接线）；**RS256 / JWKS 已交付**（`JwtKeyMaterial` 支持 RSA 私钥/公钥 PEM 与算法白名单、`RemoteJwksProvider` 拉外部 IdP 密钥集、`/.well-known/jwks.json` 对外发布公钥） |
| B2.16 | ~~**变量读写的参与人校验**~~ ✅ 已完成（2026-10-19） | 新增 `VariableAuthorization`：JWT 下流程实例变量要求发起人，或实例运行中任务的办理人 / 所有者 / 候选人（含候选组）；任务局部变量要求该任务参与人，或实例发起人；`netbpm.admin` 可越过。`EnsureInstanceAccessAsync` 改为返回已校验实例（避免重复查询），`TaskAuthorization.IsParticipantAsync` 复用候选人判定。7 个变量端点（实例读 / 单写 / 批写 / 删，任务读 / 写 / 删）全部接入。API Key 保持可信服务语义。测试 `VariableAccess_ShouldRequireInstanceParticipantOrAdmin` 覆盖发起人 / 办理人 / 候选用户 / 非参与人 403 / 管理员 / API Key |
| B2.17 | ~~**实例活动级 modify（cancel / start-before）**~~ ✅ 已完成（2026-10-21） | 引擎新增 `CancelActivityCommand`（按 `processInstanceId + activityId` 定位活动执行，逐条结束待办 → 删边界定时作业 → 删执行并收口历史/变量/候选关系；取消最后一条分支后 `PlanEndExecutionOperation` 让实例正常结束）与 `StartBeforeActivityCommand`（新建并发子执行挂在目标活动上，`ContinueProcessOperation` 真正启动该活动；非流节点拒绝）。REST：`POST /process-instances/{id}/activities/{activityId}/cancel|start-before`，审计 `instance.activity_cancel|start`，实例不存在 404、无活动执行/被并发抢占 409。测试：单测 6 例（含并行分支只取消目标、重复取消、顺序流被拒），双库集成 3 例 × 2 方言（运行态/历史收口、start-before 双分支、**并发取消恰好生效一次**）。**未做**：完整 Camunda modify（按指令批量 cancel/start + 变量作用域指令）留待二期 |
| B2.18 | ~~**控制台 API 契约收敛（模型 / 作业 / 分页）**~~ ✅ 已完成（2026-10-21） | 模型：`PATCH /models/{id}` 局部保存（PUT 全量会清空描述/SVG）、`If-Match` 乐观并发（409 + `currentRevision`，保存/回滚/发布共用）、`POST /models` 新建与导入/复制/反建、`POST /{id}/validate` 预检、`GET /{id}/publications` 发布历史、历史分页、发布 `skipIfUnchanged`（版本水位判据，回放线上定义）、`(tenant_id, process_key)` 唯一索引 + 409 预检（复制自动换 key 并改写 XML）、`0010_model_key_unique.sql` 升级脚本。作业：`GET /jobs`、`/jobs/stats`、`POST /{id}/due-date|execute`（立即触发沿用抢占语义）。批量：`tasks/batch/due-date`、`process-instances/batch/suspend|activate|migrate`。分页：Admin 默认页大小统一 20（上限 200），`envelope` 保持 opt-in 以兼容设计器。部署列表补 `definitionCount/latestVersion/suspendedCount`，定义版本改为「先排序后分页」。测试：`DualDatabaseConsoleApiTests`（MySQL + SqlServer 全链路）、`AdminModelGovernanceTests` 增 If-Match/唯一 key 用例 |
| B2.19 | ~~**控制台前端功能补齐**~~ ✅ 已完成（2026-10-21） | 模型页：预检、发布（内容未变提示跳过）、发布历史、版本回滚、If-Match 冲突提示、导入/复制/反建。部署页：定义数 / 最新版本 / 挂起数。监控页：实例活动级 cancel/start-before、变量直写、批量改到期时间、任务候选人/委派/解决/表单、作业立即触发。API 层补齐实例迁移、批量转办、身份组查询（`identityApi`），为后续 UI 留口。验证：`tsc` + vitest（admin 25 / designer 43） |
| B2.15 | ~~**API Key 服务作用域（AllowedTenants + 租户隔离）**~~ ✅ 已完成（2026-10-19） | 配置 `NetBpm:Security:ApiKeyTenants`（数组）后，服务密钥被限定在白名单租户：`RequestContext.AllowedTenants` + `EffectiveTenantId`（单租户自动回落）；`RequestIdentity.Tenant` 对部署 / 启动 / 列表校验（多租户必须显式、越界 403）；`AuthenticationMiddleware` 校验 `X-Tenant-Id` 必须在白名单（修复用请求头「自证」租户、绕过按 id 隔离的漏洞）；`TenantGuard` 对实例 / 任务 / 历史 / 部署按生效租户返回 404。留空保持「可信服务可跨租户」旧行为。测试 `ApiKeyScopeTests`（7 例）：单租户默认回落、多租户强制显式、跨租户读写 404、越界请求头 403、作用域内头部过滤、任务更新 / 改派 / 删除实例 |
| B2.14 | ~~时间契约与幂等表清理~~ ✅ 已完成（2026-10-19） | `UtcDateTimeJsonConverter` 统一把响应时间按 UTC 序列化（`Kind=Unspecified` 视为 UTC），修复 MySQL/SqlServer 读回时间缺 `Z`、浏览器按本地时区解析的问题；集成测试 `Timestamps_ShouldSerializeWithUtcDesignator` 锁定。新增 `IdempotencyCleanupService`（BackgroundService，启动后 1 分钟首轮、每小时一次）调用已存在但无人调用的 `PurgeExpiredAsync`，避免 `ACT_IDEMPOTENCY` 无限增长 |
| B2.13 | ~~任务级授权（办理人 / 候选人 / 管理 scope）~~ ✅ 已完成（2026-10-19） | 新增 `TaskAuthorization`：完成要求办理人或候选人（含候选组），认领在“已配置候选人”时只允许候选人，取消认领 / 委派 / 解决委派要求办理人或所有者；`netbpm.admin` scope 可越过。`RequestContext` 解析并保存 `groups` / `group`（空格、逗号、JSON 数组）与 `scope` / `scp` 声明。越权返回 403；API Key 模式保持可信服务语义不做任务级校验。测试 `TaskActions_ShouldRequireAssigneeCandidateOrAdmin` 覆盖非办理人 403、办理人完成、管理员越权、候选组认领与取消认领。**后续已完成**：API Key 服务作用域见 B2.15、变量读写参与人校验见 B2.16 |
| B2.12 | ~~REST 授权收口：身份不可覆盖 + 租户隔离 + 类型化异常~~ ✅ 已完成（2026-10-19） | `RequestContext` 增加 `AuthMode`（None/ApiKey/Jwt）；`RequestIdentity` 统一裁决：JWT 下 body/query 的 `startUserId` / `tenantId` 与令牌不一致 → 403，API Key 保持「可信服务、可跨租户管理」。`TenantGuard` + `EnsureInstanceAccessAsync` / `EnsureTaskAccessAsync` / `EnsureHistoryAccessAsync` / `EnsureDefinitionAccessAsync` 覆盖实例、任务、变量、认领、完成、挂起、跳转、流程图、历史与部署的按 id 访问，跨租户一律 404。历史实体 / 查询 / 读取 SQL / DTO 补 `TenantId`，`/history/process-instances` 与 `/history/tasks` 按令牌租户过滤。新增 `EngineConflictException`（409）并让「不存在」统一抛 `EngineObjectNotFoundException`（404）；`GlobalExceptionHandler` 集中映射 400/403/404/409/501，5xx 不再回传异常原文（只给 traceId）。REST 端点删除重复 try/catch；`claim` 允许无 body（身份取自令牌）。测试：`JwtAuthenticationTests` 增加身份覆盖 403、跨租户 404、bodyless claim、409 状态码；`GlobalExceptionHandlerTests` 锁住映射与 5xx 不泄漏。**未做**：任务级「仅办理人可完成」等细粒度授权（已由 B2.13 补齐；API Key 服务作用域见 B2.15） |
| B2.11 | ~~数据库连接 TLS 1.2+ 强制（安全告警收口）~~ ✅ 已完成（2026-10-19） | 新增 `SecureConnectionString`：MySQL 在未显式关闭 TLS 时锁定 `TlsVersion=TLS 1.2, TLS 1.3`，连接串显式包含 TLS 1.0/1.1 时启动即失败；`DbConnectionFactory`（引擎唯一连接入口）、Demo 建库引导、集成/对拍测试 bootstrap 全部走同一策略，消除「协商的 TLS 1.0 是非安全协议」告警的降级路径。SQL Server 侧由 SqlClient 4.0+ 保证（不再支持 1.0/1.1），只支持 TLS 1.0 的旧实例（10.1.245.36）按「应淘汰部署」处理，测试默认改连 2022 容器。单测 `SecureConnectionStringTests` 覆盖填充 / 显式关闭 / 显式弱协议拒绝 / 已安全配置保留 |
| B2.9 | ~~REST API 产品化补齐（P2 部分）~~ ✅ 已完成（2026-10-19） | 部署维度：`GET /designer/deployments`（按部署聚合并内嵌定义）、`GET /designer/deployments/{id}/resources`；`DeployedProcessDefinition` 补 `ResourceName`（Dapper 两处 select 增加 `P.RESOURCE_NAME_`）。管理端：`GET /runtime/tasks/stats`（总数/逾期/今日到期/按定义分组）、`POST /runtime/tasks/batch/assignee`、`POST /runtime/process-instances/batch/delete`（逐项结果、非原子、单次上限 200）。运维：`/health/live`、`/health/ready`（DB 不可用 503）、`/api/admin/info`（版本/方言/历史开关/结构版本/uptime）。并发：任务详情暴露 `revision`，`complete` / `claim` 支持 `expectedRevision`，不一致返回 409（`OptimisticLockingException`）。**未做**：——（均已补齐，2026-11-02 复核）：鉴权（B2.10）、幂等键（B2.10）、统一分页信封（B2.10）、事件订阅（`/admin/v1/subscriptions`）都已交付 |
| B2.8 | ~~REST API 产品化补齐（P1）~~ ✅ 已完成（2026-10-19） | 引擎新增：`UpdateTaskCommand`（名称/描述/优先级/到期/分类/所有者）、`AddTaskCandidatesCommand` / `RemoveTaskCandidatesCommand`（`ITaskManager` 新增候选用户/组移除端口，仅删任务级 candidate 行）、`DelegateTaskCommand` / `ResolveTaskCommand`（PENDING 期间完成被拒，resolve 回到 owner）、`ValidateAsync`（解析 + 校验不落库，与部署同一校验器）。REST 新增 9 个端点：`PATCH /tasks/{id}`、候选用户/组增删 4 个、delegate/resolve、`GET /tasks/{id}/form`、`POST /designer/validate`；另加 `GET /api/identity/...` 3 个只读端点（依赖 `IUserGroupManager`，默认空实现返回空集/404）。表单元数据只回传 `formKey` + `formProperty`，不做渲染与校验（延续 B2.2 定稿） |

### B3 属于二期大版本（等于换赛道）

子流程 / 调用活动、**消息 / 信号 / 错误事件**、事件子流程、事件网关、泳道与协作池、
**指令式批量 modify**（活动级 cancel / start-before 已随 B2.17 交付）、
图形 PNG 生成、其余 6 种数据库方言。

> 口径校正（2026-11-02）：**定时边界事件与异步作业（含网关异步、重试与死信）均已交付**，
> 不再属于「二期不做」；仍在拒绝名单里的是消息 / 信号 / 错误类事件与非 timer 边界事件。

> 这 12 项占 Activiti 全部复杂度 **70% 以上**。任何一项的追加都应走**变更评审并重算人月** —— 引擎项目最常见的死因是范围蔓延，不是技术。

---

## 5. 复现与验收命令

```bash
# 构建 + 单测 + 格式检查
dotnet build NetBpm.slnx -c Release
dotnet test tests/NetBpm.UnitTests/NetBpm.UnitTests.csproj -c Release --no-build
dotnet format NetBpm.slnx --verify-no-changes --no-restore

# 语料漂移校验（语料必须由脚本生成）
dotnet run --project tests/corpus/generator -- --check

# 建库（引擎侧 0001）
dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=;Allow User Variables=true;"

# 设计器
cd web/designer && npm install && npm run dev     # http://localhost:5273/?modelId=<id>

# Demo（单宿主双流程）
dotnet run --project samples/NetBpm.Demo --urls http://localhost:5188

# 对拍（需 MySQL + JDK；先补 Activiti 需要的 9 张表，再生成真值，再比对）
mvn -f tools/Oracle/pom.xml compile exec:java \
  "-Dexec.args=--corpus tests/corpus --connection jdbc:mysql://127.0.0.1:3306/netbpm_diff?useUnicode=true&characterEncoding=utf8 --user root"
dotnet test tests/NetBpm.DifferentialTests/NetBpm.DifferentialTests.csproj
```

**本地开发注意（会真的卡住）**

- 常驻 `NetBpm.Rest` 会锁 `bin/` 目录并占 **5080** 端口，且它是集成测试工程的 `ProjectReference` → 构建报 MSB3021/MSB3027。绕行：只 build `src`，再把新 `NetBpm.*.dll` 覆盖到集成测试 `bin` 下直接跑 exe。
- `dotnet test` 报「运行了零个测试」（exit 5）通常是带了 `--nologo`：MTP 会把它透传给测试宿主并被拒绝，去掉即可；按类过滤用 `--filter "FullyQualifiedName~类名"`。
- **集成测试绝不能不加类过滤全跑**：MySQL 用例的 `EngineTableCleaner.Purge` 会清库。
- 直接跑 `bin/Debug/net10.0/Xxx.exe` 时当前目录会成为 ContentRoot → 必须在项目目录下启动。

---

## 6. 若要"照这个模式再做一个"的最小任务集

1. 冻结范围（书面签字）—— 12 条"不做"比 30 条"要做"更值钱。
2. 找到**免费的预言机**（这里就是 Activiti 官方实现）。没有预言机的"照搬"退化为"自创"，验证成本高一个数量级。
3. 沿用上游的**表名与列名** —— 这是库级对拍的唯一前提，事后再改等于放弃验证。
4. 先做**表达式语言与执行树语义**，再谈功能数量。前 90 天不追功能，打穿两个最大风险。
5. 差异**回写代码**，不进台账；门禁（一致率 + 差异结论率）写进 CI。
6. 语言/线程模型差异处（`ThreadLocal` → `AsyncLocal`）用**分析器红线**锁死，不靠自觉。

---

*本文数据来自 2026-10-15 对 `E:\DataCode\NetBpm`（tag V0.1.0）的实测盘点：10 个 src 项目 / 163 个 `.cs` / 约 18,721 行；4 个测试工程 / 155 个用例方法 / 210 个语料；13 张 `ACT_*` 表 × 2 方言；13 个 REST 端点。语义依据见《NetBpm-子集流程引擎-开发计划》§5 与 §15。*
