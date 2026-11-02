# NetBpm 代码审查与重构建议

> 审查范围：`src/` 11 个项目 + `tests/`（356 个 `.cs`，64.7k 行；测试 91 个文件、20.2k 行）+ 初始化 SQL 与前端目录结构。
> 深度覆盖 Engine / Persistence / Runtime / Rest / Admin / Workflow / Bpmn / El；`web/admin`、`web/designer` 仅做结构级观察，未逐文件深审。
> 审查基线：`0.1.2`。

---

## 一、总体评价

**工程质量明显高于同规模国产工作流项目。** 具体强项：

- 每个「为什么这么写」都有注释（Activiti 对齐点、风险编号 R4/R8、驱动约束），是决策记录而非注释噪音。
- XML 入口 `DtdProcessing.Prohibit` + `XmlResolver = null`，全程 `XmlReader` 流式，不建 DOM —— 安全基线正确。
- 异步纪律好：`ConfigureAwait(false)` 与 `CancellationToken` 几乎无遗漏；用 `BannedApiAnalyzers` 把 `[ThreadStatic]` 变成编译错误。
- `TreatWarningsAsErrors` + `AnalysisLevel=latest-recommended` + 集中包管理 + 语料脚本化。
- 与 Java 预言机做**差分对拍**、210 条语料内存回归 —— 这是这类项目最值钱的资产，应继续加厚。

**核心症结不在「写得差」，而在三个边界没守住：**

| 边界 | 症状 |
|---|---|
| **模块边界** | `DapperExecutionManager`（1150 行 / 7 个协作者：5 必选 + 2 可选）、`IProcessEngine`（47 个异步方法 + 同步重载，479 行）两处仍 God 化；`RuntimeEndpoints` 已拆分（§十二），此项减一 |
| **事务边界** | 命令内靠拦截器；**命令外**（管理端 / 迁移 / 归档 / 作业失败回记）多语句写无事务，靠「碰巧不失败」 |
| **并发边界** | 内存宿主是全局共享可变图；多宿主作业抢占在 Redis / DB / 内存三处实现语义各不相同 |

---

## 二、P0（阻断级，先修）

| # | 问题 | 证据 | 影响 | 修复状态 |
|---|---|---|---|---|
| P0-1 | 议程没有步数上限，存在合法死循环 | `Agenda/EngineAgenda.cs:86` 裸 `while (_operations.Count > 0)` | 回退边或恒真条件环在单个命令内无限入队，内存无界增长，只能靠调用方 `ct` 打断 | ✅ 已修复（详见 §八） |
| P0-2 | 终止结束事件后队列残留操作；内存宿主删除不置 `IsEnded` | `Behavior/EventBehaviors.cs:67`、`Operations/ContinueProcessOperation.cs:50`、`Execution/InMemoryExecutionManager.cs:342-359` | 终止后并行分支上排队的操作继续在已删除的执行上推进，且只在内存宿主复现 | ✅ 已修复（详见 §八） |
| P0-3 | `NetBpm.Workflow` 管理端点零鉴权 | `Hosting/WorkflowEndpoints.cs:66-69`（转办 / 跳转），全项目无 `TaskAuthorization` / `RequestContext` | 宿主一行接入即暴露未授权的转办与跳转，可绕过任务级授权 | ✅ 已修复（详见 §八） |
| P0-4 | 批量异步作业无异常隔离 + 死代码 | `Runtime/ProcessEngine.cs:210-227`（对照 `:261-279`） | 一条作业抛错中断整批，`failed` 永不递增，队列提示不 `Complete` | ✅ 已修复（详见 §八） |
| P0-5 | EL 解析器无深度上限 → 栈溢出 | `El/ElParser.cs:121-137,242`；部署期校验即调用 | 能提交 BPMN 的人可用深嵌套表达式让进程 `StackOverflowException` 退出（不可捕获） | ✅ 已修复（详见 §八） |
| P0-6 | ID 区块分配在 `lock` 内做同步 DB I/O + `Thread.Sleep` | `Persistence/DbIdGenerator.cs:59-69,82,106` | 区块耗尽瞬间整个进程 ID 生成阻塞在数据库往返上；`Thread.Sleep` 阻塞线程池；行锁被持到命令提交 | ✅ 已修复（详见 §八） |
| P0-7 | 同一异步作业可能被两个宿主同时执行 | `RedisJobQueue.cs:63` inflight 无条件回收；`AsyncJobExecutorService` 从不续约（`RenewAsync` 已存在但无人调用） | 租约过期后重复执行作业，产生重复业务副作用 | ✅ 已修复（详见 §八） |

---

## 三、P1（高风险）

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| 1 | 级联删除逐节点往返 | `DapperExecutionManager.cs:890,903,921-951` | 1000 执行节点的实例删除 = 数千次 DB 往返，全程持行锁 |
| 2 | 命令外多语句写无事务 | `DapperTaskManager.cs:467-527`（5 条语句）、`DapperVariableInstanceManager.cs:116-158` | 管理端 / 迁移路径中途失败 → 有开始无结束的历史行 + 孤儿 bytearray |
| 3 | 模型保存 TOCTOU + 唯一键冲突变 500 | `AdminEndpoints.Models.cs:672-710` 先查后写；`BpmnModelStore.cs:441` 事务内重算 `max(revision)`；`GlobalExceptionHandler` 无 `DbException` 分支 | 并发保存丢更新；`ux_bpmn_model_history_rev` 冲突返回 500 而非 409 |
| 4 | 发布非原子 | `AdminEndpoints.Models.cs:843-877`（部署成功、publish 快照第二事务） | 「已发布 / 有未发布修改」状态漂移 |
| 5 | outbox 无抢占 | `DapperEventOutbox.cs:70-90`，handler 在事务外 | 多宿主必然重复投递 |
| 6 | 内存执行管理器是单例共享可变图 | `InMemoryExecutionManager.cs:130,166-174` + `AddSingleton` | 并发命令互相看到脏实体；并行测试互相污染 |
| 7 | ~~委托解析绕过 DI~~ ✅ v1.35 已修 | `ServiceProviderActivityDelegateResolver.cs:42-43`（root provider + `Activator.CreateInstance`） | 委托依赖 Scoped 服务时静默解析到根作用域；忘记注册变成「new 一个」而非报错 —— 现在改为命令作用域内解析，未注册仍按 Activiti 语义反射创建 |
| 8 | Rest 宿主无限流 / 无体积上限 / 无 CORS | 全仓 grep 仅 `Admin/Program.cs` 有 `AddRateLimiter` | `/api/auth/login` 可无限爆破；整段 BPMN XML 无上限读入内存 |
| 9 | API Key = 超级主体 | `TaskAuthorization.cs:227`、`VariableAuthorization.cs:152`、`AdminAuthorization.cs:35` | 密钥泄露即全权限，需文档明示并建议按租户分发 |
| 10 | 监听器异常默认吞掉 | `EngineEventDispatcher.cs:87` | 业务侧监听器写失败静默丢事件 |
| 11 | 作业抢占做两遍 | `ProcessEngine.cs:254` 与 `ExecuteAsyncJobCommand.cs:55` 各抢一次（不同 `now`） | 语义分散，难以推理 |
| 12 | 先删后执行 | `ExecuteAsyncJobCommand.cs:105` 先 `TryDeleteAsync` 再推进 | 内存宿主失败即丢行，靠旧快照重插兜底 |

