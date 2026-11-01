# NetBpm 平台化改造 —— 任务清单（勾选版）

> 来源：`docs/platform-api-refactoring-plan.md`（**v1.13**，2026-10-30）。本文只是它的**进度投影**，不替代方案本身。
> 导出时间：2026-11-01 ｜ 代码基线：`270e38d`（v1.24）→ 本轮 v1.25（已推送 `origin/main`）｜ 最新进度见开头 v1.25 小节
>
> **图例**
> - `[x]` 已落地 ｜ `[ ]` 未做 ｜ 部分落地的项用**子项**拆开，不写「半勾」
> - `[!]` 阻塞项（不做它，后面的都推不动）
> - 人日与周数沿用方案 §6 / §8 的原始估算，不是实测

---

## v1.25 本轮落地（2026-11-01，收尾：M3 封旧面 + `/admin/v1` 五个只读面 + S2 出口用例）

**没有新能力，只有三件「说好要做」的事。**

| 项 | 落点 | 验证 |
|---|---|---|
| **M3 收尾：旧面封存** | `WorkflowEndpoints` / `WorkflowEndpointOptions` / 两个 `Decorate*` 全部标 `[Obsolete]`，标记文案写清**替代物是什么**：响应形状由 `Platform.Contracts` 统一，业务化外置到 `/admin/v1/processes` 的覆盖行（`pageUrls` / `labels` / `entryPoints`），业务单号由调用方放进 `StartInstanceRequest.BusinessKey` | `dotnet build NetBpm.slnx` 0 错误、`src/` 侧 0 警告；Demo 局部 `#pragma` 并注明它是迁移示例 |
| **`/admin/v1` 只读观测** | `AdminReadEndpoints`（10 个操作）：`GET /dictionaries`（`workflow:admin:process`）、`/audit` + `/audit/facets`、`/quotas`（`AdminClient`）、`/subscriptions`、`/events/deadletters`（`EventSubscribe`）；全部 RFC 9457 + `PlatformProblem` | `AdminReadEndpointsTests` 8 项（内存）+ `EventDeadLetterQueryTests` 1 项（真库 offset 分页） |
| **S2 出口用例** | 三条都已有归属，本轮**跑通并钉住**：`TaskSubmitTests.Submit_Should403_ForPureServiceToken`、`SecurityAndIdempotencyTests.Idempotency_ShouldReplayAndRejectDifferentRequest`、`ClientAuthenticationTests.RotatedSecret_ShouldReplaceOldOne_AfterGracePeriod` | 8 / 4 / 15 项全绿（真 MySQL） |

**三条口径**（都写进了代码注释——它们解释的是「为什么这么写」，而不是「写了什么」）：

1. **旧面标 `[Obsolete]` 而不是删掉**：Demo 的 e2e 与前端页面都读它的业务 DTO。删掉等于「为了让架构图干净，把演示拆了」；
   标废弃 + 写清替代路径，让新宿主在编译期就收到提示，同时保住既有的验证手段。
2. **管理面读端点不写审计**：审计是「谁改了东西」的流水。把读操作也记进去，真正的干预动作会被翻页噪声淹掉——
   想看读行为去访问日志，那里有 traceId。
3. **租户口径分两类**：`dictionaries` 跟当前管理身份所属租户（字典就是「这个租户的审批口径」，跨租户并列没有意义）；
   审计 / 订阅 / 死信 / 配额跨租户 + `?tenantId=` 可选过滤（平台运维要在一屏里看到「哪个租户在狂发事件」）。

**顺带补的存储能力**（都是「读端点需要、原来没有」）：
`IEventDeliveryStore.ListDeadLettersAsync`（死信按 offset 分页，两方言各走一套 `SqlPaging`；死信判定与排序在 SQL 里）；
`ITenantDirectory.ListAsync` 上移到只读接口；新增 `IClientDirectory`（配额端点从类型上就拿不到「轮换别人密钥」的能力）。

**契约快照**：刷新后 22 路径 / **129 个组件**（+9 个管理面只读类型，纯新增、无行尾抖动），`baseline` 同步。
SDK 产物不变——可达闭包只从 `/workflow/v1` 的 23 个操作算，管理面类型不进入 SDK。

---

## v1.24 本轮落地（2026-11-01，M15 官方 SDK + CompatibilityTests + `/admin/v1` 写路径）

**S5 治理与 SDK 收口**：本轮把 §3 ⑤ 剩下的三件事一起做完——SDK、兼容性用例、管理面写路径。

| 项 | 落点 | 验证 |
|---|---|---|
| **.NET SDK** | `src/NetBpm.Platform.Client`：一层 `PlatformTransport`（拼 URL / 挂身份头 / 非 2xx → `NetBpmApiException` / 2xx 空体与坏 JSON 也转异常）+ 四个资源客户端（`instances` / `processes` / `tasks` / `admin.processes`），覆盖 23 个业务操作 + 4 个覆盖行操作；三个列表提供 `EnumerateAsync` 自动跟游标 | `NetBpm.UnitTests.PlatformClientTests` 10 项（假 handler：查询串词汇 / 身份头 / 令牌每请求刷新 / Problem→异常 / 非 RFC 9457 保留原文 / 游标分页 / 路径转义） |
| **端到端闭环** | `PlatformClientEndToEndTests` 3 项：目录→参数→表单→SVG→发起（**幂等重放不重复开单**）→参数与修订→待办→提交→附件→时间线→终止 | 真 MySQL + 内存宿主 |
| **SDK 生成器** | `tools/NetBpm.SdkGen`：读 `contracts/openapi/workflow-v1.json`，从 23 个被治理操作算**可达 schema 闭包**（120 → 49），生成 `sdk/typescript`（`models.ts` + `client.ts` + 工程骨架）与 `sdk/java`（49 个 record / enum + `pom.xml`）；产物入库 | `SdkGenerationTests` 5 项：产物与重新生成一致 / 23 个操作全覆盖且方法名来自命名表 / 闭包只含可达类型 / 生成可重复 / Java 侧全覆盖；本地另有 `tsc --noEmit` 与 `mvn compile` 验证语法 |
| **兼容性基线** | `contracts/baseline/workflow-v1.json`（发版时从快照复制）+ `CompatibilityTests` 2 项：基线每条路径/方法仍在（运行时探测）、19 项 probe 的真实响应仍符合基线 schema | 内存宿主，无需数据库；已进 CI |
| **`/admin/v1` 写路径** | `IProcessCatalogAdmin`（与只读 `IProcessCatalog` 同一 `ProcessCatalog` 实例，只多出写接口）+ 四个端点 `GET/PUT/DELETE /admin/v1/processes[/{key}]`。**PUT 是整行替换**（`null` = 回到 BPMN 派生），空覆盖 422 `catalog.override_empty`，key 打错 404 `process.not_found`、没精修过 404 `catalog.override_not_found`；每次写入进管理审计，明细只记**改了哪些字段**（不记值：业务页面地址可能带内部域名） | `AdminProcessCatalogTests` 6 项（含「第二次 PUT 只给 category → name 回 null」的整行替换语义、审计明细、scope 403） |

**为什么 PUT 是整行替换而不是逐字段合并：** 管理端要表达的是「这条流程的名片就长这样」。
逐字段合并表达不出「把 `sortOrder` 改回 0」与「没给 `sortOrder`」的区别，也表达不出
「把业务页面地址清空」——于是「清空」只能靠传空字符串，而那又和「没配」混成一个值。
整行替换 + `null` 表示「跟随 BPMN 派生」，再用 `DELETE` 表达「整条不要了」，
语义没有重叠；代价是调用方要传全量，但这本来就是管理端一个表单提交的事。

**顺带修掉一个真 bug：`Idempotency-Key` 在平台面一直被忽略。** 幂等中间件原先只挂 `/api`，
`/workflow/v1` 带 `Idempotency-Key` 会静默失效——端到端用例里表现为「同键重放真的多开了一张单」。
中间件现在覆盖 `/api` + `/workflow/v1` + `/admin/v1`，平台面错误体改回 RFC 9457，
并新增 `idempotency.in_flight`（与 `idempotency.key_reused` 分开：前者「稍后用同键重试」，
后者「这个键属于别的请求」）。

---

## v1.23 本轮落地（2026-11-01，M11 两张表收尾 + M14 契约治理）