> **状态（迭代 3 第四批后复核，2026-10-23）**：已闭环 P1-1/2/3/5（§十）、P1-4（§十一「发布与快照同事务」）、
> P1-11（§十一「作业抢占合一」）、P1-12（`ExecuteAsyncJobCommand` 现为「执行成功后才 `TryDeleteAsync`」，
> 前置删除只用于陈旧 / 已结束作业的作废路径）。
> **仍开放**：P1-6（内存宿主仍是 `AddSingleton`，但已补命令级快照 + `OnAborted` 回滚，风险从「脏树」降为
> 「跨请求可见性」）、P1-7（解析器仍注册为单例并注入根 `IServiceProvider`，`Activator.CreateInstance`
> 兜底仍在 —— Scoped 依赖会被静默解析到根作用域）、P1-10（监听器异常仍只记 warning，语义为「默认不阻断」）。
> P1-8：限流与体积上限已做，CORS 仍未配置；P1-9 已按租户白名单收敛（白名单内仍是全权限）。

---

## 四、架构级重构建议

### A. 统一「会话 / 工作单元」，把事务边界显式化

> **状态（迭代 3 第二批后复核 ✅ 基本闭环）**：`SessionUnitOfWork` 已交付；`IDbSession.State`
> （`SessionState`）接管一级缓存，`IExecutionSession` 完整化；`BpmnModelStore` 的 4 处裸
> `BeginTransaction()` 全部改走 `Acquire()` + `BeginUnitOfWork()`；全仓 `BeginTransaction(` 只剩
> `DbSession`（会话自身）与 `IdBlockAllocator`（**故意**用独立短事务占块）。
> **剩余**：归档 / 迁移 / 审计三条命令外写入仍各自 `BeginUnitOfWork`（已显式，未统一成同一入口）。

当前命令内靠 `TransactionInterceptor`，命令外靠「每条语句自动提交」。

目标形态：

- 引入 `IExecutionSession`（命令作用域），`IExecutionManager` 不再自己管缓存，`DbSession` 只做「连接 + 事务 + flush」。
- 所有写入口（迁移、归档、审计、作业失败回记、Admin 存储）统一 `await using var uow = sessionFactory.Begin()`，禁止裸 `BeginTransaction()`（`BpmnModelStore.cs:459`、`:446` 现在是同步提交）。
- `DbSession.CommitAsync` 内的同步 `transaction.Commit()`（`DbSession.cs:106`）改 `CommitAsync`，与接口注释自洽。

### B. 端点层拆成 Service + TypedResults，授权收进 Filter

> **状态（迭代 3 第四批 ✅ 已交付）**：`RuntimeEndpoints.cs`（HEAD 2042 行；拆分时工作区含
> 上一轮未提交增量，约 2360 行）拆为「路由表 + 4 个域文件 + 共享校验」，授权全部改为声明式 endpoint filter
> （`RequireInstanceAccess` / `RequireTaskAccess` / `RequireHistoryAccess` / `RequireAdminScope`），
> 4 个应用服务（`IInstanceCommandService` / `ITaskCommandService` / `IVariableService` /
> `IHistoryQueryService`）承接业务编排与审计，错误码收敛到 `EngineErrorCode`。
> 完成记录与端点级回归见 §十二。

量化现状（`RuntimeEndpoints.cs`）：49 个 `Map*`、29 处 `Results.BadRequest(new ErrorResponse(...))`、8 处 `catch (EngineException)`、16 处 `EnsureInstanceAccessAsync`、12 处 `TaskAuthorization.*`、7 处变量授权、7 处重复的「最多 200 条」校验。

重构三步：

1. **抽 Service**：`IInstanceCommandService` / `ITaskCommandService` / `IVariableService` / `IHistoryQueryService`。端点只做 HTTP 形状（绑定 → 调 Service → `TypedResults`）。
2. **授权改 Filter**：`RequireTaskAccess(Scope.Complete)`、`RequireInstanceAccess`、`RequireAdminScope(...)` 作为 endpoint filter。当前「先租户后 scope」与「先 scope 后租户」两种顺序并存（`:195` vs `:1477`），审计里 `processInstanceId: null`（`:1434`）都是手工式的必然产物。
3. **错误映射集中**：`ErrorResponse.Code` 混用 `not_found` / `task_not_found` / `process_instance_not_found`。定义 `EngineErrorCode` 常量 + 一个 `IExceptionHandler` 覆盖 `EngineException` / `DbException`（唯一键 → 409）/ `OperationCanceledException`。

**顺带**：`Rest/Program.cs` 与 `Admin/Program.cs` 约 60% 逐行同构。抽 `NetBpmHosting.AddNetBpmHost(...)`，两个 `Program.cs` 各剩 30 行 —— 同时修掉「Rest 没限流」。

### C. 定义层：模型不可变化 + 校验一次遍历

- `Process.FlowElements` 是 `IList`（`Process.cs:11`），外部 `Add` 即产生孤儿；`SequenceFlow.SourceRef` 与 `SourceFlowNode` 双存易陈旧（`FlowElement.cs:55,61`）。→ 对外 `IReadOnlyList`，写入口收进 `BpmnModelBuilder`，解析末期 `Freeze()`；`SourceRef` 降级为序列化投影。
- 校验 23 条规则各自 `FlowElements.OfType<T>()` 全扫（`ProcessValidator.cs:65-68`）。→ 一次遍历 + `rule.Visit(element)`，共享 `id→元素` 字典与 `ILookup<Type, FlowElement>`。
- 往返保真缺口：`BaseElement.ExtensionElements` 从不被填充，写侧也从不遍历（`BaseBpmnXmlConverter.cs:355-375,595`）→ 设计器保存一次即永久丢失自定义扩展。
- 错误定位：`BpmnXMLConverter.cs:242-244` 只有元素本地名，用 `IXmlLineInfo` 补行列。

### D. 表达式：保留自研，但必须先修三处

结论：**不要换 DynamicExpresso / NCalc**。`BooleanOperations.cs` 的 null 恒 false、数值提升顺序、Java String/Collection 方法垫片，都是 JUEL 细节，成熟库一律不给；换库等于把「可证明对拍」换成「看起来差不多」。

保留的前提：

1. 深度上限（P0-5）；
2. 反射元数据缓存 —— `ElResolver.cs:391` 每次求值 `GetMethods()`；`:469` `Activator.CreateInstance` 在 try 外，目标类型无无参构造时抛 `MissingMethodException` 逃逸；
3. 缓存治理 —— `ExpressionManager.cs:21,28` 用未 trim 原文做 key 却解析 trim 后文本，且无淘汰；校验侧另建 `static ExpressionManager`（`ExpressionRules.cs:24`），与运行时 DI 单例是两份缓存。

### E. 持久化：五类重复应合并

| 重复模式 | 出现处 | 状态 |
|---|---|---|
| `Text/Number/Date` 行映射助手（逐字相同） | 原先 10 份；现统一到 `Persistence/Database/RowValues.cs` | ✅ 已收敛（`DapperTaskManager` 的 `ToNullable` 适配层一并删除） |
| `BuildPaging`（含 `limit 100000` 魔法数） | 原先 2 份 | ✅ 已收敛为 `SqlPaging`（执行树 / 历史 / 作业 / outbox / 两个 Admin 存储共用） |
| 方言 `limit/top` switch | `DapperTimerJobManager:89` / `DapperAsyncJobManager` / `DapperEventOutbox` | ✅ 分页部分已收敛；`DapperAsyncJobManager` 的「取 N 条」仍各自内联 |
| 整个作业存储（Insert/Find/Acquire/Renew/Release/MarkFailure/DeadLetter + SQL 常量） | `DapperTimerJobManager:27-370` ≈ `DapperAsyncJobManager:26-353` | ✅ 已收敛为 `Persistence/Jobs/DapperJobStoreBase.cs`（两个存储各减约 200 行，只保留 Insert/FindById/FindDue/Map） |
| bytearray 三步序列（捕获 id → 删变量 → 删字节） | `DapperExecutionManager` / `DapperVariableInstanceManager` / `DapperTaskManager` | ✅ 已收敛为 `Persistence/Database/VariableCleanup.cs`（按任务 / 按实例 / 任意捕获三种入口，字节 id 按 1000 分片） |

另：`SqlPaging.Build` **不再静默截断** —— `take` 为空直接抛 `EngineException`，
只跳过不限条数的调用方必须显式写 `SqlPaging.BuildUnlimited`；
`DapperHistoryManager` 的 COUNT 与取页仍是两次独立 `Acquire()`（未改）。

### F. 可测试性

> **状态（复核）**：`IClock` ✅ 已落地（引擎内 10 处 `DateTime.UtcNow` 全部走时钟；
> 复核时发现 Rest 层 `TaskCommandService` 统计仍直读 `DateTime.UtcNow`，本次已一并改为 `IClock`）。
> 下面两条仍开放。

- **`IClock`** ✅：`InMemoryExecutionManager`、`TaskBehaviors`、`AsyncJobScheduler`、`ExecuteAsyncJobCommand` 已改；
  仓储写库时间戳仍各自取时间（属数据契约，不进业务时钟）。
- **去静态 `AsyncLocal` 兜底**：`CommandContext.cs:22` + `EngineServices.cs:64` 的 `?? _commandlessAgenda` 会让「命令外调用」静默进入一个没人排空的队列。
- **可替换性**：`ActivityBehaviorFactory.cs:34-50` 与 `MultiInstanceUserTaskBehavior.cs:72` 直接 `new` 行为；`TransactionInterceptor.cs:25` 强制 `as CommandContext`，`RetryInterceptor.cs:42-47` 同类情形却静默不重试。

### G. 建议裁剪的过度设计

- `IOperation` 只有 4 个实现且无外部扩展点，`Name` 只用于一行日志 → 内联为 internal 调度。
- 12 个行为类里 5 个是纯空继承 → 工厂直接返回默认实现。
- `IJobQueue` 三件套在「队列只是提示、DB 才是权威」的自证下可合并为一个 `IJobSignal`。
- 死代码：`ProcessEngine.cs:224` 的 `failed`、`EngineAgenda.cs:31` 的 `PendingOperationCount`、`TaskBehaviors.cs:343-381` 恒 null 的 `SkipReason`、`Unit`/`NoOpCommand`。

---

## 五、缺失索引

> **状态（迭代 2）**：`ACT_IDX_HI_PRO_INST_START`、`ACT_IDX_HI_TASK_ASSIGNEE_START`、
> `ACT_IDX_IDENT_LNK_PROCINST_USER`、`ACT_IDX_OUTBOX_CLAIM` 已加入
> `SchemaCatalog.PerfIndexes`，并由生成器写入两方言脚本；租户过滤的 `coalesce` 已移除，
> 历史 `NULL` 由 0013 段归一。**更正一处误判**：`ACT_IDX_VARIABLE_TENANT_EXEC` 实际存在于脚本
> （`SchemaCatalog.VariableTenantIndexes`），原「DDL 中不存在」的判断有误。
> 未采纳 `ACT_RU_EXECUTION (PARENT_ID_, START_TIME_)`：已有
> `(PROC_INST_ID_, PARENT_ID_)` 覆盖该查询路径，再加一条只会增加写放大。

| 表 | 建议索引 | 依据 |
|---|---|---|
| `ACT_HI_PROCINST` | `(START_TIME_, ID_)` ✅ 已加 | `DapperHistoryManager:690,835` 默认按 `START_TIME` 排序分页，原只有 `END_TIME_` 索引 |
| `ACT_HI_TASKINST` | `(ASSIGNEE_, START_TIME_)` ✅ 已加 | `:746` 已办列表默认排序 |
| `ACT_RU_EXECUTION` / `ACT_RU_TASK` / `ACT_HI_*` | 去掉 `coalesce(TENANT_ID_,'')` ✅ 已改 | `DapperExecutionManager:683`、`DapperHistoryManager:821,873` —— 函数包裹使索引失效 |
| `ACT_RU_IDENTITYLINK` | `(PROC_INST_ID_, USER_ID_)` ✅ 已加 | `DapperTaskManager:874` 每次办人变更 `count(*)` |
| `ACT_EVT_OUTBOX` | `(PUBLISHED_, DEAD_LETTER_, NEXT_ATTEMPT_)` ✅ 已加 | `DapperEventOutbox` 待发布扫描 + 发布租约 |
| `ACT_RU_VARIABLE` | `(TENANT_ID_, EXECUTION_ID_)` — 已存在 | `ACT_IDX_VARIABLE_TENANT_EXEC`（`SchemaCatalog.VariableTenantIndexes`） |

另：~~`DapperExecutionManager.cs:1256` 的 `in @ids` 无分片，SQL Server 超 2100 参数会报错~~
→ ✅ 已处理：`DeleteExecutionsBatchAsync` 按 `MaxParametersPerStatement = 1000` 分片
（`DapperExecutionManager.cs:971-996`），级联删除同时改为按层批量。

---

## 六、路线图

**迭代 1（正确性止血，1-2 周）** P0-1 → P0-2 → P0-4 → P0-5 → P0-3，每条配「先红后绿」的测试。

**迭代 2（安全与一致性）✅ 已完成** Rest 共享组合根（限流 / 体积上限）→ `IExceptionHandler` 覆盖 `DbException`→409 → 模型保存条件 upsert → outbox 租约抢占 → 删掉 `coalesce` 并补索引。完成记录见 §九。

**迭代 3（结构性重构，按需）** 事务边界统一（`IExecutionSession`）→ 级联删除批量化 → `RuntimeEndpoints` 拆 Service + 授权 Filter → 校验单次遍历 → 持久化五类重复合并。

**不要做的**：不要为了「更像 Activiti」继续加抽象层；不要引入 MediatR / AutoMapper 这类会加剧间接层的框架 —— 本项目的优势恰恰是「看得见调用链」。

---

## 七、一天内可完成的低风险改进