| 项 | 落点 | 验证 |
|---|---|---|
| **`nb_process_parameter`** | `IProcessParameterStore`（Dapper / InMemory 双实现）：把 BPMN 派生出来的参数规范**按 `definition_version` 物化**。`ProcessCatalog.GetParameterSchemaAsync` 读到派生结果时顺手落库，已存在则跳过——零后台任务、零配置 | `ParameterStorageTests.Index_ShouldMaterializeDeclaredParameters`（真 MySQL） |
| **版本不覆盖** | 主键含 `definition_version`，`ReplaceAsync` 只删**本版本**行。重部署新版本不会抹掉旧版本的声明，历史单据仍按它停在的那一版渲染 | `Index_ShouldKeepPreviousVersionRows`：同一 `processKey` 两个版本并存 |
| **`nb_parameter_snapshot`** | `IParameterSnapshotStore`：append-only，实例内 `revision` 单调（`max+1` + 唯一键冲突重试 5 次）。发起 / 提交 / 管理直写三处落库，写失败只 `LogWarning` 不阻断主流程（与时间线、表单快照同一条规矩） | `History_ShouldRecordStartAndSubmit`：发起 + 一次提交 = 两条修订，`source` 与 `taskId` 对得上 |
| **`GET /instances/{id}/parameters/history`** | `InstanceCatalog.GetParameterHistoryAsync`：先走详情读模型判存在性（不存在 → 404，与详情同口径），再用**同一份** `ComputeHidden` 过滤 `visibleTo`；老库缺表时降级空历史而不是 500 | `History_ShouldHideParametersHiddenFromViewer`：审批人看不到 `param.costCenter` |
| **归档同 cutoff** | `AdminArchiveCleanupService` 在原有循环里追加 `PruneAsync(cutoff)`，与历史归档、表单快照共用 `NetBpm:Admin:ArchiveRetentionDays`，避免「历史没了、修订还在」的孤儿 | 启动横幅文案已同步 |
| **M14 契约快照** | `contracts/openapi/workflow-v1.json`：`/openapi/v1.json` 只留 `/workflow/v1`（22 条路径 / 23 个操作）→ 递归按键排序 → 固定 `info`（去掉程序集版本）→ CRLF 落盘 | `ContractSnapshotTests` 2 项：一致性 0 差异；覆盖面 ≥ 20 条且不含 `/api/**` |
| **分类 diff** | `OpenApiContractDiff.Compare` 把差异分成**破坏性 / 兼容**两类（删路径、必填化、类型改变、`$ref` 换、枚举取值减少 → 破坏），对应方案 §7.2 | 失败时报告直接列出「哪一处、算哪一类、为什么」 |
| **CI 守门** | `.github/workflows/ci.yml` 新增独立 job `contract-governance`（只读 OpenAPI 文档，不需要数据库） | 契约破了是「契约」红，不是「构建」红 |
| **治理口径** | `contracts/README.md`：覆盖范围 / 更新方式 / 破坏性清单 / 更新快照的三条纪律 | — |

**为什么参数索引按「定义版本」冻，而不是只存当前一份：**

参数规范的真源始终是 BPMN。但审批页要渲染的从来不是「这个流程现在声明了哪些参数」，
而是**「这张单发起时承诺了哪些参数」**。只存当前一份，重部署一次、新版本加了个字段，
三个月前那张已结束的单据上就会凭空多出一行——填过什么、能不能改、该怎么校验，全错。
所以主键带上 `definition_version`，与 `nb_approval_snapshot` 按任务冻是同一条理由：
**契约要跟着它被承诺的那一刻走**，区别只是这里按「定义版本」冻、那里按「任务」冻。

**为什么 `InstanceCatalog` 的四条取声明路径都要传 `definitionVersion`：**

v1.22 之前它取的是「当前版本的参数规范」，对运行中的实例恰好等价，对历史实例则是错位：
实例停在 v1 的定义上，参数面板却按 v3 的声明渲染，于是 v3 新增的必填字段在 v1 的单据上
显示成「已声明未填」——看起来像用户漏填，实际是声明取错了。这轮把 `definitionVersion`
一路传到 `TryLoadSchemaAsync` / `BuildParameterSnapshotAsync` / `LoadHiddenParametersAsync`，
`FindAsync` 传 `definition?.Version`、`GetParametersAsync` 传 `view.DefinitionVersion`。

**为什么修订历史要复用详情读模型判存在性，而不是自己查一次：**

「这条实例存不存在」在这个系统里有两种答案——运行态和历史归档，而且**可见性也在这里收窄**
（本单相关人 vs 管理视角）。另写一份判定，迟早会与详情页给出不同答案：
详情 404、修订历史 200 空数组，等于告诉调用方「有这张单，只是没修订」。
所以先 `FindAsync` 一次，`null` 直接 404；过滤也直接用同一份 `ComputeHidden`。

**为什么 CI 里契约单独成 job：**

塞进 build 当一步也能跑，但红灯的语义就糊了——「构建失败」要翻日志才知道是编译错误还是契约 diff。
契约治理的全部价值在于**让人在 30 秒内决定「这个变更能不能接受」**，
报告出现在一个叫 `contract-governance` 的 job 里，比出现在 build 的第 7 步里有用。

---

## v1.22 本轮落地（2026-11-01，§3③ 四个读形态端点）

| 项 | 落点 | 验证 |
|---|---|---|
| **`GET /tasks/{id}/attachments`** | 复用 `IApprovalSnapshot`：附件引用在提交时随表单快照一起冻住，这里只回放 `attachments` | `TaskAttachmentReadTests`（2 项：未提交 → 404；提交后 → 原样回放） |
| **`GET /instances/{id}/diagram.svg`** | 新增 `IInstanceDiagram`：着色数据源是 `ACT_HI_ACTINST`（不是待办——待办只有用户任务，网关 / 服务任务处会「断线」）；可见性走实例读模型 | `InstanceDiagramReadTests`（3 项：内存宿主降级 / 真 MySQL 验「走过+当前」双色 / 未知实例 404） |
| **`GET /instances/{id}/parameters`** | `IInstanceCatalog.GetParametersAsync`：把值**接到声明上**（标签 / 类型 / 约束 / 谁能写）；`visibleTo` 与实例详情共用 `ComputeHidden` 一份实现 | `InstanceParameterReadTests`（2 项：已声明+已填、已声明未填、未声明补行、排序） |
| **`GET /processes/{key}/form/schema`** | `IProcessCatalog.GetFormSchemaAsync(processKey, version?)`：逐 userTask 派生 `FormSchema`，复用新抽出的 `FormSchemaBuilder` | `ProcessFormSchemaReadTests`（2 项：字段列表 + `approvalResult` 不出现 / 未知流程 404） |
| **`FormSchemaBuilder` 抽取** | 表单字段派生从 `TaskCatalog` 私有方法里抽出（`activiti:formProperty` → `FormField`），待办详情与流程级表单共用一份规则 | `TaskCatalogTests` 5 项回归全绿 |

**四个端点的共同取舍：**

- **「没有」一律 404，不给 200 + 空对象。** 附件没有快照 = 没提交过；表单没有字段 = 这个环节不该出现。
  回空对象会让调用方把「不存在」渲染成「存在但是空的」，而这两种状态在审批页上的含义完全不同。
- **读端点不新增 scope。** 三个读实例 / 待办的走 `instance:read` / `task:read`，
  流程级的走 `process:read`；数据级可见性由读模型决定（实例图跟着实例走、参数跟着 `visibleTo` 走）。
- **参数面板单独存在的理由**是「声明 + 值」的合并：只给值（详情页已带）调用方还得自己拉一次参数规范
  再按 `apiName` 对齐；只给声明则拿不到当前值。两者拼好之后，参数面板才是一个直接可渲染的响应。

---

## v1.21 本轮落地（2026-11-01，审计断档收口：管理干预写时间线 + 写入型端点）

| 项 | 落点 | 验证 |
|---|---|---|
| **前置规矩：管理干预写时间线** | `InstanceCommandService` 注入 `IApprovalTimeline`：跳转写 `INSTANCE_JUMPED`（新增 kind，目标节点进 `TargetNodeId`、原因进 `Comment`）、撤销实例写 `INSTANCE_TERMINATED`、挂起 / 恢复写 `INSTANCE_SUSPENDED` / `INSTANCE_RESUMED` | `AdminGovernanceTests.Jump_And_VariableWrite_ShouldBeAudited` 追加时间线断言（真 MySQL） |
| **变量直写写时间线** | `VariableService` 写 / 删变量前先读旧值，落 `PARAMETERS_UPDATED`，`Comment` 记 `name: 旧值 → 新值`（值截断 120 字符，整条截断 1000） | 同上（同一用例里的 `variable.write`） |
| **`POST /instances/{id}/terminate`** | `IInstanceTermination`：scope `workflow:instance:terminate`；**原因必填**（空白 422）；`ExpectedRevision` 乐观并发（不匹配 409）；数据级只认**发起人或管理视角**（其余 404）；返回终止后的 `InstanceView` | `InstanceTerminationTests`（5 项） |
| **`POST /tasks/{id}/claim`** | `ITaskAssignment.ClaimAsync`：真人主体（纯服务 403）；待办必须未认领（已被别人认领 409 `task.already_claimed`）；非候选范围与不存在**同口径 404**；可带 `expectedAssignee` 做抢单校验 | `TaskAssignmentTests`（8 项） |
| **`POST /tasks/{id}/unclaim`** | 只有办理人本人能放弃（别人 404，改别人的归属走 transfer）；已未认领时幂等 | 同上 |
| **`POST /tasks/{id}/transfer`** | `assignee` 必填（空白 422）；管理视角 / 当前办理人 / 候选范围三者之一才可转；**不改认领时间**（引擎语义） | 同上 |
| **统一拒绝通道** | 新增 `PlatformActionRejectedException`（与 `SubmitDecisionException` 同形状），端点一处转 `PlatformProblem`，错误码 → 状态码只写一遍 | 三个端点复用 |

**为什么管理动作审计不够、还必须写时间线：**

管理动作审计（`admin_audit_log`）是**挂在调用方身上**的：它回答「哪个 client 在几点调了哪个接口」。
而审计真正会断档的地方是**实例自己的时间线**——跳转 / 终止 / 变量直写有授权、有审计，
却在这张单的审批记录里看不到，于是「这张单为什么突然出现在另一个节点」只能靠人去对日志。
文档点名的「唯一有授权但没记录的口子」说的正是这件事，本轮把它补上。
kind 单列 `INSTANCE_JUMPED` 而不是复用 `COMMENT`，是为了让审计能一眼区分
「这张单是被谁批过去的」和「这张单是被谁挪过去的」。

**为什么认领的「不在候选范围」用 404 而「已被认领」用 409：**