1. `ExpressionManager` 缓存 key 改 `Trim()` + 加容量上限。 ✅ 已做（key 为 trim 后文本，容量上限 2048 FIFO，`CacheCount` 可观测；`ExpressionRules` 与运行期共用同一实例）
2. 删除 4 处死代码（`failed`、`PendingOperationCount`、`SkipReason`、`Unit`/`NoOpCommand`）。
   ✅ `PendingOperationCount`、`SkipReason`（连同 `ApprovalChainPlan` 整个 record）已删除；
   `failed` 经核查是**活变量**（三个轮询循环各自统计并打日志），不删；
   `Unit` / `NoOpCommand` **裁定保留**：`Unit` 是 `ICommand<T>` 的返回值类型（不可省），
   `NoOpCommand` 是公共空命令 + 底座冒烟测试的载体，删除收益为负。
3. `BuildPaging` 的 `limit 100000` 改成显式抛错或返回「不限」语义，不要静默截断。
   ✅ `SqlPaging.Build` 的 `take` 为空直接抛 `EngineException`；兜底路径改名 `BuildUnlimited`（显式）。
4. `WorkflowEndpoints.cs:69` 拆行。 ✅ 已拆（现 `:68-69`）
5. `DapperVariableInstanceManager:560` 的注释与 DDL 对齐。 ✅ 已改（现 `:560-564` 写明走 `ACT_IDX_VARIABLE_TENANT_EXEC`）
6. `DbSession.CommitAsync` 改 `await transaction.CommitAsync(ct)`。 ✅ 已改（提交与释放都走异步）
7. `ServiceProviderActivityDelegateResolver` 的 `Activator.CreateInstance` 改为解析不到即抛 `EngineException`。
   ✅ 反射创建失败（无无参构造 / 构造抛错）已翻译成 `EngineException` 并给出「注册到 DI」的指引；
   部署期自检 `ServiceTaskDelegateGuard` 同步改为 `TryResolve`，把异常归入 unresolved 一次报全。
8. 补「终止结束事件 + 并行分支 + 内存宿主」用例。 ✅ `EngineSafetyGuardTests.cs:38-88`

---

## 八、P0 修复记录

| # | 修复内容 | 关键文件 |
|---|---|---|
| P0-1 | 议程增加单命令操作预算（默认 1 万步），超限清空队列并抛 `EngineException`（附最后一个操作名、剩余队列长度与排查提示），可通过 `NetBpm:MaxOperationsPerCommand` 调整 | `Engine/Agenda/EngineAgenda.cs`、`Engine/Command/CommandContext.cs`、`Runtime/NetBpmOptions.cs` |
| P0-2 | 终止结束事件在删除实例后清空议程；内存宿主实例级删除统一置 `IsEnded` 并摘除父子指针；`UpdateSuspensionStateByProcessInstanceIdAsync` 返回真实影响行数 | `Engine/Behavior/EventBehaviors.cs`、`Engine/Execution/InMemoryExecutionManager.cs` |
| P0-3 | `WorkflowEndpointOptions.AuthorizeAdminAction` 授权钩子：未配置时转办 / 跳转一律 **403**（fail-closed），Demo 显式放开 | `Workflow/Hosting/WorkflowEndpointOptions.cs`、`Workflow/Hosting/WorkflowEndpoints.cs`、Demo |
| P0-4 | `ExecuteAsyncJobsByIdAsync` 与 Due 路径共用「单条作业」执行例程：逐条 try/catch、失败按退避重记、队列提示在 `finally` 中确认；删除死变量 `failed` | `Runtime/ProcessEngine.cs` |
| P0-5 | EL 解析器加入嵌套深度上限（100），三元 / 一元 / 括号三处统一检查，超限抛 `ElSyntaxException` | `El/ElParser.cs` |
| P0-6 | 新增 `IIdBlockAllocator`：`DapperIdBlockAllocator` 用**独立短事务**占块（行锁不再持到命令提交）；`DbIdGenerator` 热路径改无锁 CAS，`lock` 内不再有任何 I/O 与 `Thread.Sleep` | `Persistence/IdBlockAllocator.cs`、`Persistence/DbIdGenerator.cs` |
| P0-7 | 异步作业执行期间按租约 1/3 周期心跳续约，续租失败即停止（防止租约过期后被另一宿主接管并重复执行）；心跳任务用 `ExecutionContext.SuppressFlow()` 启动，避免继承命令的 `AsyncLocal` 去复用命令的 `DbSession` | `Runtime/ProcessEngine.cs` |

### 回归验证

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Release` | 0 error；`src/` 下无新增警告 |
| `dotnet test tests/NetBpm.UnitTests -c Release` | 739 用例全绿（13 个 Redis 用例因本机无 Redis 跳过） |
| `dotnet format NetBpm.slnx --verify-no-changes --no-restore` | 通过 |
| 新增护栏用例 | `tests/NetBpm.UnitTests/EngineSafetyGuardTests.cs`（死循环预算 / 终止丢弃排队分支 / EL 深嵌套） |
| 新增库级用例 | `tests/NetBpm.IntegrationTests/MySqlPersistenceTests.IdBlockAllocator_ShouldAdvanceCursorInAutonomousTransaction`（需真实 MySQL，无库自动跳过） |

### 行为变化（升级须知）

1. **转办 / 跳转默认关闭**：使用 `MapWorkflowEndpoints` 的宿主若未配置 `AuthorizeAdminAction`，这两个端点会返回 403。
2. **死循环模型不再「挂住」**：单个命令超过 `MaxOperationsPerCommand` 步会以 `EngineException` 失败并回滚。
3. **ID 区块分配多一次独立连接**：每 2500 个 id 一次，且不再占用业务命令的事务与锁。
4. **内存宿主挂起计数**：`UpdateSuspensionStateByProcessInstanceIdAsync` 返回受影响执行数（此前恒为 1）。

### 后续建议（P1 起）

见上文 §三 ~ §六。三项已全部消化（级联删除批量化 P1-1、命令外写事务边界 P1-2、`RuntimeEndpoints`
拆 Service + 授权 Filter §十二）；后续优先级见 §十三。

---

## 九、迭代 2 完成记录（安全与一致性）

| 项 | 改动 | 关键文件 |
|---|---|---|
| 共享组合根 | 两个宿主各约 160 行重复装配收敛为 `AddNetBpmHost` / `UseNetBpmHost` / `MapNetBpmHostEndpoints` / `ApplyNetBpmDatabaseStartup`；`Program.cs` 分别降到约 20 行 / 120 行 | `Rest/Hosting/NetBpmHost.cs`（新）、`Rest/Program.cs`、`Admin/Program.cs` |
| Rest 限流 | 匿名可达的 `/api` 此前无限流：登录可被无限爆破。现按 IP 分区（登录 10 / 探针 30 / 其它 600 每分钟），管理端沿用旧配置节 | `Rest/Hosting/NetBpmHost.cs`、`Rest/appsettings.json` |
| 请求体上限 | `NetBpm:MaxRequestBodySizeBytes`（默认 10 MB）写入 Kestrel 限制，杜绝「单个 BPMN 上传打满内存」 | 同上 |
| 异常映射 | `DbException` 唯一键冲突 → **409** `unique_violation`；其余 `DbException` → **503** `database_unavailable`（不回传 SQL / 连接串） | `Rest/GlobalExceptionHandler.cs` |
| 模型保存 CAS | `revision` 由 `max+1` 改为**加锁读**最新版本行（MySQL `for update` / SQL Server `updlock, holdlock`），并在同一事务内比较 `If-Match`：不匹配即 409。端点保留预检做快速失败，冲突响应形状不变（`RevisionConflictResponse`） | `Admin/Storage/BpmnModelStore.cs`、`Admin/Endpoints/AdminEndpoints.Models.cs` |
| outbox 租约 | 新增 `IEventOutboxStore.TryClaimAsync`：单语句条件更新把 `NEXT_ATTEMPT_` 当作租约到期时间，抢不到即跳过。多宿主不再「必然各投递一次」；崩溃后租约到期自动重投 | `Engine/Events/IEventOutbox.cs`、`Persistence/Events/DapperEventOutbox.cs`、`Runtime/Hosting/OutboxPublisherService.cs` |
| 租户过滤去函数化 | `coalesce(TENANT_ID_, '') = @t` → `TENANT_ID_ = @t`（4 处查询），并在迁移脚本 0013 段把历史 `NULL` 归一为 `''` | `Persistence/History/DapperHistoryManager.cs`、`Persistence/Execution/*`、`SchemaCatalog`/`SqlGenerator` |
| 索引 | 新增 4 条复合索引（历史开始时间、已办办理人+开始时间、实例+用户关系、outbox 待发布），由 `SchemaCatalog.PerfIndexes` 生成到两方言脚本 | `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs`、`db/*/netbpm_init.sql` |

### 回归验证（迭代 2）

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Release` | 0 error，0 warning（`src/` 与 `tools/`） |
| `dotnet test tests/NetBpm.UnitTests` | **741 用例，0 失败**（13 个 Redis 用例跳过） |
| 新增用例 | `OutboxTests.Claim_ShouldBeExclusive_AndExpireWithLease`、`OutboxTests.TwoPublishers_ShouldNotDeliverTheSameEventTwice` |
| SQL 脚本 | 由 `NetBpm.DbInit --generate-only` 重新生成，diff 仅包含预期新增（索引 + 0013 段） |
| 已知未闭环 | 集成测试 7 例在本机失败（401），**改动前即为同样失败**（`git stash` 基线验证）；与本次改动无关，属测试宿主凭据配置问题 |