两者看似都是「拿不到」，但信息含义相反。
不在候选范围时，这条待办对调用方而言应当**等同于不存在**——回答 403 就等于告诉它「存在，只是不归你」，
而待办的存在性本身就是不该泄露的。已被别人认领时，调用方此刻**已经看得到**这条待办
（它就是从待办列表里点进来的），「被谁拿走了」是它需要知道的信息，属于正常的并发冲突，所以 409 + `task.already_claimed`。

**为什么记录变量的前后值：**

变量直写是**越过 schema** 的管理动作，它改的是单据内容本身。
只写「变量被改过」回答不了合规最关心的问题——「金额在终审前被人改成了多少」。
记值有代价（变量可能含敏感信息），但替代方案是「根本不知道被改过」，两者之间取前者；因此值一律截断。

---

## v1.20 本轮落地（2026-10-31，M11 收尾：全局配额改 Redis + S2 出口用例收口）

| 项 | 落点 | 验证 |
|---|---|---|
| **全局（跨副本）配额** | `RateLimiting/IRateLimiter` 抽出唯一抽象，`FixedWindowRateLimiter`（进程内）与新增 `RedisFixedWindowRateLimiter` 两种实现，按 `NetBpm:RateLimit:Store` 切换，中间件只认接口 | `RedisRateLimitTests`（5 项，真 Redis 127.0.0.1:6379） |
| **窗口由键自身表达** | 键形如 `{prefix}ratelimit:{分钟序号}:{分区键}`，一段 Lua 里原子完成 `INCR` + 首次 `PEXPIRE`（窗口结束 +1s 余量）。窗口切换不需要清理任务，旧键由 Redis 自己过期 | 同一分区被两个 limiter 实例（= 两个副本）共用：3 次配额第 4 次 429 |
| **Redis 不可用时的取舍** | 默认 **fail-open**：放行但 `Counted=false`——中间件不写限流响应头，调用方看到的是「这次没被计数」而不是伪造的剩余额度；要「宁可 429」设 `FailOpenOnError=false` | 端口 6399（无人监听）两组用例：默认放行 / 关掉后拒绝 |
| **启动自检补一条** | `PlatformServiceGuards.VerifyRateLimit`：`Store=Redis` 但没地址 → 抛（与作业队列同款处理）；启动横幅新增 `quota=`，`Memory` 形态额外告警「多副本实际配额 = 副本数 × 配置值」 | 本机 `PORT=5199` 实跑：`RateLimit-Limit: 2` → 第 3 次 429，Redis 里能看到窗口键 |
| **S2 四条出口用例收口** | 幂等重放 / 纯服务 submit 403 / 密钥轮换期双活 / 跨租户 404 —— 逐条核对后**三条已在既有类里覆盖**，一条（`JwtAuthenticationTests.Idempotency_ShouldBeScopedToJwtIdentity`）在 v1.19 之后实际是红的 | `SecurityAndIdempotencyTests` 4 / `TaskSubmitTests` 8 / `ClientAuthenticationTests` 15 / `JwtAuthenticationTests` 11，全绿 |

**为什么「窗口由键表达」而不是存一个 `{count, startTicks}` 的值：**

存值就要先读再写，中间必须有原子性保证；而「读改写」在 Redis 上要么走 Lua、要么走 `WATCH` 重试，复杂度更高，
还要额外处理「上一段窗口的键什么时候删」。把分钟序号写进键名后，
**窗口切换天然等价于换一个键**：不需要判断、不需要清理、不需要锁，旧键由 TTL 回收。
代价是各副本时钟要大致同步（偏差超过一分钟会各自算一段窗口）——进程内实现本来就按本机时钟滚动，行为一致。

**为什么 Redis 挂了默认放行：**

限流是保护手段，不是正确性依赖。让「配额基础设施故障」变成「全站 429」，
是把一个小故障放大成一次全站事故；而「分区表满时放行」早已是同一条判断（见 `MaxPartitions` 的注释）。
真正需要「宁可拒绝」的环境可以显式关掉，但必须同时接受「Redis 抖动 = 全站 429」这个**新**故障模式——
所以它不是默认值，且失败日志**限频 10 秒一条**并打出目标地址，避免一次故障把日志卷打满、把第一条原因冲掉。

**顺手修掉的一个红用例（v1.19 遗留）：**

`JwtAuthenticationTests.Idempotency_ShouldBeScopedToJwtIdentity` 断言「另一个用户用同一个幂等键 → 409」。
v1.19 把幂等键按调用主体收窄后，这个键在 dave 的命名空间里根本不存在，实际是 201。
**改的是断言而不是实现**：安全诉求（不回放别人的响应）仍然满足，而「被别人的键挡住」恰恰是收窄要修掉的问题——
用「业务单号 + 步骤」当幂等键时，第二个人第一次调用若被挡成 409，他就永远发不出去。
⚠️ 教训：改语义时必须把「断言依赖旧语义」的用例一起找出来，否则红用例会一直躺在 HEAD 上
（本轮是逐条核对 S2 出口用例时才发现的）。

---

## v1.19 本轮落地（2026-10-31，S4 收官：收窄幂等 + 容器化 + 端到端演示）

| 项 | 落点 | 验证 |
|---|---|---|
| **幂等键按调用主体收窄** | `IdempotencyMiddleware.ToStorageKey`：幂等键是表主键、全局唯一，两个接入方各用自己的键（`order-1001-submit` 这类「业务单号+步骤」）迟早撞车；撞上后后到的一方拿到 409「已用于另一个请求」，而它明明是第一次调用，换自己的键却要等 24 小时过期。按 `caller → subject → tenant` 收窄命名空间，形态保持可读（`Anon` 分支 / `c\u001f{clientId}\u001f{key}`），超列宽才退化为哈希 | `IdempotencyNamespaceTests`（3 项：同主体回放 / **跨主体同键各走各的** / 跨租户不撞） |
| **`Platform.Service` 生产宿主** | `src/NetBpm.Platform.Service`：与 `NetBpm.Rest`（演示宿主）分开，因为两者默认值相反 —— 演示要「一行命令跑起来」，独立部署要「宁可起不来也不能起来却是残的」。同一套开关切安全基线，迟早出「忘了设 RequireCredential」的事故 | 本机以容器形态实跑：`/health` ok、`/health/ready` → `database=MySql`、`PORT` 生效 |
| **启动自检（fail fast）** | `PlatformServiceGuards.VerifyConfiguration`，**早于 `AddNetBpmHost`**（引擎也会检查连接串，但给的是技术文案）：① 数据库模式无连接串 → 抛（否则静默退化成内存引擎，数据随容器消失）② 队列配了 Redis 却没地址 → 抛（否则退化内存队列，多副本下同一作业执行多次）③ 要求凭据却没有任何校验材料 → 抛（否则所有 `/api/**` 401 而健康检查仍绿）。启动横幅打印生效配置 | 缺连接串时非 0 退出，文案给出可直接照抄的 `NetBpm__ConnectionString` |
| **容器化** | `deploy/Dockerfile`（多阶段 + 非 root + entrypoint 用 `exec` 保信号 + healthcheck 打 `/health` 而非 `/health/ready`）+ `deploy/docker-compose.yml`（MySQL / Redis / platform / worker）+ 仓库根 `.dockerignore` | 本机无 docker 引擎，改用进程形态等价验证（见下） |
| **外部任务 worker** | `samples/NetBpm.ExternalWorker` —— **刻意不引用任何 NetBpm 工程**，只靠 HTTP+JSON：轮询 fetch → 处理 → complete / failure。**worker 内不做重试**，退避与死信全在服务端；网络异常时不回执也不本地重做，放任锁到期由服务端回收 | `run-demo.sh` 实跑全链路 |
| **含 serviceTask 的端到端演示** | `processes/external-task-demo.bpmn20.xml`：`开始 → 通知(notify) → 开票(invoice) → 复核(userTask) → 结束`，两个服务节点都没有 `activiti:class`，只有 `topic` | 演示脚本全绿（见下） |

**端到端实跑结果（两个独立进程，非 docker，用的是同一套组合根）：**

```
⑤ worker 拉走两个服务节点 → 出现「复核」待办          ✅ 实现代码不在引擎进程里
⑥ simulateFailure=true → 3 次退避重试 → 进 DEAD_LETTER ✅ 失败绝不跳过节点
  失败实例的当前待办：[]                              ✅ 流程停在原地
  管理员重投 POST /external-tasks/{id}/retry → 204    ✅ 闭环
```

**仍未做：** 订阅投递的可观测看板（`netbpm.event.delivery` 指标已埋点，没有面板与告警规则）、
单个订阅的独立限速（`nb_event_delivery.next_attempt_at` 已留列，当前不参与判定）。

---

## v1.18 本轮落地（2026-10-31，M12 收尾 + M13 事件投递）