### 安全项（迭代 3 第一批已处理）

`src/NetBpm.Rest/appsettings.json` 与 `src/NetBpm.Admin/appsettings.json` 的
`NetBpm:Jobs:Queue:Redis:Auth` 明文口令**已清空**，改为注释说明注入方式：

```bash
dotnet user-secrets set "NetBpm:Jobs:Queue:Redis:Auth" "<password>"
# 或
NetBpm__Jobs__Queue__Redis__Auth=<password>
```

⚠️ 该口令曾以明文进入版本库，应视为已泄露并在所有共享环境轮换；清理 git 历史需单独执行
`git filter-repo`（未在本次改动中做）。

---

## 十、迭代 3 第一批完成记录（一天内可清账项）

| 项 | 改动 | 关键文件 |
|---|---|---|
| 分页统一 | 6 处重复的方言分页收敛为 `SqlPaging.Build` / `BuildUnlimited`；`take` 为空不再静默截断 | `Persistence/Database/SqlPaging.cs`、`Execution/DapperExecutionManager.cs`、`History/DapperHistoryManager.cs`、`Jobs/DapperAsyncJobManager.cs`、`Events/DapperEventOutbox.cs`、`Admin/*Store.cs` |
| 行映射统一 | 10 份 `Text/Number/Date` 私有副本收敛到 `RowValues`（新增 `Long` / `Double` / `Bytes` / `Flag` / `Raw`）；删除 `DapperTaskManager` 的 `ToNullable` 与 `DapperExecutionManager` 的字典拷贝适配层 | `Persistence/Database/RowValues.cs` 及 7 个调用方 |
| 工作单元 | 新增 `SessionUnitOfWork`：命令外显式事务、命令内 no-op、未提交即回滚；覆盖任务删除与变量更新两条多语句写 | `Persistence/Database/SessionUnitOfWork.cs`、`Tasks/DapperTaskManager.cs`、`Execution/DapperVariableInstanceManager.cs` |
| 异步提交 | `DbSession.CommitAsync` 改 `CommitAsync(ct)` + `DisposeAsync()` | `Persistence/Database/DbSession.cs` |
| 委托解析 | 反射创建失败翻译为 `EngineException`；部署期自检 `TryResolve` 归集错误 | `Runtime/ServiceProviderActivityDelegateResolver.cs`、`Workflow/Diagnostics/ServiceTaskDelegateGuard.cs` |
| 死代码 | 删 `EngineAgenda.PendingOperationCount`、`TaskBehaviors.ApprovalChainPlan`（含恒 null 的 `SkipReason`） | `Engine/Agenda/EngineAgenda.cs`、`Engine/Behavior/TaskBehaviors.cs` |
| 口令 | 两个 `appsettings.json` 的 Redis 口令清空，改为 user-secrets / 环境变量注入 | `Rest/appsettings.json`、`Admin/appsettings.json` |

### 回归验证（迭代 3 第一批）

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Debug` | 0 error |
| `NetBpm.UnitTests`（MTP 直接运行） | **743 用例，0 失败，13 跳过**（Redis 不可用） |

### 仍未完成（2026-10-23 复核更新）

1. ~~`IExecutionSession` 完整化~~ ✅ 已交付（§十一）：`SessionState` 接管一级缓存，`BpmnModelStore`
   4 处裸 `BeginTransaction` 改走 `Acquire()` + `BeginUnitOfWork()`。
2. ~~`RuntimeEndpoints` 拆 Service + 授权 Filter~~ ✅ 已交付（§十二）。
3. ~~作业存储抽 `DapperJobStore<T>`~~ / ~~bytearray 三步序列抽公共方法~~ ✅ 已交付（§十一）。
4. **表达式缓存治理** ✅ 已交付；**`ElResolver` 反射元数据缓存** ⛔ 仍开放
   （`ElResolver.cs:392` 每次方法调用都 `type.GetMethods()` 并 LINQ 过滤，热路径上的稳定开销）。
5. `IClock` ✅、内存宿主去单例 ⛔（保留单例 + 快照兜底）、作业抢占合一 ✅、发布与快照同事务 ✅。

---

## 十一、迭代 3 第二批完成记录（架构级 6 项）

| 项 | 改动 | 关键文件 |
|---|---|---|
| 表达式缓存治理 | key 改用 **trim 后文本**（与解析口径一致）、容量上限 2048（FIFO 淘汰，可注入）、`CacheCount` 可观测；校验期 `ExpressionParseRule` 与运行期共用同一个 `IExpressionManager`，不再「校验一份缓存、执行另一份」 | `El/ExpressionManager.cs`、`Bpmn.Validation/ExpressionRules.cs`、`Bpmn.Validation/ProcessValidator.cs`、`Runtime/NetBpmServiceCollectionExtensions.cs` |
| `IClock` | 新增 `IClock` / `SystemClock` 并挂到 `IEngineServices`；引擎内 10 处 `DateTime.UtcNow`（任务创建/领取、作业调度、退避、事件时间、`ProcessEngine`）全部走时钟；DI 用 `TryAddSingleton`，宿主或测试可先注册固定时钟覆盖 | `Engine/Services/IClock.cs`、`EngineServices.cs`、各命令与内存管理器、`Runtime/ProcessEngine.cs` |
| 内存宿主 | 保留 Singleton 注册（引擎门面本身就是单例，改 Scoped 会连锁破坏 DI），但补上**命令级快照 + `CommandContext.OnAborted`**：命令内首次访问执行树时抓快照，命令失败整体恢复（标量、父子结构、变量值）。这才是「共享可变图」的真实危害 —— 失败后内存树停在半路 | `Engine/Command/CommandContext.cs`、`CommandExecutor.cs`、`Execution/ExecutionEntity.cs`、`Execution/InMemoryExecutionManager.cs` |
| bytearray 三步序列 | 收敛为 `VariableCleanup`：按任务 / 按实例 / 任意捕获 SQL 三种入口，统一「捕获 id → 删变量 → 删字节」顺序，字节 id 按 1000 分片；三个仓储的重复代码全部删除 | `Persistence/Database/VariableCleanup.cs`、`Tasks/DapperTaskManager.cs`、`Execution/DapperVariableInstanceManager.cs`、`Execution/DapperExecutionManager.cs` |
| `DapperJobStore<T>` | 租约（抢占/续租/释放）、失败退避、死信、条件删除、按执行/实例删除、计数、改期下沉到 `DapperJobStoreBase`（SQL 由表名生成）；两个作业存储只保留 `Insert` / `FindById` / `FindDue` / `Map`，各自减约 200 行 | `Persistence/Jobs/DapperJobStoreBase.cs`、`DapperTimerJobManager.cs`、`DapperAsyncJobManager.cs` |
| `IExecutionSession` 完整化 | `IDbSession.State`（`SessionState`）接管一级缓存，执行管理器不再用外挂的 `ConditionalWeakTable`；`BpmnModelStore` 的 4 处裸 `BeginTransaction` 全部改走 `Acquire()` + `BeginUnitOfWork()`（异步提交） | `Persistence/Database/SessionState.cs`、`IDbSession.cs`、`DbSession.cs`、`Execution/DapperExecutionManager.cs`、`Admin/Storage/BpmnModelStore.cs` |

### 回归验证（迭代 3 第二批）

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Debug/Release` | 0 error |
| `NetBpm.UnitTests` | **746 用例，0 失败，13 跳过**（新增 3 个：缓存 trim、缓存上限、内存宿主命令失败回滚） |
| `dotnet format --verify-no-changes` | 通过 |

### 仍未完成

> ✅ **`RuntimeEndpoints` 拆 Service + 授权 Filter**（2042 行 / 49 端点）已在迭代 3 第四批交付，
> 见 §十二。至此本路线图列出的结构性重构项全部完成。

### 顺带清账（迭代 3 第三批）

| 项 | 改动 | 关键文件 |
|---|---|---|
| 作业抢占合一 | `ExecuteAsyncJobCommand` 新增 `leaseAlreadyHeld`：到期轮询已在外层按 `LOCK_OWNER_` 抢占，命令内不再重复执行同一条 `update ... where LOCK_OWNER_ = @owner`；按 id 执行的入口（无外层抢占）仍由命令自己抢占 | `Engine/Command/ExecuteAsyncJobCommand.cs`、`Runtime/ProcessEngine.cs` |
| 发布与快照同事务 | 新增 `PublishModelCommand`：部署（嵌套命令复用外层上下文/事务）+ `publish` 快照写入在**同一个命令**内完成，任一步失败整体回滚；端点不再「部署成功后另起一个事务写快照」 | `Admin/Storage/PublishModelCommand.cs`、`Admin/Endpoints/AdminEndpoints.Models.cs` |

> 同事务为什么成立：`CommandExecutor` 检测到 `CommandContext.Current` 非空时走嵌套分支，
> `TransactionInterceptor` 见 `transaction.IsActive` 直接透传（不新开、不提交），
> 而 `IDbSessionProvider.Acquire()` 在命令内返回同一个 `DbSession` ——
> 部署与快照因此共用连接、事务与一级缓存。

---

## 十二、迭代 3 第四批完成记录（RuntimeEndpoints 拆分 + 授权过滤器）

### 拆成什么

| 层 | 文件 | 行数 | 职责 |
|---|---|---|---|
| 路由表 | `Rest/Endpoints/RuntimeEndpoints.cs` | 190 | 49 条路由 + **每条路由声明所需访问级别**，无业务代码 |
| 实例域 | `RuntimeEndpoints.Instances.cs` | 288 | 启动 / 查询 / 生命周期 / 活动干预 / 迁移 / 批量 |
| 任务域 | `RuntimeEndpoints.Tasks.cs` | 434 | 待办查询 / 办理 / 管理动作 / 批量 / 统计 |
| 变量域 | `RuntimeEndpoints.Variables.cs` | 150 | 实例作用域 + 任务局部作用域 |
| 历史域 | `RuntimeEndpoints.History.cs` | 172 | 历史查询 + 流程图数据 |
| 共享校验 | `RuntimeEndpoints.Shared.cs` | 36 | 批量条数（200）/ 400 文案，7 处重复收敛为 1 处 |
| 授权 | `Security/RuntimeAccess.cs` | 310 | 3 个过滤器 + 实体存取 + 逐项辅助（批量用） |
| 服务 | `Rest/Services/*.cs` | 4 个接口 / 4 个实现 / 约 1300 行 | 命令编排 + 审计 + 批处理 + 历史查询 |
| 错误码 | `Rest/EngineErrorCode.cs` + `ResourceNotFoundException.cs` | 92 | 稳定 code 常量 + 带 code 的 404 |

### 三项关键决定

1. **授权顺序固定为「对象存在性 / 租户（404）→ scope（403）」**。
   重构前同一语义两种顺序并存；现在过滤器链的注册顺序就是执行顺序，
   跨租户者拿到的永远是 404，不会因为「先查 scope」而被间接告知对象存在。
   `JwtAuthenticationTests` 新增跨租户 `DELETE` / `suspend` 404 断言锁定该顺序。

2. **过滤器只做「按 id 取对象 + 租户 + 语义授权」，批处理逐项执行不挂过滤器**。
   批量端点若挂「单对象」过滤器会退化成「一条失败整批 403/404」；
   因此批量保持「循环外整批 scope + 循环内逐项租户」，逐项失败仍按 `EngineException` 隔离。

3. **Service 不返回 `IResult`**，审计改用域级 `RuntimeAudit`（成功/失败都留痕）。
   这是与 `AuditedAction`（以 HTTP 状态码判定成败）的分工：端点层用后者，Service 用前者。
   唯一保留在 handler 内的授权是 `claim` 的 `unclaim=true` 兼容分支 ——
   过滤器看不到请求体，而「候选人」与「当前办理人」是两套语义。

### 行为变化（有意为之，均有断言）

| 变化 | 之前 | 现在 |
|---|---|---|
| 对象不存在错误码 | `not_found` / `task_not_found` / `process_instance_not_found` / `variable_not_found` 混用 | 统一 `not_found`；`process_instance_not_running` 保留（语义不同：应改查历史） |
| 活动干预冲突 | 端点内 `catch (EngineException) → 409`（无 code） | Service 翻译为 `EngineConflictException` → 409 `conflict` |
| 请求体校验与授权顺序 | 先校验请求体（400），后查对象 | 先查对象（404），后校验请求体 —— 越权者不再能借 400 探测 |
| 批量端点缺 scope | handler 内 `AdminAuthorization.Ensure` | `RequireAdminScope` 过滤器（响应不变：整批 403） |
| 批量参数校验与 scope | 先校验列表（400），后判 scope | scope 过滤器先执行（403），列表校验在 handler |