| 项 | 落点 | 验证 |
|---|---|---|
| **死信重投端点** | `POST /api/runtime/external-tasks/{id}/retry`，scope `workflow:admin:instance`（**管理干预，不是 worker 自助**）：非死信 409、跨租户 404、可指定恢复的重试次数 | `ExternalTaskDeadLetterRetryTests`（4 项，内存宿主：404 / 409 / 复活后可重新被领 / 缺 scope 403） |
| **`external_task.no_worker` 告警** | `Runtime/Diagnostics/PlatformAlerts`（`IAlertSink` + 日志出口 + 内存出口）+ `ExternalTaskWatchdogService` 巡检：登记后超阈值无人领 → Critical；死信 → Critical；按 `告警码 + 任务 id` 冷却去重 | `ExternalTaskWatchdogTests`（6 项：阈值内外 / 曾被领过不算 / **冷却期内只报一次** / 死信告警 / 禁用时安静） |
| **表 `nb_event_subscription` / `nb_event_delivery`** | `SchemaCatalog` 定义 + 双方言脚本重生成；`DapperEventSubscriptionStore` / `DapperEventDeliveryStore`（复合主键 + `insert ignore` 幂等） | `EventSubscriptionDatabaseTests`（2 项，MySQL 5.7.44 真库：多值列往返 / 取或建幂等 / 死信与重投） |
| **CloudEvents 1.0 + HMAC 签名** | `Runtime/Events/CloudEvent.cs`：`specversion/id/source/type/time/datacontenttype/subject/data` + `netbpm*` 扩展属性；签名 = HMAC-SHA256 over `{timestamp}.{body}`，`X-NetBpm-Signature` / `X-NetBpm-Timestamp`，校验走常量时间比较 | `CloudEventTests`（12 项：信封 / 类型映射 / 验签 / **改 body 与过期时间戳都验不过** / 订阅匹配 / 投递行为） |
| **投递器** | `CloudEventOutboxHandler`（`IOutboxEventHandler`）：按订阅投递，成功打标、失败抛错交给 outbox 退避重投；**已成功的订阅不重复打扰**；超 `MaxAttempts` 进死信并发告警 | 同上 |
| **订阅管理端点** | `/api/admin/event-subscriptions` CRUD + `/rotate` + `/status` + 投递记录查询 + `POST /api/admin/events/{eventId}/deliveries/{subscriptionId}/retry`；scope `workflow:event:subscribe` | `EventSubscriptionEndpointTests`（5 项：明文密钥只出现一次 / 非法地址 400 / 轮换 / 非死信重投 409 / 缺 scope 403） |

**四条口径：**

1. **告警 ≠ 异常**：异常是「这次调用失败了」，告警是「根本没人来调用」。
   worker 全挂时 fetch 只会正常返回空列表，死信也不会让任何请求报错 —— 流程静静卡在 `serviceTask` 上，
   只能靠巡检发现。巡检**只告警、不动数据**：重投是人的决定，自动处置会把「worker 有 bug 反复失败」掩盖成「看起来在重试」。
2. **退避只由 outbox 管一层**：投递记录里的 `next_attempt_at` 只作展示，**不参与是否尝试的判定**。
   两级退避叠加会出现「事件说可以投了、订阅说自己还在退避」的死结 —— 事件一次次重试、投递一次次拒绝，
   计数永远涨不上去，最后双双进死信却一次都没真发过。
3. **至少一次，幂等在订阅方**：同一条事件重投时 CloudEvents 的 `id` 不变（就是 `ACT_EVT_OUTBOX.ID_`）。
   投递记录（一条事件 × 一个订阅）只用于减少无谓重发，**不是幂等的替代品**。
   一个订阅挂了不会拖住别家：已成功的订阅靠投递记录跳过，只有「还有订阅没投成」时才让事件重来。
4. **订阅密钥只在创建 / 轮换时出现一次**：读接口只有 `secretPreview`（后 4 位）。
   订阅密钥等价于「能伪造 NetBpm 通知」的凭据；另外 PATCH 不接受 `secret`，换密钥必须走 `/rotate` ——
   混在通用更新里容易出现「以为改了地址结果把密钥也换了」的事故。

**v1.19 已收完这道：** `Platform.Service` 宿主 + 容器化（Dockerfile / compose）+ 一条含 `serviceTask` 的端到端演示，
并顺手修掉幂等键跨接入方撞车。详见下面 v1.19 小节。

**仍未做（S5 范畴）：** 订阅投递的可观测看板（`netbpm.event.delivery` 指标已埋点，没有面板与告警规则）、
单个订阅的独立限速（`nb_event_delivery.next_attempt_at` 已留列，当前不参与判定）。

---

## v1.17 本轮落地（2026-10-31，S4 门槛：M12 外部任务）

| 项 | 落点 | 验证 |
|---|---|---|
| **BPMN 支持 `activiti:topic`** | `BpmnConstants.AttributeTopic`、`ServiceTask.Topic` + `ServiceTaskImplementationType.ExternalTopic`；转换器读写与往返保真、加入 `KnownAttributes`（否则会被 `unknown-activiti-attribute` 警告） | `ExternalTaskTopicTests`（6 项：解析 / 往返 / 无实现报错 / **class 与 topic 并存报错** / 仅 topic 通过） |
| **部署期「实现方式必有其一且只能有一个」** | `RequiredAttributesRule` 改为逐个属性判空：全无 → 报错；多于一个 → 报错（改一半没删干净是最容易埋雷的写法）。`ServiceTaskDelegateGuard` 跳过 topic 型，不再把它当「委托解析不到」 | 同上 |
| **引擎侧外部任务** | `Engine/Jobs/ExternalTask`（实体 + `IExternalTaskBroker` + 内存实现）、`ExternalTaskActivityBehavior`（登记后**原地等待**、`TriggerAsync` 才离开）、三个命令（fetch / complete / failure） | `ExternalTaskTests`（7 项，内存宿主） |
| **表 `nb_external_task`** | `SchemaCatalog` 定义 + 双方言脚本重生成；`DapperExternalTaskBroker`（SQL 抢占式领取） | `ExternalTaskDatabaseTests`（3 项，MySQL 5.7.44 真库） |
| **端点** | `POST /api/runtime/external-tasks/fetch`（短轮询）/ `{id}/complete` / `{id}/failure` / `{id}/unlock`、`GET /external-tasks`；scope `workflow:external:task`（新增） | 同 `ExternalTaskTests`（含「缺 scope 403」） |

**四条口径：**

1. **不复用 `ACT_RU_JOB` / `IAsyncJobManager`**：异步作业是「引擎自己晚点做」，外部任务是「等别人做完」——
   生命周期与重试语义都不同（作业失败引擎重跑，外部任务失败要 worker 决定还剩几次），共用一套表会互相污染。
   只借用它的**租约模型**（`worker_id` ⇄ `LOCK_OWNER_`、`lock_expires_at` ⇄ `LOCK_EXP_TIME_`）。
2. **失败绝不跳过节点**：重试耗尽进**死信**，流程原地不动。死信的意义就是让「这一步没做」显式可见 ——
   悄悄跳过会让流程看起来办完了，而那一步从没执行过。
3. **锁即归属**：complete / failure 都校验 `workerId` 且锁未过期，过期即视为不持有。
   worker 崩溃不需要有人解锁（锁到期自动回收），但迟到的回执必须被拒 —— 否则一次调用产生两次推进。
4. **租户必须收窄**：`ExternalTaskFetchRequest.TenantId` 非空即过滤，跨租户按 404（不是 403）处理。
   A 租户的 worker 领到 B 租户的活，等于把别人的活干在别人的流程上。

**~~仍未做（M12 之外的 S4 项）~~ —— 三项后来都落地了**，此处保留是为了不让「看清单的人」以为还欠着：
~~管理端「死信重投」端点~~（`POST /api/runtime/external-tasks/{taskId}/retry` ✅ v1.18，`ExternalTaskDeadLetterRetryTests` 4 项）、
~~无 worker 领取时的告警~~（`external_task.no_worker` / `external_task.dead_letter` ✅ v1.18）、
~~`Platform.Service` 容器化与端到端演示~~（✅ v1.19，已实跑）。

---

## v1.16 本轮落地（2026-10-31，两处遗留风险定方向）

| 项 | 决策 | 落点 | 验证 |
|---|---|---|---|
| **口径 4 残留：引擎面业务写入** | **关掉**（不是补时间线） | `EngineSurfaceScopes.RequireBusinessSurface`：发起 / 办理 / 批量办理对 `Client` 一律 403（与 scope 无关），403 文案直接给 `/workflow/v1` 替代入口 | `EngineSurfaceScopeTests`（13 项：含「有 `task:submit` 仍 403 且任务未动」「批量办理 403」「发起 403 且实例未创建」） |
| **`nb_approval_snapshot` 保留策略** | **与历史归档共用同一保留期** | `IApprovalSnapshotStore.PruneAsync(captured_at)` + `AdminArchiveCleanupService` 在同一循环里用**同一个 cutoff** 清理两张表；新增 `ix_nb_approval_snapshot_captured` | `ApprovalSnapshotPruneTests`（4 项，内存实现）+ `ApprovalSnapshotTests.Snapshot_ShouldBePrunedByCapturedAt`（MySQL） |

**为什么「关掉」而不「让原语面补写时间线」：**

1. 原语面**拿不到**审批选项，硬写时间线只能靠猜语义；而时间线的设计前提是「写时定稿、不猜」——
   一条猜出来的记录比缺一条更坏，因为它冒充了唯一权威记录，事后没人能分辨「没记」和「记错了」。
2. 同样不写时间线的还有跳转 / 终止 / 变量直写（见 §3 ③ 的前置规矩），单补 `complete` 只是把一类漏洞缩小成一个点。
3. 业务写入本来就是平台面的职责：参数 schema、`writableBy`、`optionId` 校验、时间线、快照全在 `/workflow/v1`。
   原语面留给客户端的只剩下「读」与「管理干预」——后者是显式管理授权、可审计的运维动作，不冒充审批结论。

**保留下来的边界（不是漏洞，是刻意的）：**

- **变量直写仍开放**（需 `workflow:admin:variables`）：它是运维纠错的显式管理动作，平台面还没有对应写端点；
  代价是持有它的客户端能改 `result` / `netbpm_outcome` 从而影响结论呈现——这是 admin 的含义，也是审计要覆盖的地方。
- **跳转 / 终止仍不写时间线**：已单独立在 §3 ③「前置规矩」，不在本次范围内。
- **旧凭据不受影响**：`None`（开发）与 `ApiKey`（旧单值密钥）下原语面行为不变 ——
  `Complete_Should204_ForLegacyApiKey` 就是这条护栏，防止「收紧」把已上线的集成一次性打断。

**为什么两份保留期只能有一个键：**

快照靠 `task_id` 与历史行对齐。历史没了、快照还在，是定位不到的孤儿数据；
历史还在、快照先没了，历史页退化成「有结论、没有内容」。
各配一个键的最终结果一定是被人分别调到错位，所以 `AdminArchiveCleanupService` **只读一个**
`NetBpm:Admin:ArchiveRetentionDays`，两张表用同一个 cutoff 一起过期。
⚠️ 该值应当 ≥ 审计/合规要求的年限；`0`（默认）表示永不清理。

**真库验证（2026-10-31，MySQL 5.7.44 / 隔离库 `netbpm_ittest`）：**

v1.14 / v1.15 两轮都跑在没有 MySQL 的机器上，v1.16 的
`delete from nb_approval_snapshot where captured_at < @before` 此前只被内存实现覆盖过。本次补上真库验证：

- ⚠️ **平台表变更后老库必须重跑幂等脚本**——新索引不会随建表自动出现，补它靠的是全幂等脚本：

  ```bash
  dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql \
    --connection "Server=127.0.0.1;Port=3306;Database=netbpm_ittest;User ID=root;Password=;Allow User Variables=true;"
  ```

  本次执行完 `ix_nb_approval_snapshot_captured` 才真正落到库里（此前只有 `ix_nb_approval_snapshot_instance`）。
- MySQL 门控用例全绿：`ApprovalSnapshotTests` 6（含新增的保留期用例）、`InstanceConclusionTests` 4、
  `InstanceListCompletedTests` 1、`ParticipantIndexTests` 3、`JwtAuthenticationTests` 11、`DatabaseScriptTests` 14；
  双库用例的 MySQL 半边也全绿（sqlserver 半边 SKIP）。
- **仍缺的一环：SQL Server 方言**（本机无实例）。`ClientRegistryDatabaseTests` / `AdminUserStoreDatabaseTests` /
  `ConcurrentSubmitIntegrationTests` / `ActivityModificationDualDatabaseTests` 的 sqlserver 半边保持跳过，
  上生产前要在有 SQL Server 的环境补跑一次。

---

## v1.15 本轮落地（2026-10-31，§10 第 1 步收尾：引擎面收口）

| 项 | 落点 | 验证 |
|---|---|---|
| **S2 欠账：引擎面按 `workflow:*` 收紧** | `Security/EngineSurfaceScopes`（`RequireEngineScope` + `netbpm.admin.* ⇄ workflow:*` 桥接）、`RuntimeEndpoints`（40 条路由逐条标注）、`DesignerEndpoints`、`IdentityEndpoints`；`AdminAuthorization` 改为经桥接判定 | `EngineSurfaceScopeTests`（13 项） |
| **跨人读取收紧** | `RuntimeAccess.IsAdminPrincipal`：注册客户端不再「非用户身份即管理方」，全量待办需 `workflow:admin:instance` / `workflow:history:read:all` | 同上 |
| **可观测** | `AdminDiagnostics.AuthModeTag` 增加 `client` 桶；拒绝计数同时覆盖 `workflow:*` 的拒绝 | — |

**四条口径：**

1. **只对 `RequestAuthMode.Client` 生效**。`None`（开发/演示）、`ApiKey`（旧单值密钥）、`Jwt`（真人令牌）保持既有行为——
   一次性收紧会打断全部已上线调用方，而这三类本来就有别的兜底（前两类是「可信服务」，第三类走数据级授权）。
2. **两层授权，顺序不能反**：`RequireInstanceAccess` / `RequireTaskAccess` / `RequireHistoryAccess`（404 + 租户 + 数据级）
   **在前**，`RequireEngineScope`（403）**在后**。反过来跨租户探测会从 404 变成 403，泄露 id 是否存在。
3. **管理动作要翻译**：客户端的 scope 只能取自 `PlatformScopes.All`（`ClientRegistry` 校验），里面没有 `netbpm.admin.*`。
   故 `netbpm.admin.model.read→workflow:process:read`、`model.write→admin:process`、`instance.{jump,delete,suspend}→admin:instance`、
   `task.moderate→task:assign`、`variables.write→admin:variables`。指标 / 审计 / Redis / 作业 / 归档**不翻译**——
   平台面还没有对应端点，宁可拒掉也不发明一个 `workflow:admin:*`。
4. ~~**已知残留**：引擎面 `complete` 不写时间线~~ —— v1.16 已按「关闭写原语」的方向解决（见开头 v1.16 小节），
   引擎面对客户端只剩「读 + 管理干预」。~~**剩下的同类缺口**（跳转 / 终止 / 变量直写仍不写时间线）~~
   —— **v1.21 已补齐**：跳转写 `INSTANCE_JUMPED`、撤销写 `INSTANCE_TERMINATED`、变量直写写 `PARAMETERS_UPDATED`，
   管理干预在实例时间线上不再有断档。

---

## v1.14 本轮落地（2026-10-31，按 §10 建议顺序的第 1、2 步）

| 项 | 落点 | 验证 |
|---|---|---|
| **M5 JWT 升级（RS256 + JWKS + 外部 IdP）** | `Security/JwtKeyMaterial`（密钥唯一来源）、`Security/RemoteJwksProvider`（外部公钥集缓存 + 限频）、`GET /.well-known/jwks.json`、`AuthenticationMiddleware` 算法白名单 | `JwtRs256Tests`（5 项，内存宿主，无需 MySQL） |
| **M7 写侧强制 `writableBy`** | `Platform/ParameterWritePolicy` + `InstanceStartService`（APPLICANT）/ `TaskSubmitService`（APPROVER），错误码 `parameter.readonly` | `ParameterWriteTests`（8 项） |
| **S3 尾巴①：实例列表升序** | `ProcessInstanceQuery` / `HistoricProcessInstanceQuery` 新增 `Descending`，排序与时间边界一起下推到 `order by START_TIME_, ID_`；平台侧游标按方向选上界/下界 | `InstanceListTests.List_ShouldSupportAscending` |

**三条口径，写在这里免得下次再讨论：**

1. `writableBy` **未声明** = 发起时可写、之后只读（这是 `ParameterDefinition` 的原始约定，不是本轮新加的收紧）。
   想让审批人改字段，必须在 `netbpm:parameter` 上写 `writableBy="APPROVER"`。
2. `APPROVER` / `APPLICANT` 判定**不看请求体自述**，只看环节；`ADMIN` 两个环节都放行，
   但仅当调用方确实持管理视角（`netbpm.admin` / `workflow:admin:instance` / `workflow:history:read:all`）。
3. 排序两个方向都必须按 `(开始时间, 实例 id 升序)`：tie-break **不随方向翻转**，
   否则游标在「同刻多条」的边界上会重复或漏行。

---

## 0. 进度总览

| 维度 | 总数 | 已落地 | 未完成 |
|---|---|---|---|
| 阶段（S1–S5） | 5 | 5 | 0 |
| 改造项（M1–M16） | 16 | 15 完成 + 1 部分 | 0 |
| 守门测试用例 | 32 | 32 | 0 |
| 业务面端点（`/workflow/v1`） | 23（另有 1 条已划掉） | 23 | 0 |
| 管理面端点（`/admin/v1`） | 10 个操作 / 8 条路径 | 10 | 0 |
| 平台表（`nb_*`） | 13 | 11 | 2 |
| 人日（估算） | 135 | ≈ 78 | ≈ 57 |

**一句话定位**：S1（契约）、S2（身份与授权，含引擎面对客户端只留读与管理干预）、S3（业务标准）已全部落地，
S4（独立部署）**已完成**：门槛项 M12 外部任务 v1.17、M12 收尾与 M13 事件投递 v1.18、
`Platform.Service` 宿主 + 容器化 + 含 `serviceTask` 的端到端演示 v1.19；
S5（治理 / SDK）**v1.24 完成**：M14 契约治理（v1.23）+ M15 SDK + `CompatibilityTests`。
v1.25 把最后三项收尾做掉（M3 封旧面 / `/admin/v1` 五个只读面 / S2 三条出口用例），**S1–S5 全部收口**。
八个硬缺口中 **A（对外模型）、B（多流程）、D（审批语义）、E（参数 schema + 写侧）、F（身份三元组）、
G（委托进程内）、H（契约治理）已闭环**（H 于 v1.23：OpenAPI 快照 + 分类 diff + CI 守门 + 治理文档）；
**C（响应体可变）于 v1.25 闭环**——旧 `Decorate*` 封存为 `[Obsolete]`，替代路径写进标记与 `Deprecation.Message`；
C（响应强类型）只差 M3 的 Decorator 收尾（唯一剩下的硬缺口）。

---

## 1. 阶段进度（§8）