### 端点级回归（要求真实 MySQL）

新增 `tests/NetBpm.IntegrationTests/RuntimeEndpointRegressionTests.cs`（7 例）：

| 用例 | 锁定什么 |
|---|---|
| `MissingIds_ShouldReturn404_OnEveryIdBasedRoute` | 28 条按 id 取对象的路由逐条打靶：漏挂过滤器即 500/200，立刻红 |
| `MissingObjects_ShouldUseCanonicalNotFoundCode` | 404 响应体 `code == "not_found"`（错误码收敛的契约） |
| `StartBeforeActivity_ShouldAddConcurrentToken` | 活动干预：新令牌与现有令牌并存（此前只有手工验证） |
| `TaskLocalVariables_ShouldRoundTrip` | 写 → 读 → 删 → 再删 404；局部变量不写回实例作用域 |
| `BatchLifecycle_ShouldIsolateFailuresPerItem` | 批量挂起 / 激活 / 删除；未知 id 只让该项失败 |
| `BatchMigrate_ShouldMoveAllInstancesToTargetVersion` | 批量迁移 + 活动映射（`task_approve` → `task_review`） |
| `BatchValidation_ShouldReturn400WithValidationCode` | 空列表 / 缺目标 → 400 `validation_error` |

### 回归验证（迭代 3 第四批）

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Debug` | 0 error |
| `NetBpm.UnitTests` | **744 用例，0 失败，13 跳过** |
| `NetBpm.IntegrationTests`（真实 MySQL） | **200 用例：157 通过 / 36 跳过 / 7 失败**；7 例与本次改动前基线完全一致（6 例 `AdminModelGovernanceTests` + 1 例 `DualDatabaseConsoleApiTests`，均为 Admin 宿主凭据 401，与运行态端点无关） |
| `dotnet format --verify-no-changes` | 通过 |

### 未采纳

- **不给 49 个端点补 `TypedResults` 具体类型**：现有 `.Produces<T>()` 已足够驱动 OpenAPI，
  统一改 `Results<Ok<T>, NotFound<ErrorResponse>>` 会让 handler 与 Response 类型二次耦合，
  收益仅为「编译期检查返回类型」，与本次拆分的风险相比不划算。
- **`GET /process-instances/{id}/migrations` 维持「未知实例返回空表」**：
  它属于审计类查询，与历史查询同口径（不区分「没有记录」与「对象不存在」）；
  改成 404 会让前端多写一条分支，且需要额外一次存在性查询。

---

## 十三、二次 Review（2026-10-23，迭代 3 收尾后）

复核方式：把文档里的每条断言回到 HEAD 代码取证（grep + 读实现）——能复现的保留、
已失效的当场改文档、文档没覆盖的补录。**结论：文档多数结论仍成立，但有 8 处状态已过期**
（前几轮改完没回写），另有 2 个本轮新引入的不一致已修。

### 13.1 已校正的失效断言

| 位置 | 原文 | 实际（证据） |
|---|---|---|
| §一 模块边界 | `RuntimeEndpoints` 2042 行是 God 对象 | 已拆为 6 个文件、最大 434 行（§十二）；God 对象只剩 `DapperExecutionManager`（1150 行）与 `IProcessEngine`（479 行 / 47 个异步方法） |
| §三 状态注 | P1-4 发布非原子「仍开放」 | ✅ 已闭环：`PublishModelCommand` 把部署与快照写入放进同一命令/事务（§十一「顺带清账」） |
| §三 状态注 | P1-11 作业抢占两遍「仍开放」 | ✅ 已闭环：`ExecuteAsyncJobCommand` 新增 `leaseAlreadyHeld`，轮询路径只在命令内抢一次 |
| §三 状态注 | P1-12 先删后执行「仍开放」 | ✅ 已闭环：`ExecuteAsyncJobCommand.cs:113` 业务执行成功后才 `TryDeleteAsync`；前置删除仅用于陈旧/已结束作业作废 |
| §四A 状态注 | `IExecutionSession` 与 `BpmnModelStore` 4 处同步 `BeginTransaction` 未做 | ✅ 已做：`SessionState` 接管一级缓存；`BpmnModelStore.cs:249,446,492,531` 全部 `BeginUnitOfWork`。全仓 `BeginTransaction(` 仅剩 `DbSession`（会话自身）与 `IdBlockAllocator`（故意独立短事务） |
| §四E 表格 | `DapperJobStore<T>`、bytearray 三步序列「⛔ 未做」 | ✅ 已做：`Jobs/DapperJobStoreBase.cs`（223 行）、`Database/VariableCleanup.cs` |
| §四F / §七.1 | `IClock` 未落地、表达式缓存无 trim/无上限 | ✅ 已做：引擎 10 处走时钟；`ExpressionManager` key 为 trim 文本 + 2048 上限 + `CacheCount` |
| §十 未完成清单第 1/3/5 条 | 指向已交付项 | 已改为「已交付 / 剩余项」；`ElResolver` 反射缓存单独标为仍开放 |

### 13.2 仍未闭环（按优先级，附证据）

| 优先级 | 项 | 现状与证据 | 建议 |
|---|---|---|---|
| 高 | ~~**P1-7 委托解析仍走根容器**~~ ✅ **v1.35 已闭环** | `CommandContext` 新增 `ScopedServices`（惰性开命令级 DI 作用域）+ `ReleaseServiceScope()`；`CommandExecutor` 在 finally 里**先释放作用域再释放事务**（作用域里的仓储 Dispose 时还要用会话），`ResetForRetry` 也重建作用域（否则重试拿到「新事务 + 旧会话」）；`ServiceProviderActivityDelegateResolver` 改为 `CommandContext.Current?.ScopedServices ?? 根`，命令外（部署自检 / 诊断 / 测试 harness）退回根容器 | 反向验证过：把 `Provider` 临时改回根容器，`ScopedDelegateResolutionTests` 立刻红在 `Cannot resolve scoped service 'System.Object' from root provider.` —— 说明这条路径此前确实是坏的，不是「理论上可能坏」 |
| 中 | **P1-6 内存宿主单例** | `InMemoryExecutionManager` 仍是 `AddSingleton`；但已补命令级快照 + `OnAborted` 回滚（§十一），失败不再留下半棵树 | 若要彻底解决需改 `IProcessEngine` 生命周期（连锁改动大），建议保持现状并在文档写明「内存宿主 = 单进程单租户演示」 |
| 中 | **§四C 定义层四条** —— 其中「扩展元素往返保真」✅ **v1.36 已闭环**，其余三条维持现状 | 那份判断有一处过时：解析侧**早已**捕获未知扩展（`CaptureExtensionElement`）。真正的洞在写侧 —— `WriteExtensionElements` 只写已知四类（监听器 / 字段 / 表单属性），从不遍历 `element.ExtensionElements`，还会吐出空的 `<extensionElements/>`；流程级 `WriteProcess` 同理漏写 `Process.ExtensionElements`，且写出条件只看监听器。再叠加 `ExtensionElement` 只存前缀不存 URI、`xmlns` 被当属性收下 —— 三处一起构成「设计器保存一次，`netbpm:parameters` 就没了」（部署与模型保存都会重新序列化：`ProcessDefinitionStore.Serialize` / `AdminEndpoints.Models.cs:534`） | 新增 `ExtensionElement.NamespaceUri`；新增 `WriteExtensionElement`（递归写属性 / 文本 / 子树，前缀只作美化）；流程级补写。`Freeze()` / 单次遍历 / `IXmlLineInfo` 三条仍维持现状（重构收益低于风险） |
| 中 | ~~**§四D-2 `ElResolver` 反射元数据缓存**~~ ✅ **v1.34 已闭环** | `MethodCache`（key 含参数个数）/ `MemberCache` / `IndexerCache` 三个 `ConcurrentDictionary`：属性与索引器缓存读取委托，方法缓存已过滤的候选集。**不设容量上限**——key 的基数是「类型 × 成员名 × 参数个数」，来自业务模型类型，天然有界；表达式缓存要上限是因为表达式文本来自 BPMN，用户能写出无限多不同文本，两者膨胀来源不同 | `MethodOverloads_ShouldResolveByArgumentCount_AcrossCachedCalls` 锁住「key 漏掉参数个数会错配重载」这条边界（这条错只在同类型先后用不同参数个数时出现，最容易漏测） |
| 中 | ~~**P1-10 监听器异常默认吞**~~ ✅ **v1.37 闭环 + v1.38 补齐关键监听器强制** | 比「吞」更糟的是**彻底静默**：原写法 `services.Logger?.LogWarning(...)` 里的 `?.` 一短路，宿主没注入 `ILogger`（手工装配 `EngineServices` 的场景）时异常就消失了，与 `IEngineServices.Logger` 注释承诺的「未注入时响亮抛出」正好相反。v1.37：失败计入 `netbpm.listener.failure`（tag `event` / `kind`），有日志记 warning，无日志直接 `throw`。v1.38：新增 `[CriticalEventListener]`，判据挂在**实现类型**上（与 `class` / `delegateExpression` 写法无关），部署期自检（`ServiceTaskDelegateGuard`）要求这类监听器在 BPMN 上必须声明 `throwOnFailure="true"`，否则拒绝部署 | 判据放在实现类型而非流程配置：同一个实现会被不同流程引用，「失败算不算丢数据」是实现的固有属性。没有新增全局开关 —— 逐监听器的 `throwOnFailure` 已经能表达，再加全局开关会让同一份 BPMN 在不同宿主行为不同 |
| 低 | ~~**错误码仍有 7 处无 `code`**~~ ✅ **v1.34 已闭环** | 新增 `EndpointResults`（400 `validation_error` / 404 `not_found`）作为唯一构造点：设计器 4 处（请求体空 ×2、部署不存在、定义不存在）、身份 2 处（组不存在）、登录 1 处（缺参数）全部改用它；`RuntimeEndpoints.InvalidRequest` 退化为薄委托，19 处调用点不动 | `EndpointErrorCodeTests` 2 项：登录 400 与未知组 404 都断言 `code`；设计器那几条在 `netbpm.admin.*` 管理 scope 下，由 `AdminGovernanceTests` 的 JWT 链路间接覆盖 |
| 低 | **§四G 过度设计仍在** | `IOperation` 4 个实现 + 接口（`Operations/*.cs`）；`IJobQueue` 三件套（Redis/InMemory/JobQueue.cs） | 维持现状：这两处是「看得见调用链」的代价，裁掉的收益低于改错风险 |

### 13.3 本轮发现并已修（代码）

| 问题 | 说明 | 文件 |
|---|---|---|
| 运行态仍手工构造无 `code` 响应 | 拆分时从旧代码原样带过来，与「错误码集中」目标不一致 | `RuntimeEndpoints.Tasks.cs`（form 400 → `validation_error`）、`RuntimeEndpoints.History.cs`（2 处 404 → `not_found`） |
| 待办统计绕过 `IClock` | `TaskCommandService` 用 `DateTime.UtcNow` 判断逾期/今天到期，与引擎决策不同源，固定时钟测试会对不上 | `Services/TaskCommandService.cs` |

### 13.4 新发现（未改，建议排期）

| 项 | 证据 | 建议 |
|---|---|---|
| **`AdminEndpoints.Models.cs` 是下一个「大文件」** | 1095 行 / 22 个 handler（partial class，路由在 `AdminEndpoints.cs`） | 按「模型 CRUD / 发布与版本 / 历史与剪枝」拆成 3 个文件，模板直接复用 §十二 |
| `DapperExecutionManager` 仍是最大单文件 | 1150 行 / 7 个协作者（级联删除批量化后从 1287 降下来；原文档「8 个」为笔误） | 按「执行树 CRUD / 级联删除 / 查询映射」拆接口，但需先有跨库集成测试兜底 |
| `InstanceCommandService` 三处批量循环重复 | `BatchDelete` / `BatchSetSuspension` / `BatchMigrate` 各自 45 行同构（逐项租户 + 执行 + 成败审计） | 抽 `RunBatchAsync(ids, action, detailFor, execute)`，可减约 120 行 |
| `ResourceNotFoundException` 继承引擎异常 | 为了让批量端点用单条 `catch (EngineException)` 做逐项隔离 | 可接受（类注释已说明）；替代方案是批量里 `catch (ResourceNotFoundException or EngineException)` 两分支 |
| 历史查询对未知实例的**双语义** | 不受限身份：`/history/.../activities`、`/variables` 返回 200 空数组；受限身份：404（过滤器先拒） | 客户端需同时处理两态；若要统一需额外存在性查询，暂不划算 |
| ~~集成测试 7 例 401 失败是环境问题~~ ✅ **v1.33 已修** | 实际是 8 项（7×401 + 1×403）：`AdminEngineFactory` / `DualDatabaseAdminFactory` 继承管理端 Development 配置里的 dev JWT 密钥 → 中间件进入「必须带凭据」模式；`AdminDictionaryEndpointTests.Writes_ShouldBeAudited` 缺 `workflow:admin:audit:read` → 403 | 两个 Admin 宿主夹具显式清空 `Jwt:SigningKey` + `RequireCredential=false`；该用例改用带审计读权限的 scope 集合。全量集成测试 471 项 0 失败 |

### 13.5 复核回归

| 项 | 结果 |
|---|---|
| `dotnet build NetBpm.slnx -c Debug` | 0 error |
| `NetBpm.UnitTests` | **843 用例，0 失败，13 跳过**（Redis 不可用） |
| `NetBpm.IntegrationTests` 全量（真实 MySQL） | **473 用例：429 通过 / 44 跳过 / 0 失败**（跳过项：SqlServer Testcontainers、Redis） |
| 契约治理三步 | `ContractSnapshotTests` 2 / 2、`CompatibilityTests` 2 / 2、`SdkGenerationTests` 5 / 5 |

> v1.33 修掉 Admin 宿主 401 后，这条基线第一次是「0 失败」而不是「与改动前一致地失败」。