| 阶段 | 周期 | 状态 | 出口条件达成情况 |
|---|---|---|---|
| S1 契约与骨架 | 3 周 | ✅ 完成 | 契约工程已落地；**OpenAPI 快照 / openapi-diff 于 v1.23 补齐**（M14） |
| S2 身份与授权 | 4 周 | ✅ 完成 | RS256 + JWKS（v1.14）、引擎面 `/api/**` 按 `workflow:*` 收口（v1.15）、业务写入对客户端关闭（v1.16）；跨租户 404、客户端白名单 404、按客户端限流已覆盖；**四条出口用例（跨租户 404 / 密钥轮换双活 / 幂等重放 / 纯服务 submit 403）全部有归属并在 v1.25 复跑全绿** |
| S3 业务标准 | 5 周 | ✅ 完成 | 部署期守卫能拒「选项无映射」BPMN（严格模式）；旧 BPMN 不改一字仍可跑 |
| S4 独立部署 | 4 周 | 🟢 **完成** | M12 外部任务（v1.17）+ 死信重投与告警 + M13 事件投递（v1.18）+ `Platform.Service` 宿主、容器化与端到端演示（v1.19）。含 `serviceTask` 的 BPMN 在独立部署下已实跑通过：worker 作为独立进程拉走任务并推流程，失败则停在原地不动 |
| S5 治理与 SDK | 3 周 | ✅ 完成 | M14 契约治理 ✅ v1.23；**M15 SDK ✅ v1.24**（.NET 手写客户端 + 从快照生成 TS/Java）+ **`CompatibilityTests` ✅ v1.24**（基线打当前服务） |

- [x] S1 契约与骨架
- [ ] S2 身份与授权（剩余见 §3 第 ② 组；RS256 已闭环）
- [x] S3 业务标准
- [x] S4 独立部署
- [x] S5 治理与 SDK（M14 v1.23 + M15 v1.24 + `CompatibilityTests` v1.24）

---

## 2. 改造项 M1–M16（§6）

- [x] **M1 契约层建立**（8 人日）—— `NetBpm.Platform.Contracts` 13 文件、零警告编译；`Rest` 的 DTO 内收
- [x] **M2 多流程路由**（6 人日）
  - [x] 读半边：`IProcessCatalog` + `{processKey}` 路由参数（v1.4，§7.6.1）
  - [x] 写半边：`WorkflowEndpointOptions` / `WorkflowEndpoints` 标 `[Obsolete]` ✅ v1.25。
    **不另造 `PlatformApiOptions`**——平台面的选项就是 `NetBpmHostOptions`：它不含 `ProcessKey`，
    因为平台面按 `{processKey}` 路由，根本没有「当前流程」这个概念。再造一个同名类只会多一份要同步的配置
- [x] **M3 响应强类型**（5 人日）
  - [x] 平台面端点直接返回 `Platform.Contracts` 类型（`workflow/v1` 全族）
  - [x] `Decorate*` 与 `WorkflowEndpoints.cs` 收口 ✅ v1.25：两者一起标 `[Obsolete]` + `#pragma` 收在文件内，
    废弃文案写明替代路径。**「迁到 `/admin/v1`」这个选项被否掉了**：管理面返回的是覆盖行本身，
    不是实例视图——把「实例视图的业务化 DTO」搬到管理面，等于让管理面也长出一套业务形状，问题原样搬家
- [x] **M4 身份三元组**（6 人日）—— `caller/subject/tenant` 落 `RequestContext`，`UserId` 降为兼容别名；Production 禁头身份（v1.3）
- [x] **M5 JWT 升级**（8 人日）—— RS256 + JWKS + 可选外部 IdP（v1.14）：`JwtKeyMaterial` 统一密钥来源、`RemoteJwksProvider` 拉外部公钥集、`GET /.well-known/jwks.json`、校验侧算法白名单；HS256 保留为内网模式
- [x] **M6 审批语义字典**（10 人日）—— 三码分离 + 部署期守卫 + `nb_approval_dictionary`（v1.6）
- [x] **M7 参数 schema**（12 人日）
  - [x] 从 `formProperty` 派生 `ParameterSchema`（v1.5）
  - [x] `netbpm:parameters` 覆盖 `apiName`/`writableBy`/`visibleTo`/约束（v1.13）
  - [x] **写侧强制**：发起按 `APPLICANT`、提交按 `APPROVER` 拒绝，未声明 = 发起可写 / 审批只读（v1.14）
  - [x] `nb_process_parameter` 索引表 ✅ v1.23（按 `definition_version` 物化，旧版本行保留）
- [x] **M8 表单快照**（5 人日）—— `nb_approval_snapshot`，值带定义一起冻住（v1.13）
- [ ] **M9 状态机与聚合**（8 人日）
  - [x] 5 态 `InstanceStatus`（Running/Suspended/Completed/Terminated/…）
  - [ ] `nodeOutcomes` 会签 / 或签聚合（当前为**临时实现**）
- [x] **M10 结论推导上移**（4 人日）—— 三级回落 `DECLARED → LAST_DECISION → FALLBACK`（v1.10）
- [x] **M11 平台存储**（12 人日）
  - [x] 目录 / 字典 / 快照 / 时间线 / 客户端 / 租户 六类存储落地
  - [x] `nb_external_task` ✅ v1.17；`nb_event_subscription` / `nb_event_delivery` ✅ v1.18
  - [x] `nb_process_parameter`、`nb_parameter_snapshot` ✅ v1.23（参数规范按定义版本物化 + 参数修订历史）
  - [x] `nb_idempotency` → 复用引擎的 `ACT_IDEMPOTENCY_`、`nb_audit_log` → 复用既有的 `admin_audit_log`：
    这两张表**刻意不建**（同一个语义两份表，等于给自己制造「哪份才是真的」）
  - [x] 幂等键从「全局」改为「按调用主体」 ✅ v1.19（`ToStorageKey`：caller → subject → tenant 回落）
- [x] **[!] M12 外部任务**（15 人日）—— fetch/complete/failure + 锁 + 死信。**S4 的硬前提**：没有它，含 `serviceTask` 的 BPMN 在独立部署下起不来。✅ **v1.17 已落地**（`activiti:topic` + `nb_external_task` + 三条端点 + 死信）；v1.18 补齐死信重投端点与 `external_task.no_worker` / `external_task.dead_letter` 告警，见开头 v1.18 小节
- [x] **M13 事件对外**（10 人日）—— CloudEvents + 签名 + 重试 + 订阅管理。✅ **v1.18 已落地**
  （`CloudEvent` + HMAC 签名、`CloudEventOutboxHandler`、`nb_event_subscription` / `nb_event_delivery`、订阅管理端点）；剩余：看板与单订阅限速，见开头 v1.18 小节
- [x] **M14 契约治理**（6 人日）—— OpenAPI 快照 + CI diff。**S5 的前置**。✅ v1.23：`contracts/openapi/workflow-v1.json`（22 路径 / 23 操作）+ `OpenApiContractDiff` 破坏性/兼容分类 + CI `contract-governance` job + `contracts/README.md` 治理口径
- [x] **M15 官方 SDK**（10 人日）—— ✅ v1.24：`src/NetBpm.Platform.Client`（.NET，23 个业务操作 + 4 个覆盖行操作，错误统一成 `NetBpmApiException`）
  + `tools/NetBpm.SdkGen` 从快照生成 `sdk/typescript` 与 `sdk/java`（可达闭包 120 → 49 个类型），`SdkGenerationTests` 守一致性
- [x] **M16 客户端注册与隔离**（10 人日）—— `nb_api_client` + `IClientRegistry` + 认证中间件改造、密钥轮换、流程白名单、按 client 配额（v1.3 / v1.4）

---

## 3. 剩余任务（按可并行性分组）

### ① S3 遗留三条尾巴 —— 无强依赖，随时可做

- [x] 实例列表**升序**：引擎两个 Query 新增 `Descending`（排序与时间边界一起下推），平台游标按方向选上界/下界（v1.14）。仍保留「上万量级的租户/白名单内存过滤」问题，需自建实例索引表另做
- [x] `nb_approval_snapshot` 的**清理 / 归档策略**（v1.16）：与历史归档共用 `NetBpm:Admin:ArchiveRetentionDays` 同一个保留期与同一个 cutoff，
      由 `AdminArchiveCleanupService` 在同一循环清理；新增 `ix_nb_approval_snapshot_captured` 支撑按 `captured_at` 删除
- [x] `writableBy` 的**写侧强制**（v1.14，见 §2 M7 与开头三条口径）

### ② S2 欠账 —— 出关前建议补掉

- [x] JWT 的 RS256 + JWKS（外部 IdP 直连）（v1.14）
- [x] 引擎面 `/api/runtime` `/api/identity` `/api/designer` 按 `workflow:*` scope 逐条收紧（v1.15，只对 `AuthMode=Client` 生效；
      管理动作经 `netbpm.admin.* ⇄ workflow:admin:*` 桥接；见开头 v1.15 四条口径）
- [x] 补 S2 出口用例 ✅ v1.20：逐条核对后四条**都已有归属**（跨租户 404 → `JwtAuthenticationTests`；
      密钥轮换期双活 → `ClientAuthenticationTests`；幂等重放 → `SecurityAndIdempotencyTests`；
      纯服务 submit 403 → `TaskSubmitTests`），本轮是**跑通**而不是新写；
      顺带修掉 `Idempotency_ShouldBeScopedToJwtIdentity` 这条在 v1.19 之后实际为红的用例（详见开头 v1.20 小节）

### ③ 写入型端点 —— 属 S4 面

- [x] `POST /instances/{id}/terminate` ✅ v1.21（原因必填 + 乐观并发 + 落时间线，`InstanceTerminationTests` 5 项）
- [x] `POST /tasks/{id}/claim` / `unclaim` / `transfer` ✅ v1.21（三个动作都落时间线，`TaskAssignmentTests` 8 项）
- [x] **前置规矩**：跳转 / 终止 / 变量直写写时间线条目 ✅ v1.21（新增 `INSTANCE_JUMPED` kind；文档点名的「唯一有授权没记录」的口子已补）
- [x] `GET /tasks/{id}/attachments`（只存引用，不做存储）✅ v1.22（复用表单快照，`TaskAttachmentReadTests` 2 项）
- [x] `GET /instances/{id}/diagram.svg`（高亮流转状态图）✅ v1.22（`IInstanceDiagram`，`InstanceDiagramReadTests` 3 项含真库）
- [x] `GET /instances/{id}/parameters`（参数快照）✅ v1.22（声明 + 值合并，`InstanceParameterReadTests` 2 项）
- [x] `GET /processes/{key}/form/schema` ✅ v1.22（`FormSchemaBuilder` 抽出共用，`ProcessFormSchemaReadTests` 2 项）
- [x] `/admin/v1/processes` 写路径 ✅ v1.24（`IProcessCatalogAdmin` + 整行替换 PUT / DELETE，`AdminProcessCatalogTests` 6 项）
- [x] `/admin/v1` 五个只读面 ✅ v1.25（`dictionaries` / `audit` + `audit/facets` / `subscriptions` /
      `events/deadletters` / `quotas`；`AdminReadEndpointsTests` 8 项 + `EventDeadLetterQueryTests` 1 项真库分页）

### ④ S4 独立部署（最大一块）

- [x] **[!]** M12 外部任务 + 表 `nb_external_task` ✅ v1.17；死信重投端点与 `no_worker` / `dead_letter` 告警 ✅ v1.18
- [x] M13 事件投递（CloudEvents + 签名 + 订阅）+ 表 `nb_event_subscription` / `nb_event_delivery` ✅ v1.18
- [x] `Platform.Service` 宿主 + 容器化 + 含 `serviceTask` 的端到端演示 ✅ v1.19（`deploy/Dockerfile` + `docker-compose.yml` + `samples/NetBpm.ExternalWorker`，已实跑）
- [x] 幂等键按调用主体收窄（原「按 client」）✅ v1.19
- [x] 全局（跨实例）配额改 Redis 版 ✅ v1.20（`IRateLimiter` + `RedisFixedWindowRateLimiter`，`NetBpm:RateLimit:Store`；真 Redis 实跑 + 启动自检）
- [x] M11 平台存储收尾：`nb_process_parameter` / `nb_parameter_snapshot` 两张表 ✅ v1.23（`nb_idempotency` → `ACT_IDEMPOTENCY`、`nb_audit_log` → `admin_audit_log`、`nb_external_task` 均已存在）

### ⑤ S5 治理与 SDK

- [x] M14 契约治理：OpenAPI 快照 + CI diff ✅ v1.23（`contracts/` 目录 + `contract-governance` job + 治理文档）
- [x] M15 官方 SDK ✅ v1.24（.NET `Platform.Client` + `tools/NetBpm.SdkGen` → `sdk/typescript`、`sdk/java`）
- [ ] M3 收尾：Decorator 迁移 / 删除，`WorkflowEndpointOptions` 标 `[Obsolete]`
- [ ] 配额与限流看板、审计、SLO、迁移与接入文档（Runbook）

---

## 4. 业务面端点（§5.1 `/workflow/v1`）

| 方法 | 路径 | Scope | 状态 |
|---|---|---|---|
| GET | `/processes` | `workflow:process:read` | [x] v1.4 |
| GET | `/processes/{key}` | 同上 | [x] v1.4 |
| GET | `/processes/{key}/definition` | 同上 | [x] v1.4 |
| GET | `/processes/{key}/versions` | 同上 | [x] v1.4 |
| GET | `/processes/{key}/diagram.svg` | 同上 | [x] v1.4 |
| GET | `/processes/{key}/parameters/schema` | 同上 | [x] v1.5 |
| GET | `/processes/{key}/form/schema` | 同上 | [x] v1.22 |
| POST | `/instances` | `workflow:instance:start` | [x] v1.9（幂等） |
| GET | `/instances` | `workflow:instance:read` | [x] v1.11（cursor） |
| GET | `/instances/{id}` | 同上 | [x] v1.8 |
| GET | `/instances/{id}/history` | `workflow:history:read` | [x] v1.10 |
| GET | `/instances/{id}/diagram.svg` | `workflow:instance:read` | [x] v1.22 |
| GET | `/instances/{id}/parameters` | 同上 | [x] v1.22 |
| GET | `/instances/{id}/parameters/history` | 同上 | [x] v1.23（参数修订历史，`visibleTo` 过滤） |
| POST | `/instances/{id}/terminate` | `workflow:instance:terminate` | [x] v1.21 |
| GET | `/tasks`（`scope=MINE\|CLAIMED\|ALL`；`DONE` 为已办，另需 `history:read`） | `workflow:task:read` | [x] v1.7 / v1.12 |
| GET | `/tasks/{id}` | 同上 | [x] v1.7 |
| GET | `/tasks/{id}/form` | 同上 | [x] v1.13 |
| POST | `/tasks/{id}/submit` | `workflow:task:submit` | [x] v1.8（幂等） |
| POST | `/tasks/{id}/claim` | `workflow:task:assign` | [x] v1.21 |
| POST | `/tasks/{id}/unclaim` | 同上 | [x] v1.21 |
| POST | `/tasks/{id}/transfer` | 同上 | [x] v1.21 |
| GET | `/tasks/{id}/attachments` | `workflow:task:read` | [x] v1.22 |
| ~~GET~~ | ~~/history/tasks~~ | — | [x] v1.12 起**收敛到 `/tasks?scope=DONE`**，不单开历史端点 |

管理面 `/admin/v1`（10 个操作 / 8 条路径，全部就位）：
`/processes` 覆盖行读写 ✅ v1.24（`workflow:admin:process`、RFC 9457、写操作进管理审计）；
`/dictionaries`（`AdminProcess`）、`/audit` + `/audit/facets`、`/quotas`（`AdminClient`）、
`/subscriptions`、`/events/deadletters`（`EventSubscribe`）✅ v1.25，只读。
租户 / 客户端 / 轮换 / 注销仍是 `/api/admin` 的过渡形态（v1.3），写路径未迁：迁之前要先决定
「客户端注册要不要跟着平台面一起做幂等与 RFC 9457」，那是新能力，不是收尾。

---

## 5. 平台表（§7.1）

- [x] `nb_tenant`（一期固定一行 `default`，列不可为空）
- [x] `nb_api_client`（双密钥并存 7 天做轮换）
- [x] `nb_client_process_grant`（独立表，便于按 `process_key` 反查影响面）
- [x] `nb_process_catalog`（目录覆盖层，无行则由 BPMN 派生）
- [x] `nb_approval_dictionary`
- [x] `nb_instance_timeline`（append-only，唯一索引 `(process_instance_id, seq)`）
- [x] `nb_approval_snapshot`
- [x] `nb_process_parameter` ✅ v1.23（主键含 `definition_version`，旧版本行不删）
- [x] `nb_parameter_snapshot` ✅ v1.23（append-only，实例内 revision 单调）
- [ ] `nb_idempotency`（→ 复用 `ACT_IDEMPOTENCY`，不另建表）
- [x] `nb_external_task` ✅ v1.17
- [x] `nb_event_subscription` ✅ v1.18（配套 `nb_event_delivery` 同批落地）
- [ ] `nb_audit_log`（→ 复用 `admin_audit_log`，不另建表）

---

## 6. 守门测试清单（§9）

**契约层**
- [x] `ContractSnapshotTests` ✅ v1.23 —— 提交的 `contracts/openapi/workflow-v1.json` 与代码生成结果一致（归一化后比对，差异按破坏性 / 兼容分类；另有用例防快照「空心化」）
- [x] `ContractSerializationTests` ✅（`PlatformContractTests`）
- [x] `CompatibilityTests` ✅ v1.24 —— 拿 `contracts/baseline/`（上一版承诺）打当前服务：每条路径/方法仍在（运行时探测）+ 19 项 probe 的响应结构仍符合基线 schema。与快照互补：快照管「声明变了没」，它管「声明没变但行为变了没」
- [x] `SdkGenerationTests` ✅ v1.24 —— `sdk/` 产物等于「用当前快照重新生成一遍」（纯度足够，可以逐字节比对）

**语义层**
- [ ] `DecisionVocabularyTests` —— 部分被 `TaskSubmitTests` 覆盖；**会签聚合 `ALL_APPROVED` 未测**
- [x] `SubmitValidationTests` ✅（`TaskSubmitTests`）
- [x] `InstanceConclusionTests` ✅
- [x] `TimelineTests` ✅（`ApprovalTimelineTests`）
- [x] `ParameterSchemaTests` ✅（`InstanceStartTests` + `ParameterOverridesTests`）
- [x] `InstanceListTests` ✅
- [x] `InstanceListCompletedTests` ✅
- [x] `HistoryVisibilityTests` ✅
- [x] `ApprovalSnapshotTests` ✅
- [x] `ParameterOverridesTests` ✅
- [x] `ParticipantIndexTests` ✅
- [x] `DeployGuardTests` ✅（`ApprovalVocabularyTests`）

**安全层**
- [x] `IdentityTests` ✅（`JwtAuthenticationTests` 11：跨租户读 / 写 / 管理动作一律 404；`TaskSubmitTests`：纯服务 submit → 403 `auth.subject_required`）
- [x] `ClientRegistryTests` ✅（`ClientAuthenticationTests` 15：密钥只出现一次 / 伪造密钥 401 / 租户钉死 / **轮换过渡期双密钥可用** / 宽限后旧密钥失效）
- [x] `ClientProcessGrantTests` ✅（`ProcessCatalogTests`）
- [x] `TenantIsolationTests` ✅（`JwtAuthenticationTests`：A 租户查 B 租户实例 → 404 不是 403）
- [x] `QuotaTests` ✅（`RateLimitByClientTests` 4 + `RedisRateLimitTests` 5：全局配额跨副本共享）

**一致性 / 异步 / 性能**
- [x] `IdempotencyTests` ✅（`SecurityAndIdempotencyTests` + `IdempotencyNamespaceTests` + `JwtAuthenticationTests`：
      同键同体回放 `Idempotency-Replayed: true` 且只产生一个实例；同键异体 409；键按调用主体收窄）
- [ ] `DualModeTests` —— 同一 BPMN 在嵌入式与独立服务下 timeline 语义等价
- [x] `ExternalTaskTests` ✅（`ExternalTaskTests` 7 + `ExternalTaskDatabaseTests` 3 + `ExternalTaskDeadLetterRetryTests` 4：锁过期后被另一 worker 取走 / `retries` 耗尽进死信 / 死信重投）
- [x] `WebhookTests` ✅（`CloudEventTests` 12 + `EventSubscriptionEndpointTests` 5 + `EventSubscriptionDatabaseTests` 2：HMAC 签名可验证 / 投递按 `eventId` 去重 / 死信可重放）
- [ ] `LoadTests` —— 读 p95 < 80ms、写 p95 < 200ms（100 并发，MySQL 5.7）

> v1.14 新增：`ParameterWriteTests`（8 项，`writableBy` 写侧）、`JwtRs256Tests`（5 项，RS256 / JWKS / 算法白名单 / 外部公钥集缓存）。
>
> v1.15 新增：`EngineSurfaceScopeTests`（13 项，引擎面 `workflow:*` 收口 / 管理 scope 翻译 / 先 404 再 403 / 旧凭据不受影响）。
>
> v1.16 新增：`ApprovalSnapshotPruneTests`（4 项，快照保留期的内存语义）+ `ApprovalSnapshotTests` 增加一条 MySQL 用例
> （已在 MySQL 5.7.44 / `netbpm_ittest` 实跑通过，见 v1.16 小节的「真库验证」）。
>
> v1.17 新增：`ExternalTaskTopicTests`（6 项，`activiti:topic` 解析 / 往返 / 部署期互斥校验）、
> `ExternalTaskTests`（7 项，内存宿主的外部任务闭环）、`ExternalTaskDatabaseTests`（3 项，MySQL 真库的
> SQL 抢占 / payload 类型保真 / 失败退避与死信）。
>
> 另有已落地但不在 §9 表内的类：`ClientAuthenticationTests`（15 项）、`ProcessCatalogTests`（14 项）、
> `DatabaseScriptTests`（14 项）、`PlatformClientRegistryTests`、`ApprovalVocabularyTests`（8 项）、
> `TaskCatalogTests`、`InstanceCatalogTests`、`InstanceStartTests`、`RateLimitByClientTests`、`HistoryVisibilityTests`。

---

## 7. 八个硬缺口（§1.2）闭环情况

| 缺口 | 状态 | 依据 |
|---|---|---|
| A 两套对外模型并存 | ✅ | `Platform.Contracts` 成为对外唯一真源 |
| B 单流程绑定的宿主式端点 | ✅ | `IProcessCatalog` + `{processKey}` 路由（读半边） |
| C 响应体可变 | ✅ | 平台面强类型；旧 `Decorate*` 于 v1.25 标 `[Obsolete]` 封存，替代路径（覆盖行 + `BusinessKey`）写进标记 |
| D 审批结论魔法数字 | ✅ | 语义字典打破 1/2/3，可声明任意自定义选项 |
| E 参数无 schema | ✅ | schema + `netbpm:parameters` 覆盖 + **写侧按 `writableBy` 拒绝**（v1.14） |
| F 身份只有「一个人」 | ✅ | 三元组 `caller/subject/tenant` |
| G 委托在宿主进程内 | ✅ | **外部任务已落地**（v1.17 / M12）：`activiti:topic` 的 serviceTask 交给 worker 拉走执行，独立部署不再要求把业务代码放进引擎进程。未注入 broker 时执行到该节点显式报错，不静默跳过 |
| H 无契约治理 | ✅ | `/v1` 前缀 + 错误码总表 + **OpenAPI 快照与分类 diff**（v1.23 / M14） |

---

## 8. 明确不做（§10）

1. ❌ 通用 BPMN 全语法（保持现有子集）
2. ❌ 表单引擎 / 表单设计器（参数 schema 只服务校验）
3. ❌ 附件存储（只存引用）
4. ❌ 把 `/api/runtime` 的 `identity-links`/`revision`/`variables` 塞进业务面
5. ❌ SOAP / XML over HTTP（BPMN XML 只作「定义原文」返回）
6. ❌ 在 `v1` 内做语义开关（`?legacy=true`）—— 要兼容就开新主版本

---

## 9. 执行时的硬约束（踩过的坑）

- **红线**：不改 `ACT_*` 表（13 张保留表由 `DatabaseScriptTests` 守着）；需加列走独立幂等 `alter`。
  平台表统一 `nb_` 前缀，由 `tools/NetBpm.DbInit/Schema/SchemaCatalog` 生成 —— **别手改 `db/*.sql`**。
- **无迁移记录表**：新增 `nb_*` 表后老库必须重跑 `DbMigrate migrate` 才补得出来，**缺表只降级不报错**。
- **租户列不可回收**：一期就按 `tenant_id NOT NULL` 建（默认 `default`）。
- 脚本与文档统一 **CRLF + UTF-8 无 BOM**（`.editorconfig`）。
- **测试口径**：涉及 `ACT_HI_*` 的用例（参与者索引、已办、结论）必须跑 **MySQL + 历史宿主**；
  内存宿主 `EnableHistory=false`，会得到「我没审过任何单」这类安静的错误答案。
- **集成测试绝不能不加 `-class` 全跑**（`EngineTableCleaner.Purge` 清库）。
- `ACT_HI_IDENTITYLINK` 的任务级候选行 `PROC_INST_ID_` 为空（候选组只有任务级行），
  按人下推必须 `left join ACT_HI_TASKINST on t.ID_ = l.TASK_ID_` + `coalesce(...)`，否则整类漏。

---

## 10. 建议的下一步顺序

1. ~~先补 **S2 欠账**~~ —— RS256 / JWKS（v1.14）、引擎面 scope 收口（v1.15）、业务写入关闭（v1.16）都已完成。
   **只剩三条出口用例**（幂等重放、纯服务 submit 403、密钥轮换期双活）：它们不加新能力，只把已有行为钉住，
   属于「出关前必须有」而不是「做大功能前必须有」。
2. ~~再清 **S3 三条尾巴**~~ —— 升序与 `writableBy` 写侧（v1.14）、快照保留策略（v1.16）都已完成。
   至此 **S1 / S2 / S3 三条主线的技术项全部落地**，剩下的只是出口用例与 §3 ③ 的写入型端点。
3. ~~现在只剩一个判断：要不要做 **S4**~~ —— 门槛项 **M12 外部任务已在 v1.17 落地**，
   「引擎独立部署给别人用」不再卡在「跑不了 `serviceTask`」上。
   ~~继续 S4 的话，下一步是 `Platform.Service` 容器化 + 端到端演示~~ —— **已在 v1.19 完成并实跑**。
4. ~~回头补 M11 的两张平台表与 Redis 版全局配额~~ —— **全局配额已在 v1.20 完成**（真 Redis 实跑 + 启动自检）；
   ~~§3 ③ 的前置规矩~~ —— **审计断档已在 v1.21 补上**（跳转 / 终止 / 变量直写都写时间线）。
5. ~~§3 ③ 剩下的四个读形态端点~~ —— **已在 v1.22 完成**（`attachments` / 实例 `diagram.svg` / `parameters` / `form/schema`）。
   至此 `/workflow/v1` 的 22 个端点上表全绿，**业务面没有未落地项**。
6. ~~M11 的两张表~~ / ~~M14 契约治理~~ —— **均已在 v1.23 完成**：参数规范按定义版本物化 + 参数修订历史，
   契约快照 + 分类 diff + CI 守门 + 治理文档。
7. ~~M15 官方 SDK / `CompatibilityTests` / `/admin/v1` 写路径~~ —— **均已在 v1.24 完成**。
   至此 S1–S5 五个阶段全部收口，`/workflow/v1` 与 `/admin/v1` 都不再有「说好要做但没做」的端点。
8. ~~M3 的 Decorator 迁移 / 删除、`/admin/v1` 余下五个只读面、S2 的三条出口用例~~ —— **均已在 v1.25 完成**。
   清单里**再没有「说好要做但没做」的项**。
9. 还没做的都属于**新能力**，不再是收尾：**M9 的 `nodeOutcomes` 会签 / 或签聚合**（当前是临时实现，
   只有它还是「部分完成」）、`/admin/v1` 的写路径（字典维护、客户端注册迁入）、
   以及 v1.18 留下的「事件看板与单订阅限速」。

> 若目标只是「让内部系统统一走一套干净 API」，到这里已经可以收口：对外只有 `/workflow/v1` 一套业务语义，
> 引擎原语面对客户端只留读与管理干预，**且管理干预在实例时间线上留痕**。S4/S5 可以整体后移。
>
> ✅ v1.21 之后不再有「有授权但没记录」的口子：跳转 / 终止 / 变量直写 / 认领 / 转办全部落入
> `nb_instance_timeline`，审计可以从一张单的视角完整还原发生了什么。
