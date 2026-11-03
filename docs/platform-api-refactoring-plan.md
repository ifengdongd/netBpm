# NetBpm 平台化改造方案

> **目标**：把 NetBpm 从「可嵌入的流程引擎组件」升级为「**独立的流程服务**」——外部系统不引 DLL、不实现端口、不认识 Activiti，
> 只按一套**公开标准**调 HTTP API，就能拿到流程名称、定义、流程图、待办、审批记录，并能发起与提交审批。
>
> 版本：**v1.14**（2026-11-03 代码 review 收口：参数规范读失败改 503、审计失败可观测、
> `/admin/v1` 组级授权兜底、管理面契约快照、面边界护栏、性能门禁进 CI）｜ 基线：`0.1.3`
> 约束：**不改引擎语义**（BPMN 子集、EL 求值、`ACT_*` 表结构、与 Activiti 的对拍能力全部保留）
>
> **变更记录**
> - v1.0：初稿（现状评估 / 目标架构 / 六个业务标准 / 路线图）
> - v1.1：① 接入形态定为「多系统共用 BPM 中台」（§2.5，租户 + 一系统一客户端）；
>   ② `src/NetBpm.Platform.Contracts` 落地，全文示例按代码实际 JSON 形状重写（附录 D）；
>   ③ 新增 M16 客户端注册（人日 125 → 135，S2 3 周 → 4 周）。
> - v1.2：① `nb_tenant` / `nb_api_client` / `nb_client_process_grant` 进入 `SchemaCatalog`，两方言脚本重新生成（§7.4）；
>   ② `IClientRegistry`（发密钥 / 鉴权 / 轮换 / 白名单）Dapper 与内存两套实现落地；
>   ③ 补 13 项单测 + 3 项真库集成测试 + 脚本守门断言。
> - v1.3：① M4 完成——`RequestContext` 落三元组（`caller` / `subject` / `tenant`），`UserId` 降为兼容别名；
>   ② 鉴权中间件接入 `IClientRegistry`，客户端凭据成为第三类凭据（`AuthMode.Client`），并补三条硬规则（§7.5）；
>   ③ **客户端凭据不再等于管理授权**（`AdminAuthorization` 只对 `None` / 旧 `ApiKey` 放行）；
>   ④ `RequireCredential` 开关 + 启动告警，堵住「注册了客户端但匿名仍可调」的部署坑；
>   ⑤ 接入管理端点（租户 / 客户端 / 轮换 / 注销）落地，密钥终于有地方签发；
>   ⑥ 补 15 项集成用例（全内存宿主，不依赖 MySQL）。
> - v1.4：① S3 第一块落地——`IProcessCatalog` + `/workflow/v1/processes`（名片 / 定义 / 版本 / 流程图），
>   `nb_process_catalog` 作为**覆盖层**（无行则全部由 BPMN 派生，部署即可见）（§7.6.1）；
>   ② 限流从「按 IP」改为「按调用方」（客户端 `quota_per_minute` 生效），并补 `RateLimit-*` / `Retry-After` 头（§7.6.2）；
>   ③ 鉴权保护面从 `/api` 扩到 `/workflow` `/engine` `/admin`——否则新平台面匿名可达；
>   ④ 补 10 项目录集成用例 + 4 项限流用例 + 16 项契约序列化用例。
> - v1.5：① S3 第二块（参数规范）落地——`GET /workflow/v1/processes/{key}/parameters/schema`，
>   从 BPMN `formProperty` 派生 `ParameterSchema`（`apiName=param.{variable}`、类型 / 必填 / 枚举约束）；
>   ② `ProcessCapabilities.HasParameterSchema` 由「派生参数非空」驱动，能力声明闭环（§7.6.4）；
>   ③ 补 5 项参数规范集成用例（含「未声明参数返回空 schema」与「白名单 404」）。
> - v1.6：① S3 第三块（审批语义字典）落地——`nb_approval_dictionary` 表 + `IApprovalVocabulary`（两层合并：
>   默认字典 + 流程声明，未映射一律 `CUSTOM` 不猜）+ 部署期守卫 `ApprovalVocabularyGuard`
>   （宽容告警 / 严格拒绝无映射选项，接入 DesignerEndpoints 部署响应）（§7.6.5）；
>   ② 打破 `ApprovalResult` 的 1/2/3 魔法数耦合——流程可声明任意数量的自定义选项；
>   ③ 补 8 项语义字典集成用例（默认字典解析 / 未知落 CUSTOM / 流程声明覆盖 / 节点级优先 / 守卫三级口径）。
> - v1.7：① S3 第四块（待办读模型）落地——`ITaskCatalog` + `/workflow/v1/tasks`（列表 cursor 分页 / 详情），
>   `TaskView.options` 接入 `IApprovalVocabulary`（调用方按 semantic 渲染按钮），
>   `currentUserCanSubmit` 服务端判定（assignee / 候选用户 / 候选组命中），三码模型的消费端闭环（§7.6.6）；
>   ② 无租户部署视为全局共享，任务租户边界与目录同口径；
>   ③ 补 5 项待办集成用例（options 语义解析 / currentUserCanSubmit / scope 校验 / 白名单 404）。
> - v1.8：① S3 第五块（实例读模型 + 提交审批）落地——`IInstanceCatalog` + `GET /workflow/v1/instances/{id}`，
>   `InstanceView` 作为「实例长什么样」的**单一出口**（实例详情与提交结果共用，杜绝口径分叉）；
>   ② `POST /workflow/v1/tasks/{id}/submit` 消费 `options` 的 422 校验链（选项集 / 语义一致 / 意见 / 目标节点），
>   乐观锁 `expectedRevision` 映射 409，纯服务令牌 403（`auth.subject_required`）（§7.6.7）；
>   ③ 结论推导基础版（`result` → `semantic`，`source=LAST_DECISION`）落在 `InstanceCatalog`，
>   完整三级回落留 §4.7；④ 补 12 项集成用例（实例视图 2 + 提交 8 + MySQL 结束实例结论 2），
>   其中 MySQL 宿主覆盖内存模式覆盖不到的结束实例盲区。
> - v1.9：① S3 第六块（发起端点）落地——`IInstanceStartService` + `POST /workflow/v1/instances`（201 Created + `Location`），
>   闸门（流程存在 / 可发起，404/409）→ 参数镜像与校验（`param.` 剥前缀、保留名冲突 400、必填缺失 422、
>   类型/约束 422）→ 业务主键发号（`IBusinessKeyGenerator`）→ 引擎启动 → 返回 `StartInstanceResult`（`InstanceView` 单一出口）（附录 L）；
>   ② 发起人只来自令牌 `subject`（请求体无 `startedBy` 字段），纯服务令牌发起 `startedBy=null`；
>   ③ 补 7 项发起集成用例（成功发起+视图 / 自动发号 / 传入单号 / 未知流程 404 / 必填缺失 422 且不产生实例 /
>   保留名冲突 400 / 类型不符 422 / 缺 scope 403）。
> - v1.10：① S3 第七块（审批记录时间线）落地——`nb_instance_timeline`（append-only，主键
>   `(process_instance_id, seq)`）+ `IApprovalTimeline` + `GET /workflow/v1/instances/{id}/history`
>   （`?afterSequence=` 增量拉取 / `?limit=` cursor 分页 / `nodeOutcomes` 环节聚合）（§7.6.8、附录 M）；
>   ② **写时定稿**：发起写 `INSTANCE_STARTED`、提交写 `TASK_COMPLETED`（含 `optionId` / `semantic` /
>   `comment` / `actorId` + `actorCallerId` 双身份）、实例结束补 `INSTANCE_COMPLETED`/`TERMINATED`，
>   `submit.timelineDelta` 返回本次新增条目；本能力上线前的老实例首次读取时从 `ACT_HI_ACTINST` **回填一次**；
>   ③ §4.7 **结论三级回落**闭环：`DECLARED`（变量 `netbpm_outcome`）→ `LAST_DECISION`（`result`）→
>   `FALLBACK`（`CLOSED` / 已办结，新增 `ApprovalSemantic.Closed`）；
>   ④ 补 6 项时间线集成用例 + 2 项三级回落 MySQL 用例。
> - v1.11：① S3 第八块（实例列表）落地——`IInstanceListCatalog` + `GET /workflow/v1/instances`
>   （`?processKey` / `?businessKey` / `?status` / `?startedBy` / `?startedFrom` / `?startedTo` /
>   `?cursor` / `?limit`），**运行态与历史双源合并去重**（只用历史会在关闭历史时返回空、只用运行态会漏掉全部已办）（§7.6.9、附录 N）；
>   ② **人员范围由服务端决定**：有真人主体且无跨人权限时不传 `startedBy` 默认收窄到本人、
>   显式查别人 403——「不传就返回全租户」是这类列表最典型也最不会被报 bug 的越权；
>   ③ 状态 / 结论推导抽成 `InstanceReadRules`，列表与详情共用一套判定（杜绝「列表已通过、详情已办结」的分叉）；
>   ④ 顺带修复：全局异常处理器把 Minimal API 的 `BadHttpRequestException` 映射为 400 而不再是 500
>   （参数写错一个值不该得到「服务器内部错误」）；
>   ⑤ 补 11 项列表集成用例（内存宿主 10 + MySQL 已办 1）。
 - v1.12：① S3 第九块（参与者维度）落地——`IHistoricQueryManager.QueryIdentityLinksAsync` 给
   `ACT_HI_IDENTITYLINK` 装上第一个**按人下推**的读口，`IInstanceParticipation` 把
   「发起人 / 审批人 / 候选人（含候选组）」三个来源合成 **`instanceId → ParticipationRole[]`**（附录 O）；
   核心坑：任务级候选关系的 `PROC_INST_ID_` 是空的（候选组更是只有任务级行），必须靠
   `TASK_ID_` 反查 `ACT_HI_TASKINST`，否则「我所在组待批的单据」会被整类漏掉；
   ② 实例列表增 `?involvedUser=`（服务端决定人员范围，查别人 403），并把 `myRoles` 回填到
   `InstanceSummary`——「这条单据在我的哪个列表里」由服务端算；
   ③ 待办端点增 `scope=DONE`（**我的已办**）：已办只能查历史任务（`ACT_RU_TASK` 在任务完成时删行），
   口径与 §5.1 里规划的 `/history/tasks` 等价，收敛到同一个待办端点以免两个列表形状分叉；
   `TaskSummary.CompletedAt` 是「已办」的唯一标识；端点层对它额外要求 `workflow:history:read`；
   ④ 参与者索引六个来源之间是**并集且互不阻断**（任一路失败只告警）——少一路的结果是「少几条单据」，
   而「我的单据打不开」才是真事故；
   ⑤ 补 3 项 MySQL + 历史宿主集成用例（候选人 / 候选组 / 已办 / 越权 403）。
> - v1.13：① **审批记录的人员级可见性**（§4.6 的最后一块）：`IApprovalTimeline.GetHistoryAsync`
>   在实例读模型之上加一层判定——管理 scope / 纯服务令牌看<b>全文</b>，本单的<b>相关人</b>
>   （发起人 / 办理人 / 候选人 / 参与者）看全文去 `actorCallerId`，其余一律按「这张单不存在」处理
>   （404 而不是 403：403 本身就是「它确实存在」的证据）；为此给 `IInstanceParticipation`
>   增 `DescribeAsync`（单实例判定，不去扫这个人名下的全部单据）；
>   ② **已办带回「我当时的结论」**：`IApprovalTimelineStore.ListDecisionsByTaskIdsAsync`
>   按已知 taskId 精确定位 `TASK_COMPLETED`，`TaskSummary.Decision`（optionId / semantic / comment /
>   decidedAt）——「批了什么」只在写时定稿的时间线里，引擎表里永远读不出来；
>   ③ **§4.5 表单快照**落地：新表 `nb_approval_snapshot`（键 = `task_id`，**值带着定义一起冻住**）
>   + `IApprovalSnapshot` + `GET /workflow/v1/tasks/{id}/form`；提交时快照先落、时间线条目再带
>   `formSnapshotTaskId`，历史页按当时的定义渲染而不是按最新版本；写失败不阻断审批；
>   ④ **`netbpm:parameters` 覆盖扩展**：BPMN 转换器把认不出的 `extensionElements`
>   （含平台自己的 `netbpm:*`）原样捕获到 `BaseElement.ExtensionElements`（此前是静默丢弃），
>   目录据此解析 `apiName` / `writableBy` / `visibleTo` / 约束并覆盖 `formProperty` 的派生结果；
>   `visibleTo` 不满足时参数<b>整条消失</b>而不是返回 null，管理视角与纯服务令牌不受限制，
>   未声明 `visibleTo` 的老流程完全不受影响；
>   ⑤ 补 11 项集成用例（可见性 3 + 快照与已办结论 5 + 参数覆盖 3）。
> - v1.14：① **参数规范读失败不再按「无 schema 放行」降级**（§7.6.4 的反面）：
>   `TaskSubmitService` 把「流程没有参数规范」与「规范读不出来」分开——前者返回空规范，
>   后者 `503 parameter.schema_unavailable`；`InstanceStartService` 同步。理由：写权限校验依赖规范，
>   故障期间放行等于静默关掉 `writableBy`，且响应仍是 2xx，事后无法定位这批写入；
>   ② **审计落库失败可观测**：新增 `netbpm.admin.audit.write_failures` 与 page 级告警，
>   并把「动作失败」与「审计写失败」两条规则拆开（原先共用一条，告警名会误导值班方向）；
>   审计仍 fail-open —— 事后抛 503 会诱发对已生效动作（如密钥签发）的重试；
>   ③ **`/admin/v1` 组级授权兜底**：新增 `AdminSurfaceGuardMiddleware`，路径命中且一个管理类 scope
>   都没有时直接 403；端点内细粒度判定不变，作用是「新增端点漏写判定」不再等于裸奔；
>   ④ **管理面契约快照**：`contracts/openapi/admin-v1.json`（22 路径）+ `AdminContractSnapshotTests`。
>   **这不改变「管理面不承诺向后兼容」**：它守的是同仓库控制台与后端的一致性，对外承诺仍只有
>   `/workflow/v1` 的 22 路径 / 23 操作；
>   ⑤ **面边界写成可执行断言**：`SurfaceBoundaryTests` 钉住「管理干预 / 变量直写 / 外部任务只在
>   `/api/**` 且仍按 `workflow:*` 判定」，避免半年后有人把 jump 顺手加到 `/admin/v1`；
>   ⑥ **性能门禁进 CI**：`LoadTests` 从「本地手动跑过一次」改为 nightly 独立 job
>   （独立 `netbpm_load` 库、80/300/200ms 阈值写进 job 环境变量）。

---

## 0. 结论摘要（TL;DR）

1. **不要直接把 `/api/runtime` 开放给第三方。** 它是引擎原语面（`process-instances` / `variables` / `identity-links` /
   `revisions`），概念来自 Activiti 而不是审批业务；开放它等于要求调用方先学一遍 Activiti。它应当降级为
   `engine/v1`（高级/运维通道），业务方只认新的 `workflow/v1`。
2. **真正的阻塞点不是「缺 API」，而是「缺标准」。** 今天审批结论是 `1/2/3` 三个魔法数字（`ApprovalResult`），
   选项从 BPMN `activiti:value` 里现读（`WorkflowFormProperty`），参数没有 schema（`ParaDict` 直接展开成全局变量）。
   外部系统无法据此做稳定集成——**这三件事必须先在规范层定死，再谈暴露**。
3. **`WorkflowEndpointOptions` 是「单流程宿主」的形状**（`ProcessKey` 是单个 `Func`、`Decorate*` 返回 `object?`），
   一个进程只能服务一条流程、响应体可被宿主随意改。独立服务必须换成「**流程目录 + 路由参数 + 强类型响应**」。
4. **独立部署会立刻撞上服务任务委托**（`activiti:class` / `delegateExpression` 在宿主进程内解析，
   `ServiceTaskDelegateGuard` 部署期预解析，缺一个宿主就起不来）。必须提供 **External Task（拉模式）/ Webhook（推模式）**
   替代进程内委托，否则「独立引擎」只能跑纯人工审批。
5. **流程结论当前由宿主委托声明**（`IWorkflowService.CompleteAsync(execution, WorkflowOutcomes.Approved)`），
   引擎不认识审批意见。独立化后没有宿主可声明 → 结论恒空。必须把「结论推导」上移到平台层，由 decision 语义推导。
6. **接入形态已定：多系统共用的 BPM 中台**（三层 `tenant / client / subject`，一系统一客户端一密钥）。
   详见 §2.5——它同时决定了审计字段、幂等键、配额分区与目录过滤的写法，**不能后置**。
7. 交付路线：**契约冻结 → 身份 → 业务标准 → 独立部署 → 治理**，五个阶段串行，每阶段有「不通过不许进下一阶段」的守门测试。
8. **S1 的契约层已经落地**（`src/NetBpm.Platform.Contracts`，13 个文件、零警告编译通过，枚举/字段名实测），
   后续所有示例与实现都以它为准；见附录 D 的清单。

---

## 1. 现状评估

### 1.1 已有资产（不必重造）

| 能力 | 位置 | 说明 |
|---|---|---|
| BPMN 解析/校验/部署 | `NetBpm.Bpmn.*`、`Runtime.DeployProcessCommand` | 含部署期红线校验、版本递增、挂起/激活 |
| EL 表达式 | `NetBpm.El` | `${result == 2}` 等，与 Activiti 对齐（含类型强转） |
| 运行时语义 | `NetBpm.Engine` | Agenda、执行树、会签/串签、边界定时器、异步作业 |
| 持久化 | `NetBpm.Persistence` | 双方言、幂等初始化脚本、事务拦截器、重试 |
| 应用门面 | `NetBpm.Workflow.IWorkflowService` | 发起/查环节/提交/查历史/改派/跳转，六件事已成体系 |
| 运行态原语 API | `NetBpm.Rest/Endpoints/RuntimeEndpoints.*` | 60+ 路由，声明式授权过滤器（`RuntimeAccess`）已就位 |
| 身份 | `NetBpm.Rest/Security` | API Key（SHA256 定长比较）+ JWT HS256 + 租户白名单 |
| 幂等 | `IdempotencyMiddleware` + `IdempotencyStore` | 已有请求指纹与响应回放 |
| 事件出口 | `IEventOutbox` + `OutboxPublisherService` | 事务性发件箱，已具备投递能力（**尚未对外**） |
| 流程图 | `BpmnDiagramSvgRenderer` | 静态图 + 走过的节点高亮 |
| 流程目录雏形 | `workflow_type` 表 + `ProcessTypeStore` | 只见于 Admin / Demo，未进入对外契约 |

**判断**：底层完备度足够支撑平台化，缺的是**契约层与语义层**，不是引擎能力。

### 1.2 八个硬缺口

| # | 缺口 | 代码证据 | 对外后果 |
|---|---|---|---|
| **A** | 两套对外模型并存且互不认识 | `NetBpm.Rest/Contracts.cs`（`ProcessInstanceResponse`/`TaskDetailResponse`）vs `NetBpm.Workflow/Contracts/WorkflowViews.cs`（`WorkflowInstanceView`/`WorkflowTaskView`） | 同一个「流程实例」有两种 JSON 形状、两套字段名；调用方不知道以哪套为准 |
| **B** | 单流程绑定的宿主式端点 | `WorkflowEndpointOptions.ProcessKey` 是 `Func<IServiceProvider, string>`（单值）；Demo 靠 `IBusinessProcess` 注册多个实例绕开 | 一个部署只能服务一条流程；无法「按流程名路由」 |
| **C** | 响应体可变 | `WorkflowEndpointOptions.DecorateInstanceView` 返回 `object?`，端点直接 `Results.Ok(...)` | 不同宿主返回不同 JSON → 无法生成 SDK、无法契约测试 |
| **D** | 审批结论是魔法数字 | `ApprovalResult` 枚举值 = `1/2/3`，与 BPMN `activiti:value/@id` 硬绑定 | 加一种结论（转办、加签、暂存）就要改枚举 + 改所有 BPMN；外部系统无法表达自定义选项 |
| **E** | 参数无 schema、与变量同域 | `ParaDict.ToVariables()` 把参数**原样展开为全局变量**；`ConflictingKeys()` 只能靠「撞名就拒绝」兜底 | 参数拼错静默生效；无法必填校验；无法按节点授权可见性；网关条件被无关参数污染 |
| **F** | 身份只有「一个人」，没有「谁在替谁调」 | `RequestContext` 只有 `UserId`/`TenantId`/`Scopes`；API Key 是**单个全局密钥** | 审批记录只能记「谁」，记不下「哪个系统提交的」；多系统接入只能共用一个密钥 |
| **G** | 服务任务委托在宿主进程内 | `ServiceTaskDelegateGuard` 部署期预解析 `class`/`delegateExpression`；`WorkflowOutcomes` 由宿主委托写 | 独立部署下 BPMN 里只要有一个 `serviceTask`，宿主就**起不来**；流程结论恒空 |
| **H** | 无契约治理 | 无 `/v1` 前缀、无错误码总表、无 OpenAPI 快照、无兼容性守门 | 任意一次字段改动的破坏性只能靠人肉发现 |

### 1.3 一个被低估的问题：三种「结果」被混为一谈

| 概念 | 粒度 | 现状承载 | 问题 |
|---|---|---|---|
| **TaskDecision** 单次办理结论 | 一次任务 | `WorkflowStepRecord.Decision`（选项 id 字符串） | 无「语义」，只有 id |
| **NodeOutcome** 环节聚合结果 | 一个节点（会签/或签） | **无标准表达** | 会签「3 人 2 通过 1 拒绝」的外部表达缺失 |
| **InstanceStatus** 实例状态 | 整个流程 | `Running: bool` + `Result.Outcome: string?` | 只有两态 + 一个自由字符串，无法区分「已终止/已挂起/异常」 |

外部系统做待办箱、审批看板、合规审计，必须同时用到这三层。这是规范设计的第一优先级。

---

## 2. 目标架构

### 2.1 三种部署形态（都保留）

```
形态 1  嵌入式（现状，不动）
        宿主进程 ──引用──> NetBpm.Workflow.dll ──> NetBpm.Engine.dll
        适用：单系统内部审批，追求零网络开销。Demo 继续走这条路。

形态 2  独立服务（本方案新增，**当前按此形态设计**）
        外部系统 A ─┐
        外部系统 B ─┼─HTTPS─> NetBpm.Platform.Service ──> NetBpm.Workflow ──> NetBpm.Engine
        外部系统 C ─┘           （多系统共用一套流程中心 = BPM 中台）
        适用：多系统共用一套流程中心；隔离模型见 §2.5。

形态 3  混合（推荐迁移路径）
        独立服务只暴露 HTTP；原嵌入式宿主改为「薄壳 + 服务调用」，
        先并行跑，按流程逐条切换。
```

**关键决定**：形态 1 与 2 **共用同一个应用层**（`IWorkflowService`）。平台化不是重写，而是给应用层加一层
「契约 + 标准」的外壳。这样嵌入式宿主的既有行为零变化，独立服务也不会长成第二套实现。

### 2.2 分层与依赖方向

```
┌──────────────────────────────────────────────────────────┐
│  L0 契约层   NetBpm.Platform.Contracts                   │
│    纯 POCO DTO + 枚举 + 常量；无依赖、可单独发 NuGet      │
│    对外唯一真源；OpenAPI 由它生成                         │
├──────────────────────────────────────────────────────────┤
│  L1 平台应用层  NetBpm.Platform                          │
│    IProcessCatalog（流程目录）                            │
│    IApprovalVocabulary（审批语义字典）                     │
│    IParameterSchema（参数规范）                           │
│    IExternalTaskBroker（外部任务）                         │
│    → 编排 IWorkflowService，做标准归一与校验              │
├──────────────────────────────────────────────────────────┤
│  L1' 端点层  NetBpm.Platform.Api                         │
│    workflow/v1 · engine/v1 · admin/v1                    │
│    只做 HTTP 形状 + 授权过滤器 + 幂等 + 错误映射          │
├──────────────────────────────────────────────────────────┤
│  L2 应用层（既有）  NetBpm.Workflow.IWorkflowService      │
├──────────────────────────────────────────────────────────┤
│  L3 引擎层（既有，不动）  NetBpm.Engine / El / Bpmn.*     │
└──────────────────────────────────────────────────────────┘
```

**依赖铁律**：`Platform.Contracts` **不得**引用 `Engine` / `Bpmn.Model`。一旦引用，引擎概念（`ExecutionEntity`、
`TaskEntity`）就会漏进公开契约，第一次升级引擎就会破坏外部客户端。

### 2.3 工程切分

| 新工程 | 引用 | 职责 | 规模估计 |
|---|---|---|---|
| `NetBpm.Platform.Contracts` | 无 | 公开 DTO、枚举、错误码、scope 常量 | ~1200 行 |
| `NetBpm.Platform` | Contracts + Workflow + Runtime | 目录/字典/schema/外部任务/结论推导 | ~2000 行 |
| `NetBpm.Platform.Storage` | Platform + Persistence | 平台表 Dapper 存储（双方言） | ~900 行 |
| `NetBpm.Platform.Api` | Platform + Rest | 三组端点 + 过滤器 + OpenAPI 定制 | ~1500 行 |
| `NetBpm.Platform.Service` | Api | 独立宿主（可容器化） | ~150 行 |
| `NetBpm.Platform.Client` | Contracts + HttpClient | .NET 官方 SDK | ~800 行 |

`NetBpm.Rest` 保留（运维/设计器宿主），但**不再作为对外契约的来源**；它的 `Contracts.cs` 逐步内收为
`internal` 或改名为 `*InternalDto`。

### 2.4 一次「提交审批」的请求管道

```
客户端
  │  POST /workflow/v1/tasks/{id}/submit
  │  Authorization: Bearer <user token>
  │  Idempotency-Key: 9f2c…
  ▼
① 认证      → caller / subject / tenant / scopes
② 限流      → 按 caller 分区（不是按 IP）
③ 幂等      → 命中已存在 (caller,key,path,bodyHash) → 回放上次响应
④ 授权      → scope 校验 → 任务/实例级授权（RuntimeAccess 同款过滤器）
⑤ 语义校验  → decision 是否在该节点的选项集内 / 必填意见 / 必填目标节点
⑥ 参数校验  → 按 ParameterSchema 校验类型、必填、可写性
⑦ 应用层    → IWorkflowService.SubmitAsync（既有顺序：先落业务再推进）
⑧ 标准归一  → 引擎结果 → 平台 DTO（状态机、decision 语义、timeline 增量）
⑨ 事件      → outbox 写 task.completed / instance.completed（CloudEvents）
⑩ 响应      → 200 强类型 DTO + ETag + X-Request-Id
```

每一层都**要么全有要么全无**：任何一层失败都不产生副作用（⑤⑥ 在 ⑦ 之前，避免「推进了流程才发现参数不合法」）。

---

### 2.5 接入模型：多系统共用的 BPM 中台（已定）

> **决策（2026-10-30）**：按「一套流程中心服务多个业务系统」设计。**租户 / 客户端两层隔离一期就做，不后置。**
> 理由不是"以后可能要多租户"，而是这两层解决的是当下就会遇到的问题：多个系统共用一个密钥时，
> 一个系统泄漏等于全员停摆、一个系统死循环等于全员被拖慢、出事时分不清是谁在调。

#### 2.5.1 三层组织模型

```
tenant（租户 = 数据隔离边界，落库列为 tenant_id）
  └── client（接入系统 = OAuth 客户端，配额与流程授权边界）
        └── subject（真人 = 待办归属与审批记录的唯一责任人）
```

| 层 | 标识来源（可信） | 隔离什么 | 谁维护 |
|---|---|---|---|
| `tenant` | 令牌 `tenant` 声明 | 流程目录、实例、参数、审批记录、事件订阅 | 平台管理员 |
| `client` | 令牌 `azp` / `client_id` | scope、可用的 `process_key` 白名单、配额、审计主体 | 平台管理员 + 业务系统负责人 |
| `subject` | 令牌 `sub` | 待办归属、审批记录、数据级授权（本人 / 本组 / 本部门） | 企业 IdP |

三条硬规则：

1. **`tenant_id` 不允许为空**。一期只有一个租户时固定写 `default`，而不是允许 `null`——
   没有租户条件的 `WHERE` 是跨租户泄漏最常见的来源，宁可让"忘记传"变成"查不到"。
2. **一个系统一个 client**，禁止多个系统共用密钥。现有单值 `NetBpm:Security:ApiKey` 必须替换。
3. **client 不代表人**。永远不把 `client_id` 写进 `assign_emp_id` / 审批记录办理人字段；
   受理人只能是 `subject`，否则「待办归属」会退化成"某个系统"，人工办理就跑不通了。

#### 2.5.2 客户端注册与密钥生命周期

注册（管理面 `POST /admin/v1/clients`，请求体见 `RegisterClientRequest`）：

```
① 业务系统提申请：系统名 / 租户 / 要用哪些流程 / 接入形态（A 透传 | B 代用户 | C 纯服务）
② 平台登记 client：分配 clientId + scope + process_key 白名单 + 每分钟配额
③ 返回明文 secret（只此一次，库里只存 SHA-256）
④ 联调：沙箱租户跑通「发起 → 待办 → 提交」三条用例
⑤ 上线：状态 Pending → Active；配额与告警按 client 分区分开配
```

| 规则 | 取值 | 说明 |
|---|---|---|
| 密钥存储 | SHA-256 摘要 + `FixedTimeEquals` 比较 | 已有实现可直接复用（`AuthenticationMiddleware.MatchesApiKey`） |
| 有效期 | 默认 90 天 | 到期前 14 天起响应头带 `X-Secret-Expires-In: 13d`，联调期就能看见 |
| 轮换 | 双密钥并存 7 天 | `previous_secret_hash` + `previous_secret_valid_until`，调用方可以灰度切换而不是停机改配置 |
| 吊销 | `status=Revoked` | 立即 401（`auth.client_disabled`），写审计；**不删记录**，否则历史审计断链 |
| 暂停 | `status=Suspended` | 保留密钥、立即 401，用于止血；恢复不需要重发密钥 |
| 配额 | 按 `client_id` 分区漏桶 | 超限 429 + `Retry-After`；**不按 IP**（同一网关出口 IP 会误伤） |

#### 2.5.3 三种接入形态

| 形态 | 令牌 | `caller` | `subject` | 可提交审批 | 典型场景 |
|---|---|---|---|---|---|
| **A 用户令牌透传** | IdP 签发的用户令牌 | `azp` | `sub` | ✅ | 业务系统的审批页（最常用） |
| **B 服务代用户** | 服务令牌 → RFC 8693 Token Exchange | 原服务 `client_id` | 换出的 `sub` | ✅（需 `:onbehalf`） | 后台批量、移动端代提交、机器人助手 |
| **C 纯服务** | `client_credentials` | `client_id` | 无 | ❌ | 只读同步、订阅事件、健康检查 |

```
A：浏览器 ──token──> 业务后端 ──原样透传──> 流程中心        （不改身份，审计最干净）
B：业务后端 ──client_secret + subject_token──> IdP/流程中心  （换出带 act 的用户令牌）
C：业务后端 ──client_secret──> 流程中心                     （没有真人，只能读）
```

#### 2.5.4 隔离矩阵（四层依次短路）

| 层 | 机制 | 失败返回 | 守门用例 |
|---|---|---|---|
| 租户 | 所有查询强制 `tenant_id = 令牌 tenant` | **404**（不是 403） | `TenantIsolationTests` |
| 客户端 | `process_key` 白名单；未授权的流程视为不存在 | **404** | `ClientProcessGrantTests` |
| 数据 | 实例 / 任务归属（发起人、办理人、候选组） | 403 或 404（按是否泄露存在性选择） | `TaskAuthorizationTests`（已有） |
| 动作 | scope（能力上限） | **403** | `ScopeTests` |

统一原则：**任何一层都不接受"调用方自己传对的参数"作为证明**。
现有实现里 `X-User-Id` / `X-Tenant-Id` 在 API Key 分支仍被读取，中台化后必须关闭（见 M4）。

#### 2.5.5 多系统接入后新增的三个约束

这是「中台」与「单系统自用」的真正差别，也是最容易在二期返工的地方：

1. **流程定义变更是跨系统变更**。一条流程被 3 个系统调用时，改一个 `optionId` 或删一个参数
   就是 3 次破坏性发布。约束：已上线流程的选项只允许**追加**；删除 / 改语义必须走新版本定义，
   并在发布前跑 `ProcessContractDiff`（比对流程定义与 `nb_approval_dictionary` 的差集）。
2. **参数 schema 必须独立版本化**。否则 A 系统加一个必填参数，B 系统第二天全量 422。
   已在契约层落地：`ParameterSchema.Version` 与定义版本解耦，只随参数结构变化递增。
3. **配额与熔断按 client 分区**。一个系统的重试风暴不能拖垮全员：
   漏桶限流 + 429 + `Retry-After` + 按 client 的熔断与告警阈值。

#### 2.5.6 接入 Runbook（给对接方的一页纸）

```
1  申请     系统名 / 租户 / 需要哪些流程 / 接入形态  →  拿 clientId + secret + scope 清单
2  探目录   GET /workflow/v1/processes                →  选 processKey
            GET /processes/{key}/parameters/schema    →  拿到参数规范（含必填与类型）
            GET /processes/{key}/diagram.svg          →  拿到流程图（可缓存，按 checksum 失效）
3  发起     POST /workflow/v1/instances（必带 Idempotency-Key）
4  办待办   GET /tasks?scope=Mine → GET /tasks/{id}（拿 options，不要写死按钮）
            POST /tasks/{id}/submit（回传 optionId，建议同时回传 semantic 做一致性校验）
5  回写    订阅事件（CloudEvents）或轮询 GET /instances/{id}（ETag 未变则 304）
```

验收口径：**「发起 → 待办 → 提交」三条用例覆盖了 80% 的接入工作量**，
平台必须为这三条各提供一份可直接运行的 curl / Postman 样例（含错误样例：422 选项不匹配、409 重复提交）。

---

## 3. 对外 API 规范（A：通用）

### 3.1 版本与路径

```
/workflow/v1/**     业务面：流程目录、实例、待办、审批、历史、事件订阅
/engine/v1/**       引擎面：definition 部署、变量、identity-link、迁移、作业（仅高级调用方）
/admin/v1/**        管理面：目录维护、字典维护、审计、配额
/health, /health/live, /health/ready   探针（匿名）
/.well-known/jwks.json                公钥（匿名）
```

- **版本只放主版本**（`v1`）；次版本靠字段可加性体现。
- 破坏性变更 = 新主版本 + 双跑期 + `Deprecation` / `Sunset` 响应头；**不允许**在 `v1` 内改语义。
- 查询参数一律 `camelCase`；枚举值一律 `SCREAMING_SNAKE_CASE`；时间一律 RFC 3339 UTC（`2026-10-30T06:31:42Z`）。

### 3.2 身份：三元组 `caller / subject / tenant`

这是本方案对现有 `RequestContext` 最重要的一次扩展。

| 维度 | 含义 | 来源（可信） | 用途 |
|---|---|---|---|
| `caller` | 调用系统（client_id） | 令牌 `azp` / `client_id` | 审计、配额、限流、故障定位 |
| `subject` | 实际操作人 | 令牌 `sub`，或 token exchange 的 `act.sub` | 待办归属、审批记录、数据授权 |
| `tenant` | 租户 | 令牌 `tenant`（**必须**有签名，不接受请求头） | 数据隔离 |

三种调用模式：

| 模式 | 令牌 | `caller` | `subject` | 允许的操作 |
|---|---|---|---|---|
| **用户直调** | 企业内部 IdP 签发的用户令牌 | `azp` | `sub` | 全部（受数据授权约束） |
| **服务代用户**（推荐） | OAuth2 Token Exchange（RFC 8693）：服务令牌换用户令牌 | `act.sub` 的 client | 换出的 `sub` | 全部 |
| **纯服务** | `client_credentials` | `client_id` | **无** | 只读 + 管理动作；**禁止 submit** |

> **形态已定**：按 §2.5 的中台模型实施——**所有调用方必须先注册 client**，
> 单值 `X-Api-Key` 在 `workflow/v1` 中不再接受（仅作为内网开发开关保留在 `engine/v1`）。
> 因此「一个系统一个密钥、一个系统一套配额、一个系统一份流程白名单」是 v1 的前置条件，不是可选项。

> **为何禁止纯服务提交审批**：审计要求「每一个审批结论都能落到一个真人」。
> 若允许 `client_credentials` 直接 submit，审批记录里的办理人就成了一个系统账号，
> 事后无法追责。真要让系统代批，必须走 token exchange 显式带上 `act.sub`（并有独立的
> `workflow:task:submit:onbehalf` scope，便于风控拉清单）。

**删除**「未配置密钥则放行」与「`X-User-Id` / `X-Tenant-Id` 请求头提权」在**生产模式**下的可用性：
- 保留 `NetBpm:Security:Mode=Development` 显式开关（默认 `Production`）；
- `Production` 下无凭据即 401，请求头身份一律忽略（现在只在 JWT 分支忽略，API Key 与无鉴权分支仍读头）。

### 3.3 Scope 表（对外公开、写进文档与令牌）

| Scope | 允许的操作 |
|---|---|
| `workflow:process:read` | 流程目录、定义、流程图、版本 |
| `workflow:instance:read` | 实例视图、参数、流程图状态 |
| `workflow:instance:start` | 发起流程 |
| `workflow:instance:terminate` | 终止实例（高危） |
| `workflow:task:read` | 待办查询与详情 |
| `workflow:task:submit` | 提交审批 |
| `workflow:task:submit:onbehalf` | 代他人提交（需 token exchange） |
| `workflow:task:assign` | 认领 / 转办 / 改派 |
| `workflow:history:read` | 审批记录与已办 |
| `workflow:history:read:all` | 跨人查看全部历史（审计角色） |
| `workflow:admin:process` | 部署 / 挂起流程定义 |
| `workflow:admin:instance` | 跳转 / 迁移 / 批量干预 |
| `workflow:admin:variables` | 越权读写变量（默认关闭） |
| `workflow:admin:client` | 维护客户端注册、租户、流程授权与配额 |
| `workflow:event:subscribe` | 事件订阅管理 |

规则：
- Scope 是**能力上限**，数据级授权（`RuntimeAccess` 的实例/任务/租户校验）在 scope 之内再做一层，**两层都要过**。
- `workflow:history:read:all` 与 `workflow:admin:*` 必须单独授予，不得随 `workflow:*` 通配。

### 3.4 幂等

| 项 | 规定 |
|---|---|
| 必带幂等键的端点 | `POST /instances`、`POST /tasks/{id}/submit`、所有 `*/batch/*`、所有 `admin` 变更 |
| 键来源 | 客户端生成 UUIDv7（契约里建议，不强制格式） |
| 指纹 | `(caller, idempotencyKey, method, normalizedPath)` + `SHA256(body)` |
| 行为 | 键相同且 body 相同 → **原样回放**（含状态码，带 `Idempotent-Replay: true`）；键相同但 body 不同 → `409 conflict.idempotency_key_reused` |
| 窗口 | 24h（现 `IdempotencyCleanupService` 需从「短窗口」调到可配置） |

### 3.5 分页

- 统一 `?limit=`（默认 20，上限 200）+ `?cursor=`（不透明串，服务端实现）。
- 响应头：`X-Total-Count`（可选，聚合查询可省略）+ `Link: <...>; rel="next"`。
- **不用 offset**：`firstResult` 在并发新增下会漏行/重行；`WorkflowEndpoints.FindInstancesAsync` 现有的
  offset 分页保留为内部兼容，对外只暴露 cursor。

### 3.6 错误模型

用 **RFC 9457 Problem Details**，不用自定义 `{code,msg,data}` 信封：

```json
{
  "type": "https://netbpm.dev/errors/task-already-completed",
  "title": "待办任务已完成",
  "status": 409,
  "code": "task.already_completed",
  "detail": "任务 8f2c… 已被 zhangsan 于 2026-10-30T06:12:00Z 完成。",
  "instance": "/workflow/v1/tasks/8f2c…/submit",
  "requestId": "01JB…",
  "errors": [{ "field": "decision", "code": "decision.not_in_option_set" }]
}
```

错误码命名：`<领域>.<原因>`，全小写下划线。**码表见附录 B**，随契约一起版本化。

状态码纪律（沿用现有约定，写进文档）：
`400` 请求不合法 ｜ `401` 未认证 ｜ `403` 已认证但无权 ｜ `404` 不存在**或跨租户**（不泄露存在性）｜
`409` 状态冲突/幂等冲突/乐观并发 ｜ `412` `If-Match` 失败 ｜ `422` 语义校验失败（如 decision 不在选项集）｜
`429` 限流 ｜ `503` 依赖不可用。

### 3.7 响应头

| 头 | 说明 |
|---|---|
| `X-Request-Id` | 服务端 traceId，日志与审计可查 |
| `ETag` | 实例/任务视图的版本（对齐引擎 `revision`），支持 `If-None-Match` 与 `If-Match` 乐观并发 |
| `RateLimit-Limit/Remaining/Reset` | 配额可见，避免调用方盲重试 |
| `Deprecation` / `Sunset` | 弃用通告 |

---

## 4. 对外 API 规范（B：业务标准）

> 这一节是本方案的核心交付物：**四个标准 + 一个目录**。
> 设计原则一句话：**对外分层、对内扁平**——为了让 BPMN 与 EL 保持 Activiti 兼容，引擎里的变量仍是扁平的、
> 审批选项 id 仍是 `"1"/"2"/"3"`；但**在 API 边界上**把它们分区、命名、加语义。**绝不为了 API 漂亮去动引擎语义。**

### 4.1 标准一：流程目录（Process Descriptor）

现在流程名片只存在于 Demo 的 `ProcessInfoEnum`（`samples/NetBpm.Demo/Common/ProcessInfo.cs`）与 Admin 的
`workflow_type` 表，**且不在任何对外契约里**。平台要把「流程目录」变成一等公民。

> **契约已落地**：本节所有示例与 `src/NetBpm.Platform.Contracts` 的 `ProcessDescriptor` 一一对应，
> 以代码为准（示例即 `JsonSerializer` 的真实输出形状：枚举 `SCREAMING_SNAKE_CASE`，属性 `camelCase`）。

```jsonc
// GET /workflow/v1/processes?status=ACTIVE&category=HR
{
  "items": [
    {
      "processKey": "leave_process",
      "name": "请假申请",
      "category": "HR",
      "description": "员工请假审批（组长 → 经理）",
      "icon": "https://cdn…/leave.svg",
      "pageUrls": {                              // 平台只做流程不做页面：跳转地址由流程目录声明
        "start":   "/hr/leave/new",
        "view":    "/bill.html?businessId={businessKey}",
        "approve": "/approve.html?taskId={taskId}"
      },
      "status": "ACTIVE",                        // DRAFT | ACTIVE | SUSPENDED | RETIRED
      "deployedVersion": 7,
      "definitionChecksum": "sha256:9f2c…",       // 流程图 / 定义原文的缓存键
      "parameterSchemaVersion": 3,
      "entryPoints": [
        { "key": "normal", "name": "正常请假", "startActivityId": "startEvent1", "defaultParameters": null }
      ],
      "labels": null
    }
  ],
  "nextCursor": null,
  "hasMore": false
}
```

要点：

- `processKey` 是**对外稳定标识**（= BPMN `process/@id`），`processDefinitionId` 是版本快照，两者不可混用。
- `pageUrls` 让平台「只做流程、不做页面」——业务页面地址由流程目录声明，审批中心与消息卡片按它跳转。
  这正是 Demo 里 `ProcessInfo.ViewUrl/ApproveUrl` 的通用化，且升级成了「一流程多入口」（`entryPoints`）。
- `definitionChecksum` 替代「按版本号猜是否变化」：定义内容一变（哪怕版本号没动）缓存自动失效。
- 目录**已按客户端白名单过滤**：调用方看不到自己没被授权的流程，不需要自己判断能不能用。

### 4.2 标准二：审批选项与决定（Approval Vocabulary）

#### 4.2.1 双码模型（本方案最关键的设计决定）

现在的 `ApprovalResult` 把三件事焊死在一起：枚举值、BPMN 选项 id、中文名。

```
现状：ApprovalResult.Approved = 1  ⇄  <activiti:value id="1" name="通过"/>  ⇄  变量 result = "1"
      加一种「转办」→ 改枚举 → 改所有 BPMN → 改所有网关条件 → 历史数据语义漂移
```

改成 **三码分离**：

| 码 | 取值示例 | 谁定义 | 稳定性 | 用途 |
|---|---|---|---|---|
| `optionId` | `"1"` / `"pass"` / `"refund_to_finance"` | **流程定义**（`activiti:value/@id`） | 随流程版本变 | 落变量、网关条件、历史还原 |
| `semantic` | `APPROVE` / `REJECT` / `RETURN` / `CUSTOM` | **平台字典** + 流程声明 | 平台级稳定 | 调用方分支逻辑、统计、结论推导 |
| `outcomeClass` | `POSITIVE` / `NEGATIVE` / `NEUTRAL` | 由 `semantic` 推导 | 平台级稳定 | 「这单最后是过还是不过」的统一判定 |

```jsonc
// GET /workflow/v1/tasks/{taskId} 片段（TaskView.options）
"options": [
  { "optionId": "1", "label": "通过", "semantic": "APPROVE", "outcomeClass": "POSITIVE",
    "requiresComment": false, "requiresTargetNode": false,
    "allowedTargetNodes": null, "commentPrompt": null, "attributes": null },
  { "optionId": "3", "label": "驳回修改", "semantic": "RETURN", "outcomeClass": "NEGATIVE",
    "requiresComment": true, "requiresTargetNode": true,
    "allowedTargetNodes": ["task_apply"], "commentPrompt": "请说明需要补充什么", "attributes": null },
  { "optionId": "9", "label": "转财务复核", "semantic": "CUSTOM", "outcomeClass": "NEUTRAL",
    "requiresComment": true, "requiresTargetNode": false,
    "allowedTargetNodes": null, "commentPrompt": null,
    "attributes": { "requiresAssignee": "true" } }   // 流程自定义属性，调用方可忽略
]
```

- 调用方只按 `semantic` / `outcomeClass` 做逻辑，**永远不按 `optionId` 硬编码**。
- 平台只保证 4 个核心 `semantic`（`APPROVE`/`REJECT`/`RETURN`/`CUSTOM`）；流程可以声明任意数量的
  `CUSTOM` 选项，调用方按不透明码透传即可（契约里标 `x-extensible-enum`）。
- **部署期守门**：BPMN 里每个 `approvalResult` 的 `activiti:value` 必须在平台字典或流程声明里有映射，
  缺一个就**拒绝部署**（扩展现有的 `ApprovalFormGuard`）。这是「标准可执行」而非「标准靠自觉」的关键。
- **兼容**：`result` 变量仍写 `optionId` 字符串，`${result == 2}` 照样成立。语义映射只在平台层。

#### 4.2.2 提交审批的请求体

```jsonc
// POST /workflow/v1/tasks/{taskId}/submit   （= SubmitTaskRequest，扁平结构）
{
  "optionId": "3",
  "semantic": "RETURN",                         // 可选：服务端以 optionId 为准，不一致返回 422
  "comment": "金额与发票不符，请补充",
  "targetNodeId": "task_apply",                 // requiresTargetNode=true 时必填
  "parameters": { "param.costCenter": "R&D-01" },   // 走 schema 校验（必填 / 类型 / 可写性）
  "formValues": { "days": "3", "reason": "年假" },  // 键 = 字段 id，仅存档
  "expectedRevision": 12,                       // 可选乐观并发（对齐引擎 revision）
  "attachments": [
    { "name": "发票.pdf", "url": "https://…", "size": 81234, "contentType": "application/pdf", "sha256": null }
  ]
}
```

- **扁平而不是嵌套 `decision` 对象**：少一层嵌套，SDK 生成与手写 curl 都更省事；
  语义分组靠字段名前缀（`optionId`/`semantic`/`comment`/`targetNodeId` 属于决定本身）。
- `attachments` 只存**引用**（URL + 元数据），不存二进制。附件本体走调用方自己的对象存储，
  平台只在审批记录里留引用——避免平台变成文件服务器。
- `expectedRevision` 对齐 `CompleteTaskRequest` 已有的并发校验，缺省不校验（向后兼容）。

### 4.3 标准三：流程参数（Parameter Schema）

#### 4.3.1 现状问题

`ParaDict` 直接展开为**全局流程变量**，与 `BaseModel` 的 8 个变量同域：

```csharp
// ParaDict.ToVariables()：键名原样成为变量名
variables[name] = value;
// 唯一的防线是「撞名就拒绝」
public IReadOnlyList<string> ConflictingKeys() => Values.Keys.Where(BaseModelVariables.All.Contains);
```

后果：拼错的参数名静默成为变量（`day` vs `days`，流程照跑、网关走默认流）；没有类型/必填校验；
没有「这个参数谁能看、谁能改」的概念；参数表与变量表是两份数据但语义重叠。

#### 4.3.2 三层命名空间（对外分层，对内仍扁平）

| 层 | 前缀 | 例子 | 可写 | 可读 | 进 EL？ |
|---|---|---|---|---|---|
| 系统保留 | `sys.` | `sys.tenantId`、`sys.startedAt`、`sys.currentNodeId` | ❌ | ✅ | ❌ |
| 流程参数 | `param.` | `param.days`、`param.costCenter` | 按 schema | 按 schema | ✅（写入时以裸名镜像） |
| 表单值 | `form.` | `form.approvalResult`、`form.comment` | 提交时 | 按 schema | ✅（同上） |
| 引擎变量 | `var.` | `var.result`、`var.applyUser` | 仅引擎/高权限 | 需 `engine` scope | ✅ |

**镜像规则**（兼容性的核心）：写入 `param.days = "3"` 时，平台**同时**在引擎里写变量 `days = "3"`。
于是 BPMN 里 `conditionExpression="${days > 3}"` 不用改一个字，而 API 侧拿到了命名空间与 schema。
读取时以 `param.` 为准，`var.` 默认不返回（除非 `?include=engineVariables` 且持 scope）。

#### 4.3.3 参数声明

在 BPMN 的 `process` 上加扩展元素（新增，可选；不写则按「无 schema」放行，保持旧流程可用）：

```xml
<bpmn:extensionElements>
  <netbpm:parameters>
    <netbpm:parameter name="days" type="long" required="true"
                      writableBy="APPLICANT" visibleTo="APPLICANT,APPROVER"
                      min="0.5" max="365" description="请假天数" />
    <netbpm:parameter name="costCenter" type="string" required="false"
                      apiName="param.costCenter"
                      writableBy="APPROVER" visibleTo="APPROVER,ADMIN"
                      pattern="^[A-Z]{2,4}-\d{2}$" description="成本中心" />
  </netbpm:parameters>
</bpmn:extensionElements>
```

| 属性 | 取值 | 说明 |
|---|---|---|
| `name` | 变量名 | **必须与 BPMN 里用的变量名一致**（这是 EL 兼容的根） |
| `apiName` | 可选，默认 `param.{name}` | 对外参数名；改它不影响 EL，只影响 API 调用方 |
| `type` | `string`/`long`/`decimal`/`boolean`/`date`/`dateTime`/`enum`/`json` | 校验与序列化依据（枚举值见 `ParameterType`） |
| `required` | `true`/`false` | 缺失即 422（**在推进流程之前**） |
| `writableBy` | `APPLICANT`/`APPROVER`/`ADMIN`/`NONE` | 按角色判定，不按请求体自述 |
| `visibleTo` | 逗号分隔角色 | 不满足则**字段不出现在响应里**（而不是返回 null） |
| `min`/`max`/`minLength`/`maxLength`/`pattern`/`enumValues` | | 校验规则（对应 `ParameterConstraints`） |

对外暴露：`GET /workflow/v1/processes/{key}/parameters/schema`，返回 `ParameterSchema`（带 `version`）。
**不返回 JSON Schema 2020-12**：外部系统需要的是「字段名 + 类型 + 必填 + 约束」，
套一层 JSON Schema 反而让 Java/Go 的调用方要额外引依赖才能解析。

> **明确定位**：NetBpm 不做表单引擎（README 已声明）。参数 schema **只服务于校验与契约**，
> 不负责渲染。渲染永远在调用方。

### 4.4 标准四：待办与实例视图

#### 4.4.1 状态机（对外）

```
实例：  RUNNING ──suspend──> SUSPENDED ──activate──> RUNNING
           │                     │
           ├── complete ────────> COMPLETED
           ├── terminate ───────> TERMINATED
           └── incident ────────> FAILED  ──retry/resolve──> RUNNING

任务：  CREATED ──claim──> CLAIMED ──transfer──> CLAIMED（换人）
           │                  │
           └── complete ──────┴──> COMPLETED（任务行消失，只剩时间线条目）
                              └── 实例终止/跳转 ──> CANCELED（任务行消失，不留状态）
```

任务**不设独立状态字段**（契约里没有 `TaskStatus`）：调用方能观测到的只有三种事实——
「任务存在且我能办」「任务存在但我不能办」「任务不存在（已办结 / 已取消）」，分别由
`TaskView.currentUserCanSubmit` 与 404/409 表达。多造一个状态枚举只会制造与引擎不一致的第二真源。

现状 `Running: bool` + `Result.Outcome: string?` 只能表达两态，且 `WorkflowOutcomes.Closed/Running`
混用了「结论」与「状态」两个概念。对外必须分成三个字段：

```jsonc
// GET /workflow/v1/instances/{id}   （= InstanceView，字段与代码逐一对应）
{
  "instanceId": "1001",
  "processKey": "leave_process",
  "processDefinitionId": "leave_process:7:812",
  "definitionVersion": 7,
  "businessKey": "L20261030001",
  "title": "张三的请假申请（3 天）",        // 服务端按 summaryTemplate 渲染
  "status": "RUNNING",                     // RUNNING | COMPLETED | TERMINATED | SUSPENDED | FAILED
  "startedAt": "2026-10-30T02:00:00Z",
  "startedBy": "zhangsan",
  "endedAt": null,
  "currentActivityIds": ["task_leader_approve"],
  "revision": 12,                          // 与 ETag 同源，用于乐观并发
  "tasks": [ /* TaskView[]，见 4.4.2 */ ],
  "parameters": {
    "instanceId": "1001",
    "revision": 3,
    "values": { "param.days": "3", "param.reason": "年假" },   // 只含 visibleTo 允许的
    "updatedAt": "2026-10-30T02:00:00Z",
    "updatedBy": "zhangsan"
  },
  "nodeOutcomes": [                        // 环节聚合（会签口径），见 4.6
    { "activityId": "task_apply", "activityName": "申请", "aggregation": "SINGLE",
      "satisfied": true, "total": 1, "positive": 1, "negative": 0, "pending": 0,
      "decidedAt": "2026-10-30T02:00:05Z" }
  ],
  "conclusion": null                       // 结束才有：{ "semantic":"APPROVE", "optionId":"1", "label":"已通过", "source":"LAST_DECISION" }
}
```

三个概念必须在响应里分得开（现状混在一起：`Running: bool` + `Result.Outcome: string?`）：

| 概念 | 字段 | 粒度 |
|---|---|---|
| 单次办理结论 | `TaskView.options` → 提交时回传 `optionId` | 一次任务 |
| 环节聚合结果 | `nodeOutcomes[]` | 一个节点（会签 / 或签） |
| 实例状态与结论 | `status` + `conclusion` | 整个流程 |

**能力声明替代 `actions` 数组**：调用方不再收到一个猜出来的动作列表，而是拿到
`tasks[].options`（能不能点、点了会怎样都由它决定）与 `tasks[].currentUserCanSubmit`
（服务端已判定当前主体能否办理）。这比 `actions` 更可靠：`actions` 只能猜，猜错了就是一次 403。

#### 4.4.2 待办视图

```jsonc
// GET /workflow/v1/tasks?scope=MINE&limit=20    （列表元素 = TaskSummary）
{
  "items": [{
    "taskId": "8f2c…",
    "instanceId": "1001",
    "processKey": "leave_process",
    "processName": "请假申请",              // ← 现在要二次查询才能拿到
    "businessKey": "L20261030001",
    "title": "张三的请假申请（3 天）",        // ← 由 schema 的 summaryTemplate 渲染
    "activityName": "组长审批",
    "assignee": null,
    "createdAt": "2026-10-30T02:00:05Z",
    "dueAt": null,
    "startedBy": "zhangsan",
    "summary": "张三 请假 3 天",
    "entryUrl": "/approve.html?taskId=8f2c…"
  }],
  "nextCursor": "eyJpZCI6…",
  "hasMore": true
}
```

```jsonc
// GET /workflow/v1/tasks/{id}    （= TaskView，手上有它就能渲染整个审批页）
{
  "taskId": "8f2c…",
  "instanceId": "1001",
  "activityId": "task_leader_approve",
  "activityName": "组长审批",
  "assignee": null,
  "candidateUsers": [],
  "candidateGroups": ["leaders"],
  "createdAt": "2026-10-30T02:00:05Z",
  "dueAt": null,
  "options": [ /* 见 4.2.1，调用方按 semantic 渲染按钮 */ ],
  "form": [ /* FormField[]，见 4.5；无表单时为 null */ ],
  "currentUserCanSubmit": true             // 服务端已判定，客户端不再自己猜
}
```

要点：
- `processName` / `title` / `entryUrl` 让待办中心**不需要认识任何流程**（现在 Demo 靠前端硬编码 `process` 参数）。
- `title` 走流程目录里的 `summaryTemplate`（如 `"{applicant}的{processName}（{param.days} 天）"`），
  模板求值在平台层做，缺字段降级为 `processName + businessKey`。
- `scope` 三值（`MINE` / `CLAIMED` / `ALL`）替代 `assignee=me`：`ALL` 需管理权限，
  服务端按 scope 判定，**不靠调用方传参自觉**。

### 4.5 标准五：表单 Schema 与快照

现有 `WorkflowFormProperty` 只透传 `activiti:formProperty`，够用但不完整：没有 `readonly`、
没有条件显示、没有版本快照。而**流程定义会升级**，实例却停在旧版本上——这时若按新 schema
渲染旧实例，字段就对不上了。

规定：
1. `GET /tasks/{id}` 返回该任务**所属定义版本**的 schema（`FormSchema`，随定义走，不随流程目录走）。
2. **提交时的表单值 + schema 一起快照入库**（`nb_approval_snapshot`，键 = `task_id`），
   历史详情页永远按当时的 schema 渲染，不随定义漂移。条目里只带 `formSnapshotTaskId`，快照按需再拉。
3. `FormField` 相对 `FormProperty` 增加三个字段：`readOnly`、`sortOrder`、`file` 类型（只存引用）；
   **不做** `visibleWhen` 条件显示——条件渲染属于表单引擎，本方案明确不做（见 §10「明确不做」）。

> ✅ **已落地（v1.13）**：快照端（`nb_approval_snapshot` + `IApprovalSnapshot` +
> `GET /workflow/v1/tasks/{id}/form` + 提交时冻住「定义 + 值」）见 §7.6.11 与附录 P。
> ~~仍未做的两条~~：
> `GET /processes/{key}/form/schema` ✅ 已于 v1.22 交付（`ProcessCatalog.GetFormSchemaAsync`）。
> `GET /tasks/{id}` 的 schema 按定义版本 ✅ 已满足 —— `TaskCatalog.ResolveUserTaskAsync` 用
> 任务上的 `ProcessDefinitionId` 反查模型（`_engine.GetBpmnModelAsync`），取到的就是**该待办所属定义版本**的
> `userTask`，不是目录里当前生效的版本。

### 4.6 标准六：审批记录（Append-only Timeline）

> ✅ **已落地**（v1.10）：实现见 §7.6.8 与附录 M（表 `nb_instance_timeline` + `IApprovalTimeline` +
> `GET /workflow/v1/instances/{id}/history`）。

```jsonc
// GET /workflow/v1/instances/{id}/history?afterSequence=0&limit=100   （= HistoryView）
{
  "instanceId": "1001",
  "processKey": "leave_process",
  "businessKey": "L20261030001",
  "status": "COMPLETED",
  "startedAt": "2026-10-30T02:00:00Z",
  "startedBy": "zhangsan",
  "endedAt": "2026-10-30T02:10:00Z",
  "durationInMilliseconds": 600000,
  "entries": [
    { "sequence": 1, "kind": "INSTANCE_STARTED",
      "activityId": null, "activityName": null, "taskId": null,
      "actorId": "zhangsan", "actorCallerId": "hr-system",
      "optionId": null, "semantic": null, "comment": null, "targetNodeId": null,
      "formSnapshotTaskId": null, "occurredAt": "2026-10-30T02:00:00Z" },

    { "sequence": 2, "kind": "TASK_COMPLETED",
      "activityId": "task_leader_approve", "activityName": "组长审批", "taskId": "8f2c…",
      "actorId": "lisi", "actorCallerId": "oa-portal",
      "optionId": "1", "semantic": "APPROVE", "comment": "同意", "targetNodeId": null,
      "formSnapshotTaskId": "8f2c…", "occurredAt": "2026-10-30T02:10:00Z" }
  ],
  "nextCursor": null,
  "nodeOutcomes": [
    { "activityId": "task_leader_approve", "activityName": "组长审批",
      "aggregation": "SINGLE", "satisfied": true,
      "total": 1, "positive": 1, "negative": 0, "pending": 0,
      "decidedAt": "2026-10-30T02:10:00Z" }
  ],
  "conclusion": { "semantic": "APPROVE", "optionId": "1", "label": "已通过", "source": "LAST_DECISION" }
}
```

关键规定：
1. **append-only + 单调 `sequence`**。跳转 / 终止 / 转办也写条目，不删不覆盖；
   增量拉取用 `?afterSequence=`（比时间戳可靠：同一秒可能有多条）。
2. `actorId`（真人）与 `actorCallerId`（提交它的系统）**必须并列**（缺口 F 的落点）。
   纯服务令牌产生的条目 `actorId` 为空，合规报表要单列。
3. `nodeOutcomes` 显式表达会签 / 或签聚合（现状完全没有）。聚合口径来自 BPMN 多实例配置：
   `SINGLE` / `ALL_APPROVED` / `ANY_APPROVED` / `MAJORITY`。
4. 数据来源仍走现有「双源单出口」（`EngineHistory` 权威 + `BusinessApprovals` 合成），
   但**出口收敛到 `IApprovalTimeline`**，端点不再自己拼。
5. 表单不做内联（不再把 `formSnapshot` 塞进条目）：条目只带 `formSnapshotTaskId`，
   需要时再拉 `/tasks/{id}/form`。理由是历史页通常只渲染当前选中的那一屏，内联会把响应放大 5~10 倍。

### 4.7 结论推导上移（解决缺口 G 的一半）

> ✅ **已落地**（v1.10）：三级回落见 §7.6.8；第一级的运行时落点是变量 `netbpm_outcome`
> （BPMN `<netbpm:outcome>` 解析器尚未支持，但读侧已就位）。

现状：结论由宿主在服务任务里声明（`IWorkflowService.CompleteAsync(execution, WorkflowOutcomes.Approved)`）。
独立部署后没有宿主 → 结论恒空。

改为**三级回落**：
1. 流程显式声明（`<netbpm:outcome on="PATTERN" semantic="APPROVE"/>`）——最精确；
2. 由**最后一个被完成的审批节点的 `semantic`/`outcomeClass`** 推导——覆盖 90% 场景；
3. 兜底 `CLOSED`（人工干预办结）。

`WorkflowOutcomes` 保留为兼容层，但不再是对外契约的一部分。

### 4.8 事件与回调（对外）

复用现有 `IEventOutbox`，只是把它接到公网。

- 信封：**CloudEvents 1.0**（`application/cloudevents+json`）。
- 事件类型：`netbpm.instance.started` / `netbpm.instance.completed` / `netbpm.instance.terminated` /
  `netbpm.task.created` / `netbpm.task.assigned` / `netbpm.task.completed`。
- 签名：`X-NetBpm-Signature: t=<ts>,v1=<HMAC-SHA256(ts + "." + body)>`，5 分钟时钟容忍。
- 投递：at-least-once + 指数退避（1s→2s→…→1h，最多 12 次）+ 死信（`admin/v1/events/deadletters` 可重放）。
- 幂等要求写进文档：订阅方必须按 `eventId` 去重。
- 订阅管理：`POST /admin/v1/subscriptions`（`url`、`eventTypes`、`processKeys`、`secret`）。

### 4.9 外部任务（External Task）—— 独立部署的**前置条件**

这是本方案**优先级最高的工程项**（缺口 G）。没有它，独立引擎只能跑纯人工流程。

```
引擎（serviceTask 标记 external）
   │  创建 external task（ACT_RU_TASK 旁挂，或新表 nb_external_task）
   ▼
工作者（调用方系统）
   │  POST /engine/v1/external-tasks/fetch   { workerId, topic, maxTasks, timeout }
   │  ← 长轮询返回任务 + 变量快照 + lockExpiration
   │  ... 业务处理 ...
   └─ POST /engine/v1/external-tasks/{id}/complete  { variables, ... }  或 /failure
```

设计要点：
- `topic` 来自 BPMN `activiti:topic`，取代 `activiti:class`；**部署期**校验「每个 serviceTask 要么有
  `class`/`delegateExpression`（嵌入式宿主），要么有 `topic`（独立服务），否则拒绝部署」。
- 锁 + 可见性超时（复用 `Hosting/JobQueue` 的租约模型与 `LeaseSeconds` 语义）。
- `failure` 支持 `retries` / `retryTimeout` / `errorMessage`；耗尽进死信，可由管理端点重试。
- 与既有 `IAsyncJobManager` 的关系：**不复用**。异步作业是「引擎内部延迟执行」，外部任务是
  「等外部系统执行」，生命周期与重试语义都不同；共用一套表会让两侧互相污染。

---

## 5. 对外 API 清单

### 5.1 `/workflow/v1`（业务面）

> ✅ = 已落地（见各版本变更记录与 §7.6）：目录（v1.4）、参数规范（v1.5）、待办（v1.7）、
> 实例视图与提交审批（v1.8）、发起（v1.9）、审批记录时间线（v1.10）、实例列表（v1.11）、
> 参与者维度（v1.12，见 §7.6.10）、表单快照与参数覆盖（v1.13，见 §7.6.11）。

| 方法 | 路径 | 语义 | 幂等 | Scope |
|---|---|---|---|---|
| GET | `/processes` | 流程目录 | — | `workflow:process:read` | ✅
| GET | `/processes/{key}` | 流程名片 | — | 同上 | ✅
| GET | `/processes/{key}/definition` | 当前定义（XML + 校验和） | — | 同上 | ✅
| GET | `/processes/{key}/versions` | 历史版本列表 | — | 同上 | ✅
| GET | `/processes/{key}/diagram.svg` | 静态流程图（`?version=`） | — | 同上 | ✅
| GET | `/processes/{key}/parameters/schema` | 参数规范 | — | 同上 | ✅
| GET | `/processes/{key}/form/schema` | 表单规范 | — | 同上 |
| POST | `/instances` | 发起 | ✅ | `workflow:instance:start` | ✅
| GET | `/instances` | 实例列表（cursor） | — | `workflow:instance:read` | ✅
| GET | `/instances/{id}` | 实例视图 | — | 同上 | ✅
| GET | `/instances/{id}/history` | 审批记录（`?afterSequence=` 增量拉取） | — | `workflow:history:read` | ✅
| GET | `/instances/{id}/diagram.svg` | 流转状态图（高亮） | — | `workflow:instance:read` |
| GET | `/instances/{id}/parameters` | 参数快照 | — | 同上 |
| POST | `/instances/{id}/terminate` | 终止 | ✅ | `workflow:instance:terminate` |
| GET | `/tasks` | 我的待办（`scope=MINE|CLAIMED|ALL`）；**我的已办**（`scope=DONE`，另需 `workflow:history:read`） | — | `workflow:task:read` | ✅
| GET | `/tasks/{id}` | 待办详情（含选项 + 表单 schema） | — | 同上 | ✅
| GET | `/tasks/{id}/form` | 该任务的表单快照（历史回显用） | — | `workflow:task:read` | ✅ |
| POST | `/tasks/{id}/submit` | 提交审批 | ✅ | `workflow:task:submit` | ✅
| POST | `/tasks/{id}/claim` | 认领 | ✅ | `workflow:task:assign` |
| POST | `/tasks/{id}/unclaim` | 取消认领 | ✅ | 同上 |
| POST | `/tasks/{id}/transfer` | 转办 | ✅ | 同上 |
| GET | `/tasks/{id}/attachments` | 附件引用 | — | `workflow:task:read` |
| ~~GET~~ | ~~/history/tasks~~ | ~~我的已办~~ | — | v1.12 起**收敛到 `GET /tasks?scope=DONE`**（同一个列表形状，用 `completedAt` 区分），不单开历史端点 |

### 5.2 `/engine/v1`（引擎面，高级）

保留现有 `RuntimeEndpoints` 的能力，但**重新分组 + 去掉裸 `variables` 的越权路径**：

`/definitions`（部署/挂起/激活/删除）、`/instances/{id}/variables`、`/instances/{id}/identity-links`、
`/instances/{id}/jump`、`/instances/{id}/migrate`、`/external-tasks/fetch|complete|failure`、
`/jobs`、`/outbox`。

⚠️ 这一组**不承诺向后兼容**（引擎原语随引擎演进），文档里明确标注 `stability: experimental`。

### 5.3 `/admin/v1`（管理面）

`/processes`（目录维护）、`/dictionaries`（审批语义字典）、`/clients`（API 客户端与 scope）、
`/audit`、`/subscriptions`、`/events/deadletters`、`/quotas`。

---

## 6. 关键改造点（逐项落点）

| # | 改造 | 现状 | 目标 | 主要落点 | 人日 |
|---|---|---|---|---|---|
| M1 | 契约层建立 | DTO 分散在 `NetBpm.Rest/Contracts.cs` 与 `Workflow/Contracts/*` | 独立 `Platform.Contracts`，`Rest` 的 DTO 内收 | 新建工程；`Rest/Contracts.cs` 加 `internal` | 8 ✅ **已完成** |
| M2 | 多流程路由 | `WorkflowEndpointOptions.ProcessKey` 单值 | `IProcessCatalog` + `{processKey}` 路由参数 | `WorkflowEndpointOptions` → 标记 `[Obsolete]`，新 `PlatformApiOptions` | 6 ✅ 读半边（v1.4，§7.6.1） |
| M3 | 响应强类型 | `Decorate*` 返回 `object?` | 端点直接返回 `Platform.Contracts` 类型；Decorator 迁到 `admin/v1` 或删除 | `WorkflowEndpoints.cs` | 5 |
| M4 | 身份三元组 | `RequestContext.UserId/TenantId` | 加 `CallerId`、`SubjectId`；`Production` 模式禁头身份 | `Security/RequestContext.cs`、`AuthenticationMiddleware.cs` | 6 |
| M5 | JWT 升级 | HS256 自签 | RS256 + JWKS + 可选外部 IdP；HS256 保留为内网模式 | `AuthenticationMiddleware.cs`、`AuthTokenIssuer.cs` | 8 |
| M6 | 审批语义字典 | `ApprovalResult` 硬编码 1/2/3 | 三码分离 + 部署期守卫 + 字典表 | 新 `IApprovalVocabulary`；扩 `ApprovalFormGuard` | 10 |
| M7 | 参数 schema | `ParaDict` 扁平展开 | `netbpm:parameter` 声明 + 校验 + 命名空间镜像 | `Model/ParaDict.cs`、`WorkflowService` 校验点 | 12 |
| M8 | 表单快照 | 只透传 formProperty | 提交时落 schema 快照 | `WorkflowService.SubmitAsync` 落库 | 5 |
| M9 | 状态机与聚合 | `Running` 布尔 | 5 态 + `nodeOutcomes` | `WorkflowInstanceViewBuilder`、`WorkflowHistoryBuilder` | 8 |
| M10 | 结论推导上移 | 依赖宿主委托 | 三级回落推导 | `IWorkflowService.CompleteAsync` 旁路 | 4 |
| M11 | 平台存储 | 宿主实现 `IWorkflowDataStore` | 平台自带实现（目录/字典/schema/快照/幂等/客户端） | 新 `Platform.Storage` + 建表脚本 | 12 |
| M12 | 外部任务 | 无 | fetch/complete/failure + 锁 + 死信 | 新 `IExternalTaskBroker` + 表 + 端点 | 15 |
| M13 | 事件对外 | outbox 只写不发 | CloudEvents + 签名 + 重试 + 订阅管理 | `OutboxPublisherService` 扩投递器 | 10 |
| M14 | 契约治理 | 无 | OpenAPI 快照 + CI diff + SDK 生成 | `contracts/` 目录 + CI 脚本 | 6 |
| M15 | 官方 SDK | 无 | `NetBpm.Platform.Client`（.NET）+ 生成 Java/TS | 新工程 | 10 |
| **M16** | **客户端注册与隔离** | 单值 `NetBpm:Security:ApiKey`，无租户/客户端模型 | 一系统一 client：密钥轮换、scope、流程白名单、按 client 配额 | `nb_api_client` + `IClientRegistry` + 认证中间件改造 | **10** |

合计约 **135 人日**（含测试）；按 2 人并行约 **3.5 个月**。

> M16 是「中台形态」相对「单系统自用」多出来的净增量。若哪天改成一对一接入，
> 砍掉的是 M16 的 10 人日，而不是租户列——租户列一旦上线就不能删（历史数据都在里面）。

---

## 7. 数据与兼容

### 7.1 新增平台表（与 `ACT_*` 分离）

本仓已把 `0001~0008` 合并为 `db/<dialect>/netbpm_init.sql`（幂等 `create table if not exists`），
新增平台表按同样风格追加，两份方言都要改，且**转回 CRLF**（`.editorconfig` 规定 `end_of_line = crlf`）。

统一 `nb_` 前缀，明确「这是平台表，不是引擎表」：

| 表 | 关键列 | 说明 |
|---|---|---|
| `nb_tenant` ✅ | `id, name, status, quota_per_minute, created_at, updated_at` | 租户。一期固定一行 `default`；**列不可为空**，避免漏 `WHERE` 导致跨租户读 |
| `nb_api_client` ✅ | `id, tenant_id, name, status, scopes, quota_per_minute, secret_hash, secret_expires_at, previous_secret_hash, previous_secret_expires_at, created_at, updated_at, last_used_at` | 一系统一客户端；双密钥并存 7 天做轮换（§2.5.2）。列名差异与理由见 §7.4 |
| `nb_client_process_grant` ✅ | `client_id, process_key, granted_at, granted_by` | 客户端可用流程白名单。**独立表**而不是 JSON 列：要按 `process_key` 反查「这条流程被哪些系统调用」——发布前的影响面评估全靠它 |
| `nb_process_catalog` ✅ | `tenant_id, process_key, name, category, description, icon, page_urls_json, entry_points_json, status, sort_order, labels_json, parameter_schema_version, created_at, updated_at` | 流程目录的**覆盖层**（不是唯一真源）：无行则全部由部署的 BPMN 派生，实现「部署即可见」。列差异见 §7.6.1 |
| `nb_process_parameter` | `tenant_id, process_key, api_name, variable_name, type, required, writable_at_json, visible_to_json, default_value, rules_json, sort_order, schema_version` | 参数 schema（BPMN 声明是权威，此表是索引 + 对外视角） |
| `nb_approval_dictionary` ✅ | `tenant_id, process_key, node_id NULL, option_id, semantic, outcome_class, label, requires_comment, requires_target_node, allowed_target_nodes_json, attributes_json, created_at, updated_at` | 审批语义字典；`node_id` 为空表示流程级默认。只存流程自定义选项的声明，1/2/3 由默认字典覆盖（§7.6.5） |
| `nb_approval_snapshot` | `process_instance_id, task_id, definition_version, form_schema_json, form_values_json, captured_at` | 审批时的表单快照（历史按当时 schema 渲染） |
| `nb_parameter_snapshot` | `process_instance_id, revision, values_json, updated_at, updated_by` | 参数修订历史（只增不改，可回溯「谁在第几步改了什么」） |
| `nb_instance_timeline` | `id, tenant_id, process_instance_id, sequence, kind, activity_id, task_id, actor_id, actor_caller_id, option_id, semantic, comment, target_node_id, payload_json, occurred_at` | append-only 时间线；唯一索引 `(process_instance_id, sequence)` 是「单调序号」的物理保证 |
| `nb_idempotency` | `client_id, idem_key, method, path, body_hash, status_code, response_json, expires_at` | 长窗口幂等（现有表按需迁移；键从「全局」改为「按 client」） |
| `nb_external_task` | `id, process_instance_id, task_definition_key, topic, worker_id, lock_expires_at, retries, payload_json, error_message` | 外部任务 |
| `nb_event_subscription` | `id, tenant_id, client_id, url, event_types_json, process_keys_json, secret, status` | 事件订阅（按 client 记账，避免一个系统退订影响别家） |
| `nb_audit_log` | `id, tenant_id, client_id, actor_id, action, target_type, target_id, before_json, after_json, occurred_at` | 管理动作审计（部署、吊销、跳转、直写变量） |

红线：**不改 `ACT_*` 表**（`0001` 的 13 张表约束与 `DatabaseScriptTests` 不动）。
需要给 `ACT_*` 加列时（如 `ACT_HI_TASKINST` 加 `caller_id`），走**独立幂等 `alter`**，并接受「现网需手工执行」
（本仓无迁移记录表，`DatabaseMigrator` 只在库未初始化时跑脚本）。

### 7.2 兼容策略

| 变更 | 策略 |
|---|---|
| 新增响应字段 | 直接加（契约标 `x-extensible-enum`，客户端必须容忍未知字段/枚举） |
| 新增必填请求字段 | ❌ 禁止；只能新增可选字段 + 服务端默认值 |
| 改字段语义 | 新字段 + 旧字段标记 `deprecated` + 双写一个主版本 |
| 改 BPMN 变量名 | ❌ 禁止（变量名是流程契约）；新流程用新名 |
| 引擎升级 | `Platform.Contracts` 不引用引擎 → 引擎可独立升级 |

### 7.3 灰度路径

```
第 1 步  平台表建好，双写（目录/字典）—— 旧 API 行为零变化
第 2 步  新增 workflow/v1，只读端点先上（目录/定义/流程图/实例/历史）
第 3 步  写端点（submit/start）切到一个内部系统试点
第 4 步  外部任务上线，把 BPMN 里的 serviceTask 逐条改 topic
第 5 步  /api/runtime 标 deprecated，指向 engine/v1
第 6 步  Demo 保持嵌入式不变（它同时是两种形态的回归基线）
```

### 7.4 v1.2 已落地：接入三表 + `IClientRegistry`

三张表已经进入 `SchemaCatalog`（唯一数据源），由 `dotnet run --project tools/NetBpm.DbInit -- --generate-only`
渲染到两份方言脚本——不再是「DDL 草案」。实际列与草案的差异（**以代码为准**）：

| 草案 | 实际 | 为什么改 |
|---|---|---|
| `nb_tenant.tenant_id` | `nb_tenant.id` | 与引擎 `ACT_*.TENANT_ID_` 的取值、对外的 `tenantId` 保持一个词；多一层前缀只会让每次 join 都写别名 |
| `nb_api_client.client_id` | `nb_api_client.id` | 同上（对外字段仍叫 `clientId`） |
| `scopes_json` | `scopes varchar(1000)`（空格分隔） | scope 是短词表不是任意结构；与既有 `admin_user.scopes` 同格式，运维能肉眼比对 |
| `previous_secret_valid_until` | `previous_secret_expires_at` | 与 `secret_expires_at` 成对命名，避免「valid_until / expires_at」两套叫法 |
| （无） | `last_used_at` | 要停用一个「看起来没人用」的客户端，得先能看见它最后一次被调用的时间 |

三条实现决策（**上线后改不动**）：

1. **密钥格式 `nbc.{clientId}.{token}`**（`ClientSecretFormat`）。密钥自带客户端 id，鉴权时按主键查一行即可；
   否则要么全表遍历比对摘要，要么再加一张「密钥 → 客户端」表。
   摘要用 **SHA-256 而非 PBKDF2**：token 是 256 位随机数，慢哈希只会让每个请求多付十万轮开销
   （用户口令仍走 `PasswordHasher` 的 PBKDF2）。
2. **状态列存对外词汇**（`PENDING / ACTIVE / SUSPENDED / REVOKED`），不存枚举序号。
   序号会随枚举增删漂移，而字符串是契约的一部分——改它必须改契约。
3. **注册即 `ACTIVE`**（`ClientRegistryOptions.InitialStatus` 可改）。注册本身由管理员发起，已经是人工审批点；
   再要求一次「激活」只会让接入方第一次调用收到 401 却不知道为什么。需要「申请 → 审批 → 启用」的部署把它配成 `Pending`。

`IClientRegistry`（`src/NetBpm.Persistence/Platform/`，Dapper + 内存两套实现语义逐条对齐）：

| 方法 | 语义 | 刻意不做的事 |
|---|---|---|
| `RegisterAsync` | 校验租户存在 + scope 合法（高危需 `allowPrivilegedScopes`）+ 客户端与白名单同一事务落库 | 不写审计、不限流 |
| `AuthenticateAsync` | 解析 id → 查一行 → **先验密钥再看状态** → 判租户 → 节流回写 `last_used_at` | 不验 scope、不判数据归属 |
| `RotateSecretAsync` | 新密钥生效，旧密钥进入宽限期；**宽限期只有一代** | 不通知调用方（通知属于事件订阅） |
| `UpdateAsync` | Patch 语义（`null`=不改，空集合=清空）；白名单**整体替换** | 不支持改 `clientId` |
| `CanUseProcessAsync` | 白名单无行 = 全部授权 | 不判流程是否存在 |

「先验密钥再看状态」不是洁癖：若顺序反过来，拿一把瞎猜的密钥就能探出「这个客户端被停用了」。

现网补表（本仓**无迁移记录表**，`migrate` 对已初始化的库整体跳过，只能按文件 `apply`）：

```bash
# 1) 重新生成脚本（SchemaCatalog 是唯一数据源，别手改 db/*.sql）
dotnet run --project tools/NetBpm.DbInit -- --generate-only

# 2) 对目标库整份执行（脚本全幂等，老库只补缺失的表/索引，不覆盖已有数据）
dotnet run --project tools/NetBpm.DbMigrate -- apply --script db/mysql/netbpm_init.sql \
  --provider mysql --connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;Allow User Variables=true;"
```

守门测试：`PlatformClientRegistryTests`（13 项：密钥格式、未知客户端与错密钥的区分、状态拒绝、租户停用、
轮换宽限期、连续轮换只留一代、白名单空集语义、配额回落）、
`ClientRegistryDatabaseTests`（真库 3 项：只存摘要、状态列文本、白名单整体替换、宽限期到点失效）、
`DatabaseScriptTests`（三张表必须同时出现在两份方言脚本里）。

---

### 7.5 v1.3 已落地：身份三元组 + 客户端凭据 + 接入管理

M4（身份）与 M16（客户端注册）的接线部分完成。这一节只写**上线后不好改**的决定，以及与 §2.5 / 附录 C 的出入。

#### 7.5.1 三元组落到 `RequestContext`

| 字段 | 填充来源 | 说明 |
|---|---|---|
| `CallerId` / `CallerName` | 客户端注册表 | 「哪个系统在调」 |
| `SubjectId` | JWT `sub` / `X-Subject-Id`（或旧 `X-User-Id`） | 「以谁的身份」；纯服务为 `null` |
| `TenantId` | 客户端记录的 `tenant_id` / JWT `tenant` | **不接受请求头覆盖** |
| `RequestId` | `X-Request-Id`，缺省 `TraceIdentifier` | 响应头回显，401/403 也带 |
| `UserId` | —— | `SubjectId` 的兼容别名（`get`/`set` 转发），60 多个端点零改动 |
| `AuthMode` | —— | 新增 `Client`；`None` / `ApiKey` / `Jwt` 语义不变 |

`CanSubmitApproval` 放在上下文里而不是散在各端点：判据是**有没有真人主体**，所以 `Client` 模式的纯服务令牌恒为
`false`，即使它被授予了 `workflow:task:submit`。`None` / `Jwt` / 旧 `ApiKey` 沿用既有行为（它们本来就没有「系统 vs 人」的区分）。

#### 7.5.2 中间件判定顺序（`AuthenticationMiddleware`）

```
1. Bearer JWT（配置了签名密钥时）           → 失败即 401
2. 凭据形如 nbc.* （含 Authorization: Bearer nbc.*）→ IClientRegistry 校验
3. X-Api-Key（旧单值密钥）                  → 定长比较
4. 都没配 → 放行（开放模式）                 → 配了但没带 → 401
```

四条硬规则（都有集成用例守着）：

1. **前缀即承诺**：`nbc.` 开头的凭据即使解析不出来（被截断 / 多了空格）也返回 401，**不降级成匿名**。
   否则配置损坏会以「匿名调用成功」的形式潜伏，直到某天被写进生产数据。
2. **租户头不符就拒**：客户端记录钉死租户，`X-Tenant-Id` 不一致 → 403。
   这里刻意**不**「忽略请求头」——忽略会让「写 tenant-b」的请求静默落到 tenant-a，比报错危险得多。
3. **声明操作人需要代客 scope**：带 `X-Subject-Id` / `X-User-Id` 必须有
   `workflow:task:submit:onbehalf`（代客身份是显式授权）。否则任意客户端都能冒充真人提交审批，
   审批记录里的办理人不再可信。
4. **查询参数不能跨租户**：`RequestIdentity.Tenant` 对客户端与 JWT 同口径——
   请求声明了不同租户 → 403，未声明 → 用凭据租户。
   这条最容易漏：按 id 访问有 `TenantGuard` 兜底，但**列表**的过滤条件完全由它决定，
   少了它一个 `?tenantId=别人` 就能跨租户读。

#### 7.5.3 客户端凭据 ≠ 管理授权

`AdminAuthorization.Ensure` 的放行分支从「非用户身份（即所有 API Key）」收紧为
「`None` 或旧 `ApiKey`」。理由：旧 API Key 是「一个密钥代表整个可信内网」的历史产物，
而客户端是**一个业务系统**——只被授予 `workflow:instance:read` 的系统若也能调管理端点，
多系统接入后第一周就会出越权事故。

代价是：客户端即使拿到了 `workflow:admin:client`，也**不**自动通过旧 `netbpm.admin.*` 检查（两套词汇各管一面）。
接入管理端点（`/api/admin/clients`）显式认 `workflow:admin:client`，所以「平台管理员」与「引擎管理员」可以是两个主体。

#### 7.5.4 必须显式打开的开关：`RequireCredential`

只注册客户端、不配旧 API Key / JWT 时，`IsConfigured` 仍为 `false` → 匿名请求照旧放行。
这是最容易踩的部署坑（"我注册了客户端，以为 API 已经受保护"），处理方式：

- 新增 `NetBpm:Security:RequireCredential`（默认 `false`，保持既有部署行为）；
- 启动时若「已注册客户端 + 三项凭据配置全空」，**打印显式告警**并给出该配置项名称。

首次注册客户端需要**人类管理员**：打开 `RequireCredential` 后匿名调不了 `POST /api/admin/clients`，
得先用管理账号登录（`admin_user` + JWT）或临时旧 API Key 完成第一次发密钥。这是有意的——发密钥不该是匿名操作。

#### 7.5.5 接入管理端点（过渡形态）

| 方法 | 路径 | 作用 | scope |
|---|---|---|---|
| `GET` | `/api/admin/tenants` | 租户列表 | `workflow:admin:client` |
| `PUT` | `/api/admin/tenants/{tenantId}` | 幂等登记租户（不覆盖名称 / 配额） | 同上 |
| `PUT` | `/api/admin/tenants/{tenantId}/status` | 启停租户（整租户止血） | 同上 |
| `GET` | `/api/admin/clients` | 客户端列表（可按租户过滤，越界 403） | 同上 |
| `GET` | `/api/admin/clients/{clientId}` | 客户端详情 | 同上 |
| `POST` | `/api/admin/clients` | 注册并签发首个密钥（明文只此一次） | 同上 |
| `PATCH` | `/api/admin/clients/{clientId}` | 局部更新（`null`=不改，空集=清空） | 同上 |
| `POST` | `/api/admin/clients/{clientId}/rotate` | 轮换（旧密钥进 7 天宽限期） | 同上 |
| `DELETE` | `/api/admin/clients/{clientId}` | 注销（密钥立即失效，记录保留） | 同上 |

- 高危 scope 必须 `?confirmPrivilegedScopes=true`（未确认 → 400 `client.scope_needs_confirmation`）。
- 不能停用**当前凭据所属**租户（否则一把停掉自己，连改回来都做不到）→ 400 `tenant.self_suspend_forbidden`。
- 管理动作全部写审计（`client.register` / `client.update` / `client.rotate` / `client.revoke` /
  `tenant.register` / `tenant.status_change`），明细里带 `caller`——「哪个系统给哪个系统发的密钥」必须可回查。

端点放在 `/api/admin/**` 而不是 `/admin/v1/**`：这一版复用既有错误体与鉴权口径，等 `Platform.Api` 落地整组迁走。
调用方目前只有运维脚本，路径改两次没有价值。

#### 7.5.6 本阶段明确没做

| 项 | 现状 | 影响 |
|---|---|---|
| 引擎面端点（`/api/runtime` `/api/identity` `/api/designer`）按平台 scope 逐条收紧 | 只做租户隔离，未按 `workflow:*` 判定 | 客户端能读到比所需更多的引擎数据；平台面（`workflow/v1`）不经过这些端点 |
| JWT 的 RS256 + JWKS | 仍是 HS256 自签 | 外部 IdP 直连要等 S2 第二批 |
| 事件投递 / 外部任务 | 未动 | S4 |

> 「限流按客户端分区」原列在本表，已在 v1.4 落地（§7.6.2）。

守门用例：`ClientAuthenticationTests`（15 项：密钥只出现一次、`Bearer nbc.*`、伪造密钥在开放模式下也 401、
租户头越界 403、查询参数越界 403、无代客 scope 声明主体 403、高危 scope 需确认、无管理 scope 不能管客户端、
注销 / 暂停即时生效、轮换宽限期双密钥可用、租户引导与自停用保护、`RequireCredential` 关闭匿名访问）。

---

### 7.6 v1.4 已落地：流程目录 + 按客户端限流

S3 的第一块（M2 的读半边）与 S2 遗留的限流项。这一节只写**结论与代价**，不重复代码注释。

#### 7.6.1 流程目录：三层合并，部署即可见

`GET /workflow/v1/processes`（+ `/{key}`、`/{key}/definition`、`/{key}/versions`、`/{key}/diagram.svg`）
由 `IProcessCatalog`（`src/NetBpm.Rest/Platform/ProcessCatalog.cs`）供数，三处信息按序合并：

| 层 | 提供什么 | 为什么在这里 |
|---|---|---|
| 引擎部署（`ACT_RE_PROCDEF`） | 版本号、挂起状态、部署时间、定义原文 | 唯一真源。目录**不缓存**版本与校验和，避免「部署了但目录还是旧的」 |
| BPMN 派生 | 名称、发起入口（每个 `startEvent`）、支持的审批语义（`approvalResult` 的选项）、是否有表单字段、`sha256:` 校验和 | 不需要任何人先配置；派生结果按**定义 id** 缓存 30 分钟（重部署会换 id，不会读陈旧） |
| `nb_process_catalog` 覆盖行 | 分类、图标、业务页面地址、显式状态、排序、本地化标签、参数 schema 版本 | 只在需要精修时登记。可空列 = 「跟随上一层」，空串是合法展示名，不能拿来表示「没配」 |

三条设计决定：

1. **表是覆盖层，不是真源**。草案里的 `deployed_version` / `definition_checksum` 两列**没有建**：
   存进来就会有两份真源，任何一次「部署了但忘了同步」都会让调用方按过期校验和缓存流程图。
   新流程部署完自动出现在目录里——这是「接入方零配置就能开始」的前提。
2. **引擎的挂起优先于覆盖行**。覆盖行可以把流程标成草稿 / 已下线，但**不能**把已挂起的流程说成可发起：
   那道闸门在引擎里，目录盖过去只会让调用方在提交时才吃 409。
3. **无租户部署视为全局共享流程**。引擎原语里 `TENANT_ID_ = ''` 是「无租户」，不是「所有租户」；
   而历史部署都不带租户。目录按「自己的 + 无租户的」合并、同 key 时租户自己的胜出，
   否则「客户端属于 `default`」会看不到任何流程。

白名单在目录里生效：未授权的流程**不出现**在列表，按 key 访问返回 **404 `process.not_found`**
（不是 403——403 等于告诉调用方「这条流程存在，只是没授权」，多系统共用一套引擎时这就是流程清单泄露）。

能力声明（`capabilities`）是**服务端已判定的结论**，不是原始事实：

| 字段 | 依据 |
|---|---|
| `canStart` | 流程状态生效 + 已部署 + 调用方有 `workflow:instance:start` |
| `canReadDefinition` / `canReadDiagram` | 有 `workflow:process:read` |
| `hasParameterSchema` | `parameter_schema_version > 0`（参数规范在 S3 第二批实现，当前恒 `false`） |
| `hasFormSchema` | BPMN 里存在任意 `formProperty` |
| `supportedSemantics` | 从 `approvalResult` 的 `activiti:value/@id` 经 `ApprovalSemantics.Resolve` 推导；空数组 = **未声明** |

`ApprovalSemantics`（`Platform.Contracts/Process/ProcessCapabilities.cs`）是三码模型的第一层：
`1/2/3` 与常见英文词映射到 `APPROVE/REJECT/RETURN/DELEGATE`，**未识别一律 `CUSTOM`**，不猜——
猜错的后果是「把退回当成通过」。流程特有的选项等 S3 第二批的 `nb_approval_dictionary`。

#### 7.6.2 限流：从按 IP 到按调用方

`ClientRateLimitMiddleware` + `FixedWindowRateLimiter`（`src/NetBpm.Rest/RateLimiting/`）。
分区键的顺序就是优先级：

| 顺序 | 分区键 | 配额 | 为什么 |
|---|---|---|---|
| 1 | `client:{clientId}` | `nb_api_client.quota_per_minute`（未配则 `ClientPerMinute`） | 配额是「这个接入方能用多少」。即使它代客调用（带 `subject`），也记在系统头上 |
| 2 | `user:{tenant}:{subject}` | `ApiPerMinute` | 比按 IP 公平：一个办公室几十号人不会互相挤占；反代后也仍然可分 |
| 3 | `ip:login` / `ip:probe` / `ip:api` | `LoginPerMinute` / `ProbePerMinute` / `ApiPerMinute` | 无身份时只能按 IP，且按用途分档（登录口是口令爆破点） |

按 IP 分区在两种真实部署下都不成立：多系统共用一个出口 IP（NAT / 网关）时一家跑飞别家全 429；
反代后面所有人共用一个 `RemoteIpAddress` 时整站共用一个桶。

三处配套改动（少一处就不成立）：

1. **管道顺序改成「鉴权 → 限流 → 幂等」**。限流要按 `clientId` 分区，而 `clientId` 只有验过密钥才有。
   先限流后鉴权的时代只能拿 IP。代价：未通过鉴权的请求不做限流（它们本来就过不去）。
   限流放在幂等之前是为了避免「被 429 的请求写了一条幂等记录，重试时永远回放 429」。
2. **鉴权覆盖面从 `/api` 扩到 `/api` + `/workflow` + `/engine` + `/admin`**。
   否则新的平台面完全在鉴权之外——匿名就能读全部流程目录，scope 与白名单形同虚设。
3. **配额随身份带出**（`RequestContext.QuotaPerMinute`）。限流中间件不回表：它的热路径上不该有 IO。

响应头与错误体：`RateLimit-Limit` / `RateLimit-Remaining` / `RateLimit-Reset`（每请求），
429 额外给 `Retry-After`，错误体是 RFC 9457（`code = rate_limited`，带分区与上限，便于调用方判断找谁调配额）。

两个刻意的取舍：

- **固定窗口而非滑动**：窗口边界允许 2 倍突发。目的是「防脚本失控 / 防爆破」，不是精确计费。
- **分区表满时 fail-open**：分区键可被海量 IP 撑爆，满表时 fail-closed 等于用一个内存保护把全体调用方打挂。
- **进程内计数**：多实例部署时总配额 = 实例数 × 配置值。要全局配额时把 `FixedWindowRateLimiter`
  换成分区在 Redis 上的实现，中间件不用改（这是把它做成独立类型而不是内联的原因）。

#### 7.6.3 本阶段明确没做

| 项 | 现状 | 影响 |
|---|---|---|
| 参数规范的 `netbpm:parameters` 覆盖扩展 | 只从 `formProperty` 派生基础字段 | `writableBy` / `visibleTo` / `pattern` / `min`/`max` 等平台级约束还没来源 |
| 审批语义字典表 | 只有 `ApprovalSemantics` 的默认字典 | 流程自定义选项一律 `CUSTOM`，没有部署期守卫 |
| 目录管理端点（`/admin/v1/processes`） | 只有读路径；`IProcessCatalogStore` 无写方法 | 精修名片目前要直接改库（写路径等管理面一起做，避免写一段没人用的代码） |
| 全局（跨实例）配额 | 进程内计数 | 多实例部署需 Redis 版 |

#### 7.6.4 参数规范：从 `formProperty` 派生，能力声明闭环

`GET /workflow/v1/processes/{key}/parameters/schema` 返回 `ParameterSchema`。本阶段只做**派生**，
不引入 `netbpm:parameters` 扩展（那是覆盖层，下一块补）。

- **来源**：各 userTask 的 `activiti:formProperty`。`apiName = param.{variable}`（缺省用 `id`）、
  `type` 由 Activiti 类型映射（`long`/`int`→`LONG`、`boolean`→`BOOLEAN`、`enum`→`ENUM` 且枚举值进
  `constraints.enumValues`……未识别一律 `STRING`）、`required` 直接透传、`displayName` 取 `name`。
- **唯一排除 `approvalResult`**：它是审批选项，走语义字典（三码），不是业务参数。
  `comment`（审批意见）**保留**——它是调用方要采集的字段。
- **去重**：同一 `variable` 在多个环节重复出现（「申请人修改」复用「提交申请」的字段）按
  `variable` 去重，保留首个环节的定义。
- **空 schema**：流程没有业务 `formProperty` 时返回 `parameters: []`、`version: 0`，
  且 `capabilities.hasParameterSchema = false`。能力声明不再依赖 `parameterSchemaVersion`（那张表还没人写），
  改为**由派生参数非空驱动**——这样「部署即可见」的语义也延伸到参数规范。

守门用例：

- `ProcessCatalogTests`（14 项：部署即可见与能力声明、白名单过滤与 404、定义原文按需返回、
  版本列表标记 current、cursor 分页不重不漏、覆盖行精修与 `RETIRED` 不可发起、
  引擎挂起优先、流程图 SVG、`RequireCredential` 下匿名 401、缺 `workflow:process:read` 403、
  参数 schema 派生 / 排除审批选项 / 空 schema / 白名单 404）。
- `RateLimitByClientTests`（4 项：同一 client 超限 429 + `Retry-After` + 配额头、
  **另一 client 同 IP 不受影响**、窗口重置、不限流分区不计数、分区表满 fail-open）。
- `PlatformContractTests`（16 项：枚举 `SCREAMING_SNAKE_CASE`、属性 camelCase、
  `canStart` 与 `capabilities.canStart` 恒等、optionId → 语义映射、`outcomeClass` 推导）。
- `ApprovalVocabularyTests`（8 项：默认字典解析 1/2/3、未知选项落 `CUSTOM`/`NEUTRAL`、
  流程声明覆盖默认字典、节点级覆盖流程级、守卫「全映射通过 / 无映射告警 / 无映射严格拒绝 /
  声明后通过」）。

#### 7.6.5 审批语义字典：两层合并 + 部署期守卫

三码模型（§4.2）的第一层落地：`optionId` → `semantic` → `outcomeClass` 的解析，
以及「标准可执行」的部署期守卫。

- **`IApprovalVocabulary`**（`src/NetBpm.Rest/Platform/ApprovalVocabulary.cs`）：两层合并——
  ① 默认字典（`ApprovalSemantics`，数字 1/2/3 + 常见英文词，跨流程稳定）；
  ② 流程声明（`nb_approval_dictionary`，节点级覆盖流程级）。**未识别的选项一律 `CUSTOM`，不猜**——
  猜错的后果是「把退回当成通过」，比返回「自定义」严重得多。
- **字典表只存流程自定义选项**：1/2/3 的映射由默认字典覆盖，<b>不重复落库</b>——否则两份真源，
  一份改了另一份没改就是静默漂移。`semantic` / `outcome_class` 存文本词汇（`APPROVE` 等），不存枚举序号。
- **部署期守卫 `ApprovalVocabularyGuard`**（`src/NetBpm.Rest/Diagnostics/`）：遍历 `approvalResult` 的
  每个 `activiti:value`，凡是「默认字典 + 字典表都没有映射」的选项：
  - 默认（宽容）→ 返回提示级 `ValidationError`，随部署响应回给调用方（与委托自检 `DeploymentDelegatePrecheck` 同理）；
  - `rejectUnmapped: true` → 抛 `InvalidOperationException`，报错含节点 id 与选项 id。
- **放在 Rest 而非 Workflow**：守卫要读 `nb_approval_dictionary`（Persistence）与 `ApprovalSemantics`
  （Platform.Contracts），而 `NetBpm.Workflow` 的分层约束是「只依赖 Bpmn.Model + Engine + Runtime」。
- **打破魔法数耦合**：旧的 `ApprovalFormGuard` 把选项锁死在 `ApprovalResult` 的 1/2/3 上，本守卫把口径
  放宽到「任意 optionId + 语义映射」——流程可以声明任意数量的自定义选项。旧守卫仍保留（Demo 宿主专用），
  平台面不再依赖它。

`TaskView.Options`（调用方渲染审批按钮）的消费端将在下一块（待办端点 `/workflow/v1/tasks`）落地时接入
`IApprovalVocabulary`。

#### 7.6.6 待办读模型：`TaskView.options` 消费语义字典

三码模型的消费端落地：调用方不再读 `activiti:value` 或硬编码 `optionId`，而是拿到服务端算好的
`TaskView.options`（`semantic` / `outcomeClass` / 校验规则）直接渲染按钮。

- **`ITaskCatalog`**（`src/NetBpm.Rest/Platform/TaskCatalog.cs`）：把引擎 `TaskEntity` + BPMN `UserTask` +
  `IApprovalVocabulary` 组装成 `TaskView` / `TaskSummary`。
  - `Options` 从 `approvalResult` 的 `activiti:value` 派生，语义由字典两层合并解析；
  - `Form` 从 `formProperty` 派生（`approvalResult` 排除——它走 options），`comment` 等业务字段保留；
  - `CurrentUserCanSubmit` 服务端判定：assignee / 候选用户 / 候选组命中（组来自 JWT `groups` 声明），
    调用方不必自己猜——猜错了就是一次 403。
- **端点**（`src/NetBpm.Rest/Endpoints/TaskEndpoints.cs`）：`GET /workflow/v1/tasks`（`scope=MINE|CLAIMED|ALL`
  cursor 分页）、`GET /workflow/v1/tasks/{id}`。`ALL` 需管理权限（超管或 `workflow:history:read:all`），
  服务端判定不靠调用方自觉；未授权 / 不存在 / 已完成的待办按 **404** 处理（不泄露归属）。
- **租户口径与目录一致**：无租户部署（`TENANT_ID_ = ''`）视为全局共享；引擎查询不传租户，
  租户边界与流程白名单由 `IsAuthorizedForTask` + `context.CanUseProcess` 在结果层过滤。
- **代客调用仍受 `onbehalf` 约束**：客户端凭据声明 `X-Subject-Id` 必须持有
  `workflow:task:submit:onbehalf`（高危 scope，注册时 `confirmPrivilegedScopes=true`）——纯服务令牌不得冒充真人。

守门用例：`TaskCatalogTests`（5 项：options 语义解析、`currentUserCanSubmit` 由 assignee 判定、
`scope=CLAIMED` 只返回本人待办、缺 `workflow:task:read` 403、未知待办 404）。

#### 7.6.7 实例读模型 + 提交审批：`InstanceView` 单一出口 + `submit` 校验闭环

S3 第五块一次落地两块：实例读模型（`GET /workflow/v1/instances/{id}`）与提交审批
（`POST /workflow/v1/tasks/{id}/submit`），两者共用 `InstanceView` 这一**单一出口**——
实例详情与提交结果里的 `Instance` 长得一样，不再各拼一份（口径分叉的来源）。

- **`IInstanceCatalog`**（`src/NetBpm.Rest/Platform/InstanceCatalog.cs`）：把「执行树 + 历史 + 待办」
  组装成 `InstanceView`。
  - 存在性**双查**：运行态执行（`IExecutionManager.FindByIdAsync`）+ 历史实例
    （`FindHistoricProcessInstanceAsync`）。内存模式（历史关闭）下结束实例查不到，运行实例仍靠执行树回答；
  - `Status` 由「运行态执行是否存在」优先判定（`Running` / `Suspended`），运行态删除后靠历史 `DeleteReason`
    判 `Completed` / `Terminated`（引擎本子集不产出 `Failed`）；
  - 变量快照 = 历史变量（排除任务局部变量）为底 + 运行变量覆盖；参数快照排除 `BaseModelVariables`、
    对外键加 `param.` 前缀（与参数 schema 的 `apiName` 同口径）；
  - 结论推导**三级回落**（见 §7.6.8）：`DECLARED` → `LAST_DECISION` → `FALLBACK`。
- **提交审批 `ITaskSubmitService`**（`src/NetBpm.Rest/Platform/TaskSubmitService.cs`）：
  校验链先过「真人主体」（`CanSubmitApproval` + `SubjectId`），再过待办归属、`optionId ∈ options`、
  `semantic` 一致性、`requiresComment`、`requiresTargetNode` / `allowedTargetNodes`
  （422 各自带稳定错误码）；通过后组装变量（`result=optionId` + `comment` + 参数剥 `param.` 前缀镜像）
  → `CompleteTaskAsync`（带 `expectedRevision` 乐观锁，越界 400、冲突 409）。
- **端点**（`src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` / `TaskEndpoints.cs`）：
  `GET /instances/{id}`（`workflow:instance:read`）、`POST /tasks/{id}/submit`（`workflow:task:submit`）；
  未授权 / 不存在按 404（`InstanceNotFound` / `TaskNotFound` 同口径，不泄露归属）。
- **代客调用约束**：客户端凭据声明 `X-Subject-Id` 必须持 `workflow:task:submit:onbehalf`；
  纯服务令牌（无 subject）即便持 `workflow:task:submit` 也 403 `auth.subject_required`。
- **本轮只校验不落库**：`targetNodeId` 不执行退回（§4.5），`formValues` 不落表单快照（§4.6），
  已在代码注释显式声明，属后续块。

守门用例：`InstanceCatalogTests`（2 项，内存宿主：运行中视图组装 + 未知实例 404）、
`TaskSubmitTests`（8 项，内存宿主：成功推进到下一环节 / 非选项 422 / 语义不一致 422 /
需意见 422 / 需目标节点 422 / 目标节点白名单 422 / 非办理人 404 / 纯服务 403）、
`InstanceConclusionTests`（4 项，MySQL + 历史宿主：结束实例 `COMPLETED` + 结论三级回落三态）。

#### 7.6.8 审批记录时间线：写时定稿 + 单调 `sequence` + 结论三级回落

S3 第七块把「审批记录」从一个拼装动作变成一条**可增量拉取的时间线**，并把结论推导补齐到三级（§4.7）。

- **表** `nb_instance_timeline`（`tools/NetBpm.DbInit/Schema/SchemaCatalog.cs`）：append-only，
  主键 `(process_instance_id, seq)`、索引 `(tenant_id, occurred_at)`。
  列名用 `seq` / `comment_text` 而不是 `sequence` / `comment`——后两者在部分方言里是保留字，
  建表与 Dapper 拼 SQL 都得加引号，而两种方言的引号还不一样，换个短名最省事。
- **为什么要有这张表**：引擎历史 `ACT_HI_ACTINST` 只记「谁在什么时间办完了哪个环节」，
  **不认识审批意见与选项语义**（那是业务概念）；业务侧审批记录又不认识引擎的流转。
  对外要一条完整时间线，只能在**写入的那一刻**把两份信息合成好并定稿——读时再拼永远拼不稳
  （定义升级、字典变更都会让「当时批的是什么」漂移）。
- **写时定稿**（`src/NetBpm.Rest/Platform/ApprovalTimeline.cs`）：
  - 发起 → `INSTANCE_STARTED`（`actorId` 取令牌 `subject`，纯服务令牌为空）；
  - 提交 → `TASK_COMPLETED`（带 `optionId` / `semantic` / `comment` / `targetNodeId`）；
  - 实例结束 → `INSTANCE_COMPLETED` / `INSTANCE_TERMINATED`；
  - **`actorId`（真人）与 `actorCallerId`（提交它的系统）并列**（缺口 F 的落点）。
- **写失败不阻断业务**：`AppendAsync` 内部吞异常并按 `Warning` 记录。
  引擎已经把任务办结了，这时候抛错只会让调用方以为没提交成功——少一条审计记录比「状态说不清」轻。
- **序号分配**：存储层「读 max + 插入 + 唯一键冲突重试」（`DatabaseErrors.IsUniqueViolation`），
  调用方传入的 `Sequence` 一律忽略。序号是并发安全的产物，不该由业务决定。
- **老实例回填**：本能力上线前发起的实例没有时间线行；首次读取时从 `ACT_HI_ACTINST` 合成一份
  最小时间线写进去，此后走主路径。回填条目**不带审批意见与语义**——硬按 `result` 倒推语义
  等于编造一条「当时的决定」，比缺一条更糟。
- **环节聚合 `nodeOutcomes`**：同环节多条完成记录按 `ALL_APPROVED`、单条按 `SINGLE`；
  `pending` 从实例当前待办补（会签「3 人还有 1 人没批」只看已完成条目会误判成通过）。
  BPMN 多实例配置尚未进平台模型，这里先用保守口径，注释已声明这是**临时实现**。
- **端点**（`InstanceEndpoints.HistoryAsync`）：`GET /instances/{id}/history`
  （`workflow:history:read`，`?afterSequence=` + `?limit=`，未知实例 404）。
  存在性 / 租户 / 白名单全部复用 `IInstanceCatalog`，不另写一份判定——
  否则「详情能看、历史看不到」这种分裂迟早出现。人员级可见性（仅相关人员）留审计块。
- **`submit.timelineDelta`**：只带**本次操作新增**的条目，调用方直接接着拉即可，不需要自己算序号。

**结论三级回落（§4.7 闭环）** —— `InstanceCatalog.BuildConclusion`：

| 级别 | 来源 | 判据 | `conclusion.source` |
|---|---|---|---|
| 一 | 流程显式声明 | 变量 `netbpm_outcome`（语义名或选项 id） | `DECLARED` |
| 二 | 最后一条审批决定 | 变量 `result` → `ApprovalSemantics.Resolve` | `LAST_DECISION` |
| 三 | 兜底 | 以上都没有（人工跳结束 / 被终止） | `FALLBACK`（`CLOSED` / 已办结） |

- 变量名带 `netbpm_` 前缀是刻意的：业务方自己的变量叫 `outcome` 很常见，一旦撞名就会
  把业务值当成流程结论，而这种错误在联调阶段几乎发现不了。
- BPMN 侧对应写法是 `<netbpm:outcome semantic="APPROVE"/>`；当前解析器尚无 `extensionElements`
  读取能力，**运行时落点已就位**（同名变量），解析器支持后读侧不用改。
- 解析声明值时**先比语义名、再走选项 id 字典**：`Enum.TryParse("1")` 会解析成枚举序号 1
  （= `REJECT`），那正好是最危险的误判——流程声明「通过」，平台读成「驳回」。
- 新增 `ApprovalSemantic.Closed`（追加值 5）：让审计能区分「流程说通过了」与「平台只知道它结束了」。
  把第三级伪装成第一级，等于给一个没人批过的单子盖上「已通过」。

守门用例：`ApprovalTimelineTests`（6 项，内存宿主：发起写 `INSTANCE_STARTED`（纯服务令牌
`actorId` 为空）/ 提交写 `TASK_COMPLETED` 且 `timelineDelta` 与历史一致 / `afterSequence`
增量拉取 / `limit` 分页 + `nextCursor` / 未知实例 404 / 缺 scope 403）、
`InstanceConclusionTests` 新增 2 项（MySQL + 历史宿主：`netbpm_outcome=REJECT` 与 `result=1`
**矛盾**时以声明为准 / 无任何决定时兜底 `CLOSED` + `FALLBACK`）。

---

#### 7.6.9 实例列表：双源合并 + 游标分页 + 服务端人员范围

`GET /workflow/v1/instances`（`IInstanceListCatalog`）——「我的单据」页面的数据源。

**1. 数据源必须两侧合并，单用任何一侧都是错的**

| 只用 | 后果 |
|---|---|
| 历史（`ACT_HI_PROCINST`） | 历史关闭（`EnableHistory=false`）时**恒为空**，列表直接是空的 |
| 运行态（`ACT_RU_EXECUTION` 根执行） | 实例一结束运行态行就被删 → **所有已办单据全部消失**，而「我的单据」里绝大多数恰恰是已办 |

合并规则：历史提供 `EndTime` / `DeleteReason`（状态判定必需），运行态提供挂起状态与更及时的业务主键 /
发起人（历史这两列在运行期间可能为空）。按实例 id 去重，运行态优先覆盖。

**2. 状态过滤做了「够用就下推」**

`status=COMPLETED/TERMINATED` → `HistoricProcessInstanceQuery.Finished = true`，把历史表里绝大部分在办
行挡在门外；`status=RUNNING/SUSPENDED` → 只查运行态。剩下 `SUSPENDED` 与 `RUNNING` 的区分只能读
`SUSPENSION_STATE_`，在内存做（引擎没有「按挂起状态」的实例查询）。

`status=FAILED` 恒返回空：引擎不产出该状态（见 `InstanceReadRules.ResolveStatus` 的注释），
但保留查询路径而不是直接拒绝——将来引擎真的产出异常挂起时不用改端点。

**3. 人员范围在服务端决定，不是 scope 能替代的**

| 调用方 | 不传 `startedBy` | 显式传别人的 `startedBy` |
|---|---|---|
| 有跨人权限（`netbpm.admin` / `workflow:admin:instance` / `workflow:history:read:all`） | 全租户 | 允许 |
| 有真人主体（`X-Subject-Id` / JWT `sub`），无跨人权限 | **收窄到本人** | 403 `auth.insufficient_scope` |
| 纯服务令牌（无主体，含开发模式） | 全租户 | 允许 |

「不传就返回全租户单据」是这类列表最典型的越权漏洞，而且**不会有任何人报 bug**——功能「看起来是好的」。
`workflow:instance:read` 只说明「能查实例」，不说明「能查谁的单据」，这两层必须分开判。

**4. 游标是 `(开始时间, 实例 id)`，不是 offset**

审批数据持续写入，offset 分页在并发写入下会漏行或重复行。游标用 `<startTicks>\u001f<instanceId>` 的
Base64Url 编码；同刻多条用实例 id 做 tie-break，保证翻页边界唯一。

倒序时把游标下推成引擎的 `StartedBefore`（**含等号**），同刻的由内存按实例 id 排除——这是唯一安全的
下推：它不会漏掉任何本页该有的行。

**5. 升序被明确拒绝（400），而不是安静地慢**

引擎门面的实例查询固定「开始时间倒序」（`ProcessInstanceQuery` 的既有约定），升序无法下推游标，
只能全量拉回来再反向切片。与其返回一个「正确但要全表扫描」的分页，不如让调用方知道自己要的是什么：
需要「最早的单据」用 `startedFrom/startedTo` 收窄时间范围。

**6. 判定规则抽成 `InstanceReadRules`，列表与详情共用**

状态判定（运行态优先）与结论三级回落只有一份实现。复制两份必然漂移，而
「列表显示已通过、点进去显示已办结」这类问题排查成本极高——没人会先怀疑是两处实现不一致。

**7. 每页都要补两次往返，所以页大小上限只有 100**

`currentAssignees`（运行中 / 挂起实例查待办）与 `conclusion`（已结束实例查历史变量）各一次往返，
引擎没有「按多个实例批量查待办 / 变量」的 API。单页 100 条 ≈ 200 次查询，且补详情时按
`MaxDegreeOfParallelism = 4` 并发（连接池就那么大，打满反而更慢）。

`currentAssignees` 只收**已认领**待办的办理人；候选组未认领时为空——「这单在等谁认领」看
`currentActivityIds`，把候选人 / 候选组塞进同一个「人」列表只会让调用方分不清哪个是人哪个是组。

守门用例：`InstanceListTests`（10 项，内存宿主：列表返回 + 当前环节/办理人 + 倒序 / `processKey` 过滤 /
`businessKey` 过滤 / `status` 过滤（`RUNNING` 命中、`COMPLETED` 为空）/ 非管理员默认收窄到本人 /
查别人 403 / 跨人权限可见他人 / `limit=2` 翻页不重不漏（2 页 3 条）/ 游标非法 400 / 升序 400 / 缺 scope 403）、
`InstanceListCompletedTests`（1 项，MySQL + 历史宿主：在办与已办同时出现在列表、已办带 `conclusion`、
`status=COMPLETED` 只剩已办）。

---

#### 7.6.10 参与者维度：`ACT_HI_IDENTITYLINK` 索引 + 「我参与过的单据」+「我的已办」

**问题**：「我的已办」和「我参与过的单据」是两个最常见的列表，但它们在数据模型里**没有对应字段**——
发起人要看 `ACT_HI_PROCINST.START_USER_ID_`，审批人要看历史任务的 `ASSIGNEE_`，候选人要走
`ACT_HI_IDENTITYLINK`。此前这张历史身份关系表**只写不读**（`RecordIdentityLinkCreatedAsync` 有写入、
没有任何查询端口），所以平台层面完全没有能力回答这两个问题。

**1. 为什么必须读历史身份关系，而不是运行态**

运行态的 `ACT_RU_IDENTITYLINK` 在任务完成、实例结束时会被级联清理，而已办清单**全部**是已结束的任务。
只查运行态会得到「我没审过任何单」这个安静的错误答案——它不报错，只是列表空着。历史表只增不删
（Activiti 删除身份关系时传 `cascadeHistory = false`），它是唯一能回答这个问题的源头。

**2. 最大的坑：任务级候选行没有实例 id**

写入侧（`DapperTaskManager`）的口径是 Activiti 原样：

| 关系 | 落哪些行 | `PROC_INST_ID_` |
|---|---|---|
| 候选用户 | 任务级 `candidate` **+** 实例级 `participant`（`involveUser`） | 任务级行为空，实例级行有 |
| 候选组 | **只有**任务级 `candidate` 行 | **永远为空** |
| 办理人 / 所有者 | 只有实例级 `participant` 行 | 有 |

所以直接 `where USER_ID_ = ?` 会拿到一堆 `PROC_INST_ID_` 为空的行，**候选组这一类会被整类漏掉**。
下推 SQL 必须 `left join ACT_HI_TASKINST on t.ID_ = l.TASK_ID_`，再
`coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_)`：

```sql
select l.ID_, l.TYPE_, l.USER_ID_, l.GROUP_ID_, l.TASK_ID_,
       coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_) as PROC_INST_ID_
from ACT_HI_IDENTITYLINK l
left join ACT_HI_TASKINST t on t.ID_ = l.TASK_ID_
where coalesce(l.PROC_INST_ID_, t.PROC_INST_ID_) is not null
  and (l.USER_ID_ = @userId or l.GROUP_ID_ in @groupIds)
```

**3. 索引的三个来源合成一组「服务端算好」的角色**

`IInstanceParticipation.FindAsync(userId, groupIds)` → `instanceId → ParticipationRole[]`：

| 来源 | 条件 | 角色 |
|---|---|---|
| 历史 / 运行态流程实例 | `StartUserId = 本人` | `STARTER` |
| 历史任务 + 运行待办 | `Assignee = 本人` | `APPROVER` |
| 历史身份关系（含候选组） | `UserId = 本人 or GroupId in 我的组` | `CANDIDATE` / `PARTICIPANT` |

候选命中会**吃掉** `PARTICIPANT`：登记候选用户时引擎顺手写了一条实例级 `participant` 行
（`involveUser` 行为），两个都给会让调用方不知道看哪个，而「候选人」才是更具体的那句。

六个来源之间是**并集且互不阻断**（任一路失败只告警）：少一路的结果是「少几条单据」（降级），
让整页 500 才是真事故——「我的单据列表打不开」比「少一两条」严重得多。

**4. 历史关闭的宿主怎么办**

诚实答案：**回答不了**。历史关闭时 `ACT_HI_*` 一行都不写，参与关系物理上不存在。
此时运行态兜底只覆盖「还在跑」的那部分（这是我唯一能给的），返回的列表会偏少——
这与 v1.11 已知的「内存宿主看不到已办」是同一个口径，不是新的坑。

**5. 接入点**

- `GET /workflow/v1/instances?involvedUser=`：`LoadInvolvementAsync` 返回 `null` 表示「没有这个过滤条件」，
  返回**空映射**才表示「这个人跟任何单据都无关」——不区分这两者，一旦索引查不到东西，
  「我的单据」就会**静默整体消失**且没有任何报错。
  人员范围与 `startedBy` 同规则：无跨人权限时只能查自己，显式查别人 → 403。
  命中列表的每条会回填 `myRoles`。
- `GET /workflow/v1/tasks?scope=DONE`：**我的已办**。只一个数源下推（`HistoricTaskQuery.Assignee`），
  按办结时间倒序（最近办的在前），用 `TaskSummary.CompletedAt` 是否有值区分待办 / 已办。
  端点层对它额外要求 `workflow:history:read`（读的是审批记录，不是待办箱）。
  业务主键不在任务表上，按实例补一次（历史查不到就空着，不让一条已办因为没单号而消失）。

守门用例：`ParticipantIndexTests`（3 项，MySQL + 历史宿主：发起人 `STARTER` / 审批人 `APPROVER` /
候选人 `CANDIDATE` + **候选组只能靠 `TASK_ID_` 反查解析到实例** / 无关人员列表为空 /
`scope=DONE` 只回来自己办过且带 `completedAt`、别人的已办与自己的待办互不污染 / 查别人 403）。

#### 7.6.11 v1.13 收尾四块：可见性分级 / 已办结论 / 表单快照 / 参数覆盖

**1. 审批记录的人员级可见性（§4.6 的最后一块）**

`workflow:history:read` 只证明「这个调用方会用审批记录」，不证明「这个人跟这张单据有关」。
把两者当成一回事的结果是：任何一个拿到读权限的系统都能拉走全租户的审批意见。
判定落在 `IApprovalTimeline.GetHistoryAsync`（实例读模型之上再加一层）：

| 调用方 | 结果 |
|---|---|
| 管理 scope（`netbpm.admin` / `workflow:admin:instance` / `workflow:history:read:all`） | **全文**（含 `actorCallerId`） |
| 纯服务令牌（无真人主体） | 全文——它代表系统，没有「我」这层关系可比 |
| 本单的相关人（发起人 / 办理人 / 候选人 / 参与者） | 全文**去 `actorCallerId`**：参与者要知道的是「谁批的、批了什么」，不是调用链 |
| 其余真人 | **404（按不存在处理）** |

刻意用 404 而不是 403：403 本身就是「这张单确实存在」的证据，而它本来连在不在都不该知道。
为此给 `IInstanceParticipation` 增 `DescribeAsync(instanceId, …)`——单实例判定，
不是把 `FindAsync` 的「这个人全部单据」拿来查一个键（读放大）。

**2. 已办带回「我当时的结论」**

`ACT_HI_TASKINST` 只记「谁在几点把任务点了完成」，审批意见与选项语义从来没进过引擎的表。
所以「这批了什么」的答案**只可能在写时定稿的时间线里**。
存储层按已知 taskId 精确定位（`ListDecisionsByTaskIdsAsync`），不做「按 actor 跨实例扫」——
调用方手里的钥匙本来就是 taskId 集合，按人建二级索引要为一致性多付一次成本。
查不到时返回原样（没有 decision），**不按 `result` 变量倒推语义**：倒推出来的是一条编出来的决定。

**3. §4.5 表单快照（`nb_approval_snapshot`）**

关键取舍：**值必须带着定义走**。只存值的快照在三个月后（定义升级之后）会变成一堆无法解释的裸字符串；
带着定义的快照能把历史办理页渲染得跟当时一模一样。
键是 `task_id`（「这一次提交」的唯一锚点，也正是时间线条目里的 `formSnapshotTaskId`）；
写入顺序是**先快照后时间线**（先有键再有引用）；写失败**不阻断审批**（任务已经办结了）。

**4. `netbpm:parameters` 覆盖与 `visibleTo`**

此前 BPMN 转换器对认不出的 `extensionElements` 一律静默丢弃——「模型写了、引擎没看见」是最短的一类差异，
它不报错，只是悄悄不该做。现在它们被原样捕获到 `BaseElement.ExtensionElements`，
平台层据此解析 `apiName` / `writableBy` / `visibleTo` / 约束，覆盖 `formProperty` 的派生结果；
只在扩展里声明过的参数也能进 schema（例如纯后台任务写入的字段）。

`visibleTo` 不满足时参数**整条消失**而不是返回 null：返回 null 会让调用方分不清
「这个字段还没填」与「这个字段你不该看」。管理视角与纯服务令牌不受限制
（返回 null 表示「不受限制」，而不是「加一个 ADMIN 角色」——后者会让
`visibleTo="APPLICANT"` 的字段反而对管理员不可见，把一条安全规则解释成了它的反面）；
未声明 `visibleTo` 的老流程完全不受影响（不为新能力付兼容税）。

守门用例：`HistoryVisibilityTests`（3 项，内存宿主：相关人看得到条目但 `actorCallerId` 为空 /
无关真人 404 且错误码与「真的不存在」一致 / `history:read:all` 保留 `actorCallerId`）、
`ApprovalSnapshotTests`（5 项，MySQL：快照含当时的定义与值且不含 `approvalResult` /
时间线 `formSnapshotTaskId` 指向快照 / 无快照 404 / 已办带 `decision`（选项 + 语义 + 意见）/
绕过平台端点办结的任务仍在已办里但没有 decision）、
`ParameterOverridesTests`（3 项：覆盖出现在 schema 且含约束与角色 / `visibleTo` 双向过滤 +
未声明参数对所有人可见 / 老流程不受影响）。

---

## 8. 路线图

| 阶段 | 周期 | 交付 | 出口条件（不通过不进下一阶段） |
|---|---|---|---|
| **S1 契约与骨架** | 3 周 | `Platform.Contracts`（**已落地**）、OpenAPI v1 草案、错误码表、`/workflow/v1` 只读端点、契约快照 CI | 用 3 个不同语言的客户端生成器各生成一次，均能编译；`openapi-diff` 无 breaking |
| **S2 身份与授权** | 4 周 | 三元组身份、`nb_tenant`/`nb_api_client` 注册表、密钥轮换、RS256+JWKS、scope 表、按 client 配额、幂等契约、生产模式禁头身份 | 跨租户 404、越权 403、纯服务 submit 403、客户端白名单 404、密钥轮换期双活、幂等重放 —— 6 项集成用例全绿 |
| **S3 业务标准** | 5 周 | 目录、语义字典（三码）、参数 schema、表单快照、状态机、timeline、结论推导 | 部署期守卫能拒绝「选项无映射」的 BPMN；旧 BPMN 不改一字仍可跑（回归） |
| **S4 独立部署** | 4 周 | 平台存储、外部任务、CloudEvents 投递、`Platform.Service` 容器 | 一条含 `serviceTask` 的流程在独立部署下端到端跑通；worker 宕机后任务被重投 |
| **S5 治理与 SDK** | 3 周 | 官方 .NET SDK、配额与限流看板、审计、SLO、迁移与接入文档 | 旧 SDK 客户端可无损对接新服务（向前兼容用例）；p95 达标；两家业务系统按 Runbook 自助接入成功 |

总周期约 **19 周**，与 6 节人日一致（2 人并行）。

> S2 从 3 周调到 4 周，多出来的是客户端注册表（`nb_api_client` + 白名单 + 轮换 + 配额）。
> 这部分不能挪到 S5：它决定审计字段与幂等键的形状，晚做就等于把 S3/S4 的表全部改一遍。

---

## 9. 守门测试清单

| 层 | 用例 | 断言要点 |
|---|---|---|
| 契约 | `ContractSnapshotTests` | 提交的 `openapi.v1.json` 与代码生成结果**字节一致**；变更必须显式更新快照文件（CI 拒绝"顺手改"） |
| 契约 | `ContractSerializationTests` ✅（`PlatformContractTests`） | `Platform.Contracts` 的枚举一律序列化为 `SCREAMING_SNAKE_CASE`、属性 `camelCase`、时间为 RFC 3339 UTC；**禁止**出现数字枚举（防止新增值被静默当 0） |
| 契约 | `CompatibilityTests` | 上一版 SDK 打新服务，全部端点通过（只用旧字段） |
| 语义 | `DecisionVocabularyTests` | 未知 `optionId` → 422；`semantic` 与 `optionId` 冲突 → 422；会签聚合 `ALL_APPROVED` 正确 |
| 语义 | `SubmitValidationTests` ✅（`TaskSubmitTests`） | 非选项 / 语义冲突 / 需意见 / 需目标节点 / 白名单 → 422；非办理人 → 404；纯服务令牌 → 403；`expectedRevision` 冲突 → 409 |
| 语义 | `InstanceConclusionTests` ✅ | 结束实例 `status=COMPLETED`；三级回落三态：`DECLARED`（`netbpm_outcome` 覆盖 `result`）/`LAST_DECISION`（`result` → 中文文案）/`FALLBACK`（`CLOSED`）；运行中实例 `conclusion=null` |
| 语义 | `TimelineTests` ✅（`ApprovalTimelineTests`） | 发起写 `INSTANCE_STARTED`、提交写 `TASK_COMPLETED`（`optionId`/`semantic`/`comment`/`actorId`+`actorCallerId`）；纯服务令牌 `actorId` 为空；`?afterSequence=` 不重复返回；`?limit=` 带 `nextCursor` 不重不漏；未知实例 404；缺 `history:read` 403 |
| 语义 | `ParameterSchemaTests` ✅（`InstanceStartTests` + `ParameterOverridesTests`） | 必填缺失 → 422 **且不产生实例**（断言运行中实例数为 0）；撞保留名 → 400；类型/约束不符 → 422；`netbpm:parameters` 覆盖 `apiName`/`writableBy`/`visibleTo`/约束；`visibleTo` 不满足时参数**整条消失**（不是 null） |
| 语义 | `InstanceListTests` ✅ | 双源合并后已办与在办同时可见；`processKey`/`businessKey`/`status` 过滤；`limit` 翻页不重不漏；**非管理员默认只看本人**、查别人 403、跨人权限可见他人；游标非法 / 升序 → 400 |
| 语义 | `InstanceListCompletedTests` ✅ | MySQL + 历史宿主：已办实例出现在列表且 `status=COMPLETED`、`conclusion` 与详情同一套推导；`status=COMPLETED` 过滤只剩已办 |
| 语义 | `HistoryVisibilityTests` ✅ | 相关人可见但去 `actorCallerId`；无关真人 → **404**（不是 403）；`history:read:all` 保留全文 |
| 语义 | `ApprovalSnapshotTests` ✅ | 表单快照含当时的定义 + 值（不含 `approvalResult`）；时间线 `formSnapshotTaskId` 指向快照；无快照 → 404；已办带 `decision`；引擎侧直接办结的任务没有 `decision` |
| 语义 | `ParameterOverridesTests` ✅ | `netbpm:parameters` 覆盖生效；`visibleTo` 对申请人 / 审批人双向过滤；未声明参数与老流程不受影响 |
| 语义 | `ParticipantIndexTests` ✅ | 参与人索引三类角色：`STARTER`（发起人）/ `APPROVER`（历史任务办理人）/ `CANDIDATE`（**候选组必须靠 `TASK_ID_` 反查实例**，否则整类漏）；无关人员列表为空；`scope=DONE` 只回自己办过且带 `completedAt`，待办不出现在已办里；无跨人权限查别人 403 |
| 语义 | `DeployGuardTests` ✅（`ApprovalVocabularyTests`） | 选项无映射的 BPMN：宽容模式随部署响应告警，严格模式（`rejectUnmapped`）部署失败，错误信息含节点 id 与选项 id |
| 安全 | `IdentityTests` | 无凭据 401；头身份在 Production 被忽略；`client_credentials` submit → 403 |
| 安全 | `ClientRegistryTests` | 未注册 client → 401；`Suspended`/`Revoked` → 401；轮换过渡期内新旧密钥**同时可用**，过期后旧的立即 401 |
| 安全 | `ClientProcessGrantTests` ✅（`ProcessCatalogTests`） | 客户端未被授权 `process_key` → 目录里看不到它，按 id 访问返回 **404**（不是 403） |
| 安全 | `TenantIsolationTests` | A 租户查 B 租户实例 → 404（不是 403，不泄露存在性） |
| 限流 | `QuotaTests` ✅（`RateLimitByClientTests`） | 同一 client 超配额 → 429 + `Retry-After`；另一 client **不受影响**（分区分的是 client，不是 IP） |
| 幂等 | `IdempotencyTests` | 同键同体 → 200 + `Idempotent-Replay: true`，且**只产生一个实例**；同键异体 → 409 |
| 一致性 | `DualModeTests` | 同一 BPMN 在嵌入式与独立服务下，`submit → history` 的 timeline 语义等价 |
| 外部任务 | `ExternalTaskTests` | 锁过期后任务可被另一 worker 取走；`retries=0` 进死信 |
| 事件 | `WebhookTests` | 签名可验证；重复投递按 `eventId` 可去重；死信可重放 |
| 性能 | `LoadTests` | 读 p95 < 80ms、写 p95 < 200ms（单实例 100 并发，MySQL 5.7 现网配置） |

---

## 10. 风险与取舍

| 风险 | 影响 | 对策 |
|---|---|---|
| 服务任务独立化改造量被低估 | 高（阻塞 S4） | 先做**部署期守卫**：无 `topic` 且无 `class` 的 serviceTask 直接拒部署，让问题在 S1 就暴露 |
| 语义字典与既有 BPMN 不一致 | 中（部署失败） | 守卫给**可执行的修复建议**（打印缺失的 `optionId` 与建议 `semantic`）；提供一次性扫描工具 |
| 平台表与 `workflow_type` 双写漂移 | 中 | 双写期只允许**平台表写入**、`workflow_type` 由触发器/同步任务投影；一个版本后下线旧表 |
| 结论推导三级回落产生歧义 | 中 | 推导结果带 `conclusion.source: "DECLARED"｜"LAST_DECISION"｜"FALLBACK"`，审计可分辨 |
| 契约层过早膨胀 | 中 | `Platform.Contracts` 只放**对外**类型；内部模型一律不进 |
| 一次性 v1 设计错误 | 高 | S1 结束后做一次**外部评审**（找 2 个真实调用方各写 1 个接入 demo），再冻结 v1 |
| 客户端注册成为新的运维瓶颈 | 中 | 自助注册（管理端表单 + 一次性 secret 展示页）；默认配额模板；轮换前 14 天自动提醒 |
| 多系统共用一条流程导致变更协调成本 | **高** | `nb_client_process_grant` 反查调用方 → 发布前自动生成「影响面清单」；选项只允许追加（§2.5.5） |
| 租户列上线后不可回收 | 中 | 一期就按 `tenant_id NOT NULL` 建表（默认 `default`）；不要留「以后再加」的选项 |

### 明确不做

1. ❌ 不做通用 BPMN 全语法（保持现有子集），不为平台化扩语言。
2. ❌ 不做表单引擎/表单设计器（参数 schema 只服务校验，不服务渲染）。
3. ❌ 不做附件存储（只存引用）。
4. ❌ 不把 `/api/runtime` 那套 `identity-links`/`revision`/`variables` 直接塞进业务面。
5. ❌ 不做 SOAP / XML over HTTP（BPMN XML 只作为「定义原文」返回，不参与业务交互）。
6. ❌ 不为兼容而在 `v1` 内做语义开关（`?legacy=true` 这类）——要兼容就开新主版本。

---

## 附录 A：决策记录（ADR 摘要）

| # | 决策 | 理由 | 代价 |
|---|---|---|---|
| ADR-01 | 对外用 `workflow/v1`，`/api/runtime` 降级为 `engine/v1` | 引擎原语会把 Activiti 概念漏给业务方 | 需维护两套文档 |
| ADR-02 | 审批决定三码分离（optionId / semantic / outcomeClass） | 让流程可自由加选项而不破坏调用方 | 需字典表 + 部署守卫 |
| ADR-03 | 参数对外分层、对内扁平（镜像写入裸变量） | 保住 BPMN/EL 兼容，同时拿到 schema | 存在两份表示，需保证同步 |
| ADR-04 | 用 RFC 9457，不用 `{code,data}` 信封 | 与 HTTP 语义一致，网关/客户端零适配 | 与国内常见习惯不同，需培训 |
| ADR-05 | cursor 分页，弃 offset | offset 在并发写入下会漏行 | 实现复杂度略高 |
| ADR-06 | 纯服务令牌禁止 submit | 审批结论必须落到真人 | 部分自动化场景需走 token exchange |
| ADR-07 | 外部任务用独立表，不复用 `ACT_RU_JOB` | 生命周期与重试语义不同 | 多一套表与执行器 |
| ADR-08 | 平台表 `nb_` 前缀、不改 `ACT_*` | 保住引擎升级自由度与表结构红线 | 跨表 join 需注意命名 |
| ADR-09 | **一期就做租户 + 多客户端（中台形态）** | 多系统共用一套引擎时，单密钥会在泄漏/限流/审计三处同时失效 | 多 10 人日（M16）；客户端注册成为新的运维动作 |
| ADR-10 | `tenant_id` 列 NOT NULL，单租户期也写 `default` | 空租户 = 漏 `WHERE` 就跨界读，且事后加列无法回填历史 | 迁移脚本必须带默认值 |
| ADR-11 | 枚举序列化用 `SCREAMING_SNAKE_CASE` 且转换器随类型走 | 数字枚举让「新增值」变成静默破坏；转换器跟着类型才能保证独立服务与 SDK 一致 | 需自定义 `JsonConverter`（已实现） |
| ADR-12 | `Platform.Contracts` 不引用引擎工程 | 引擎升级不应等于对外契约破坏 | 需要一层显式映射（DTO ↔ 引擎模型） |

## 附录 B：错误码表（与 `Platform.Contracts/Common/ErrorCodes.cs` 一一对应）

| code | HTTP | 场景 |
|---|---|---|
| `auth.missing_credential` | 401 | 未带令牌 |
| `auth.invalid_token` | 401 | 签名/过期/受众不符 |
| `auth.insufficient_scope` | 403 | scope 不足 |
| `auth.subject_required` | 403 | 纯服务令牌调 submit |
| `auth.client_disabled` | 401 | 客户端被停用 / 密钥已吊销 |
| `tenant.mismatch` | 404 | 跨租户（按不存在处理） |
| `process.not_found` | 404 | 流程 key 不存在，或客户端未被授权 |
| `process.not_deployed` | 409 | key 存在但无生效定义 |
| `process.suspended` | 409 | 定义已挂起 |
| `process.version_not_found` | 404 | 指定版本不存在 |
| `instance.not_found` | 404 | 实例不存在（或不属于本租户） |
| `instance.not_running` | 409 | 已结束/已终止/已挂起 |
| `task.not_found` | 404 | 任务不存在（可能已完成） |
| `task.already_completed` | 409 | 重复提交 |
| `task.not_assignee` | 403 | 非办理人提交 |
| `task.already_claimed` | 409 | 已被他人认领 |
| `decision.not_in_option_set` | 422 | 选项不属于该节点 |
| `decision.semantic_mismatch` | 422 | semantic 与 optionId 不符 |
| `decision.comment_required` | 422 | 该选项要求意见 |
| `decision.target_node_required` | 422 | 退回需指定目标节点 |
| `decision.target_node_invalid` | 422 | 目标节点不在允许范围 |
| `parameter.required_missing` | 422 | 必填参数缺失 |
| `parameter.type_invalid` | 422 | 类型/规则不符 |
| `parameter.readonly` | 422 | 参数不可写 |
| `parameter.conflict_with_reserved` | 400 | 与 `sys.*`/保留变量撞名 |
| `concurrency.revision_mismatch` | 409 | `expectedRevision` 不符 |
| `concurrency.etag_mismatch` | 412 | `If-Match` 失败 |
| `idempotency.key_reused` | 409 | 同键异体 |
| `engine.expression_error` | 500 | EL 求值失败 |
| `engine.delegate_missing` | 500 | 委托解析失败（独立部署下应为 `external_task.no_worker`） |
| `external_task.lock_expired` | 409 | 锁已过期，任务被回收 |
| `external_task.no_worker` | 503 | 无 worker 领取该 topic |
| `request.malformed` | 400 | 请求体结构不合法 |
| `internal.error` | 500 | 未预期异常（只回 `traceId`，不回堆栈） |
| `rate_limited` | 429 | 超配额 |

## 附录 C：身份模型（已落地）

契约层已实现（`src/NetBpm.Platform.Contracts/Identity/CallerIdentity.cs`），服务端中间件改造（M4）按它填值：

```csharp
public sealed record CallerIdentity(
    string CallerId,                       // azp / client_id：哪个系统在调
    string? SubjectId,                     // sub：以谁的身份；纯服务为 null
    string TenantId,                       // 必须有签名，不接受请求头
    CallerMode Mode,                       // USER_TOKEN | ON_BEHALF_OF | CLIENT_CREDENTIALS
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string> Groups)
{
    public bool IsOnBehalfOf => SubjectId is not null && !string.Equals(CallerId, SubjectId, StringComparison.Ordinal);

    // 判据是「有没有真人主体」，不是「有没有 scope」——纯服务即便被授予 submit 也必须拒绝
    public bool CanSubmitApproval => SubjectId is not null && Mode != CallerMode.ClientCredentials;
}
```

**已按此落地（v1.3）**，与本文原计划的差异只有两处，都以代码为准：

| 既有成员 | 实际做法 |
|---|---|
| `RequestContext.UserId` | 保留为 `SubjectId` 的**兼容别名**（`get => SubjectId; set => SubjectId = value;`），60 多个端点零改动 |
| `RequestContext.CallerId` / `CallerName` | 新增；`Client` 模式取注册表里的 `clientId` / `name`（JWT 的 `azp` 留待 RS256+JWKS 那批一起做） |
| `RequestContext.RequestId` | 新增；回显 `X-Request-Id`（缺省用框架 trace id），401/403 也带，失败请求同样能对齐日志 |
| `RequestContext.AuthMode` | `None` / `ApiKey` / `Jwt` 不动，另加 **`Client`**。⚠️ 原计划的「另加 `CallerMode`」**没有**采用：`CallerMode` 是对外契约词汇，不该出现在内部上下文里，映射放在平台 API 层做（`Jwt→USER_TOKEN`、`Client`+有主体→`ON_BEHALF_OF`、`Client` 无主体→`CLIENT_CREDENTIALS`） |
| `AllowedTenants` | **没有废弃**——旧 `ApiKey`（`ApiKeyTenants` 白名单）还在用。`Client` 模式下它被写成单元素集合「该客户端所属租户」，于是 `IsTenantRestricted` 自动成立 |

## 附录 D：S1 已落地清单（`src/NetBpm.Platform.Contracts`）

| 文件 | 内容 | 对应文档章节 |
|---|---|---|
| `Common/PlatformScopes.cs` | 15 个对外 scope + `IsSatisfiedBy`（`workflow:*` 不覆盖 `:admin` / `:onbehalf`）；v1.30 增 `workflow:admin:audit:read`，现 **17** 个 | §3.3 |
| `Common/ErrorCodes.cs` | 36 个稳定错误码常量 | 附录 B |
| `Common/PlatformProblem.cs` | RFC 9457 响应体、字段级错误、`CursorPage<T>`、`SortDirection` | §3.5 / §3.6 |
| `Common/ScreamingSnakeCaseEnumConverter.cs` | 枚举统一 `SCREAMING_SNAKE_CASE`，策略随类型走 | ADR-11 |
| `Identity/CallerIdentity.cs` | 三元组身份、`CallerMode`、`ClientDescriptor`、`IssuedClientSecret` | §2.5 / §3.2 |
| `Process/ProcessDescriptor.cs` | 流程目录、入口、页面地址、版本与校验和 | §4.1 |
| `Approval/ApprovalVocabulary.cs` | `ApprovalOption`（三码）、`ApprovalDecision`、`NodeOutcome` 聚合 | §4.2 |
| `Parameters/ParameterSchema.cs` | 参数定义（含 `ApiName`/`VariableName` 镜像）、快照 | §4.3 |
| `Instances/InstanceContracts.cs` | 5 态实例、`InstanceView`、`InstanceConclusion`（含 `source`） | §4.4 / §4.7 |
| `Tasks/TaskContracts.cs` | 待办视图与列表、`SubmitTaskRequest`/`SubmitTaskResult`、`AttachmentRef` | §4.4.2 |
| `Forms/FormContracts.cs` | 表单字段与快照（历史回显用） | §4.5 |
| `History/TimelineContracts.cs` | append-only 时间线（`sequence` 增量拉取） | §4.6 |
| `Admin/ClientContracts.cs` | 客户端注册 / 修改 / 用量（多系统接入的管理面） | §2.5.2 |

**已验证**：工程零警告零错误编译通过；序列化实测输出 `"semantic": "RETURN"`、`"aggregation": "ALL_APPROVED"`、
属性 `camelCase`（即本文档全部示例的真实形状）。

**尚未做**（下一步）：流程目录 / 审批字典 / 参数 schema 的服务与映射、`Platform.Api`（三组端点 + 授权过滤器）、
`openapi.v1.json` 快照与契约 CI。客户端注册与租户（`nb_*` 三表 + `IClientRegistry`）已于 v1.2 落地，见附录 E。

---

## 附录 E：S2 第一段落点（`NetBpm.Persistence/Platform`）

| 文件 | 内容 | 备注 |
|---|---|---|
| `ClientSecretFormat.cs` | 密钥格式 `nbc.{clientId}.{token}`、SHA-256 摘要、定长比较、日志脱敏 `Describe` | 明文只在签发响应出现一次 |
| `TenantRegistry.cs` | `ITenantDirectory`（只读）/ `ITenantRegistry`（登记 / 列 / 停用），Dapper + 内存实现 | 只读接口独立，鉴权组件从类型上就写不了租户 |
| `ClientRegistry.cs` | `IClientRegistry`、`ClientRegistryOptions`、`ClientAuthenticationStatus`、`ClientRegistryException`、scope 高危判定 | 高危 scope 需 `allowPrivilegedScopes` 显式确认 |
| `DapperClientRegistry.cs` | 真库实现：事务内写客户端 + 白名单；鉴权热路径单次查询 + 分钟级节流回写 | 列名显式 `as` 别名（Dapper 不做下划线→驼峰） |
| `InMemoryClientRegistry.cs` | 内存实现：语义与 Dapper 版逐条对齐 | 无库模式与单测用 |
| `PlatformTimestamps.cs` | 库内 UTC ↔ `DateTimeOffset` | 两方言读回的 `DateTimeKind` 不同，统一按 UTC 解释 |

三张表的 DDL 在 `SchemaCatalog.cs`（`TenantTable` / `ApiClientTable` / `ClientProcessGrantTable`），
与 `ACT_*` 严格分节；`DatabaseScriptTests.ApplicationTables` 已加入这三张表做存在性守门。

## 附录 F：S2 第二段落点（身份与接入管理）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Rest/Security/RequestContext.cs` | 三元组 + `RequestId` + `CanSubmitApproval` + `AllowedProcessKeys`；`UserId` 兼容别名 | `AuthMode.Client` 新增；`IsTenantRestricted` 纳入客户端 |
| `src/NetBpm.Rest/Security/AuthenticationMiddleware.cs` | 第三条凭据通道（`nbc.*`）、`X-Subject-Id`、请求 id 回显 | 前缀即承诺；租户头不符 403；主体声明需代客 scope |
| `src/NetBpm.Rest/Security/PlatformSecurity.cs` | `AddNetBpmPlatformSecurity`（有库 Dapper / 无库内存）+ 启动引导 + 匿名告警 | 两个接口指向同一实例，避免「注册表写了、鉴权看不见」 |
| `src/NetBpm.Rest/Security/AdminAuthorization.cs` | 放行分支收紧为 `None` / 旧 `ApiKey` | 客户端逐条命中 scope |
| `src/NetBpm.Rest/Security/RequestIdentity.cs` | 租户解析对客户端与 JWT 同口径（声明不同 → 403） | 堵住 `?tenantId=` 跨租户列表 |
| `src/NetBpm.Rest/Endpoints/ClientAdminEndpoints.cs` | 租户 / 客户端管理 9 个端点 + 审计 | 高危 scope 需显式确认；不能自停用租户 |
| `src/NetBpm.Rest/Endpoints/AuthEndpoints.cs` | `/api/auth/me` 增 `subject` / `caller` / `callerName` / `canSubmitApproval` / `requestId` | 兼容字段 `user` 保留 |
| `src/NetBpm.Rest/GlobalExceptionHandler.cs` | `ClientRegistryException` → 400 + 稳定码 | 客户端按码分支，不看文案 |
| `src/NetBpm.Persistence/Admin/AdminAuditStore.cs` | 6 个接入审计动作 + 2 个目标类型 | 与引擎审计同一张表 |
| `src/NetBpm.Platform.Contracts/Admin/ClientContracts.cs` | `TenantView` / `RegisterTenantRequest` / `SetTenantStatusRequest` | 对外不暴露 persistence 记录 |
| `src/NetBpm.Platform.Contracts/Common/ErrorCodes.cs` | `tenant.name_required` / `tenant.self_suspend_forbidden` / `client.name_required` / `request.body_required` | 40 个稳定码 |
| `src/NetBpm.Rest/appsettings.json`、`src/NetBpm.Admin/appsettings.json` | `Security:RequireCredential` / `Clients` / `Tenants` | 两宿主同基线 |

测试：`tests/NetBpm.IntegrationTests/ClientAuthenticationTests.cs`（15 项，内存宿主，秒级、不依赖 MySQL）。

---

## 附录 G：S3 第一段落点（流程目录 + 按客户端限流）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Platform.Contracts/Process/ProcessCapabilities.cs` | `ProcessCapabilities`（6 个字段）+ `ApprovalSemantics`（默认字典 + `OutcomeClassOf`） | 能力是「**你**能对它做什么」，与流程状态分开；未识别的 optionId 一律 `CUSTOM`，不猜 |
| `src/NetBpm.Platform.Contracts/Process/ProcessDescriptor.cs` | 增 `Capabilities`；`CanStart` 改为转调 `Capabilities.CanStart` | 单一真源，避免两处判定漂移 |
| `src/NetBpm.Persistence/Platform/ProcessCatalogStore.cs` | `ProcessCatalogEntry` + `IProcessCatalogStore` + Dapper / 内存实现 | 只读接口；覆盖行的可空列 = 「跟随引擎 / BPMN」 |
| `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` | `ProcessCatalogTable`（`nb_process_catalog`） | 主键 `(tenant_id, process_key)`；不存版本与校验和 |
| `src/NetBpm.Rest/Platform/ProcessCatalog.cs` | `IProcessCatalog` + 实现：三层合并、白名单、cursor 分页、BPMN 派生缓存 | 引擎挂起优先；无租户部署视为全局共享 |
| `src/NetBpm.Rest/Platform/ProcessCatalogServiceExtensions.cs` | `AddNetBpmProcessCatalog`（有库 Dapper / 无库内存 + `IMemoryCache`） | 派生结果按定义 id 缓存 30 分钟 |
| `src/NetBpm.Rest/Platform/PlatformProblems.cs` | RFC 9457 错误构造 + `InsufficientScope` / `ProcessNotFound` | 未授权与不存在共用 404 |
| `src/NetBpm.Rest/Security/PlatformAuthorization.cs` | 平台面 scope 判定（`None`/旧 `ApiKey` 放行，`Client` 逐条命中，JWT 超管等价平台管理员） | scope 只是能力上限，数据授权另行校验 |
| `src/NetBpm.Rest/Endpoints/ProcessCatalogEndpoints.cs` | 5 个读端点（`/workflow/v1/processes**`） | 与 `/api/**` 分开：变更纪律不同 |
| `src/NetBpm.Rest/RateLimiting/FixedWindowRateLimiter.cs` | 固定窗口计数器（每分区一把锁、分区表满 fail-open） | 拿得到 `Remaining`；配额按 key 传 |
| `src/NetBpm.Rest/RateLimiting/ClientRateLimitMiddleware.cs` | 分区解析（client → user → IP）+ `RateLimit-*` 头 + 429 问题体 | 必须排在鉴权之后 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 管道顺序改为「鉴权 → 限流 → 幂等」；替换框架 `RateLimiter`；映射目录端点 | 顺序是结论，不是巧合 |
| `src/NetBpm.Rest/Security/RequestContext.cs` | 增 `QuotaPerMinute` | 配额随身份带出，限流热路径不回表 |
| `src/NetBpm.Rest/Security/AuthenticationMiddleware.cs` | 保护前缀扩到 `/api` `/workflow` `/engine` `/admin` | 否则新平台面匿名可达 |
| `src/NetBpm.Rest/appsettings.json` | `RateLimit:ClientPerMinute` | 客户端未配置配额时的兜底 |

测试：`tests/NetBpm.IntegrationTests/ProcessCatalogTests.cs`（14 项，内存宿主）、
`RateLimitByClientTests.cs`（4 项）、`tests/NetBpm.UnitTests/PlatformContractTests.cs`（16 项）。

现网补表：与 §7.4 同一条命令。`nb_process_catalog` **可以是空表**——空表就是「全部由 BPMN 派生」，
目录照样可用；覆盖表不可读时目录也降级为无覆盖（只记警告，不阻断）。

---

## 附录 H：S3 第二段落点（参数规范）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Rest/Platform/ProcessCatalog.cs` | `DerivedProcess` 增 `ParameterSchema`；`DeriveParameterSchema` / `BuildParameterDefinition` / `MapParameterType`；接口增 `GetParameterSchemaAsync` | 只排除 `approvalResult`；`comment` 保留为业务参数；按 `variable` 去重保留首个环节 |
| `src/NetBpm.Rest/Platform/ProcessCatalog.cs` | `HasParameterSchema` 改为 `Parameters.Count > 0` | 能力声明不再依赖没人写的 `parameterSchemaVersion` |
| `src/NetBpm.Rest/Endpoints/ProcessCatalogEndpoints.cs` | 第 6 个读端点 `GET /processes/{key}/parameters/schema` | scope 仍走 `workflow:process:read` |

测试：`ProcessCatalogTests` 新增 5 项（派生字段与类型、排除审批选项、空 schema 且能力闭环、白名单 404）。

下一步（S3 第三块）：`netbpm:parameters` 覆盖扩展（`writableBy` / `visibleTo` / `pattern` / 数值区间），
或直接进语义字典表 `nb_approval_dictionary` + 部署期守卫（§4.2 的「标准可执行」关键）。

---

## 附录 I：S3 第三段落点（审批语义字典）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` | `ApprovalDictionaryTable`（`nb_approval_dictionary`） | 主键 `(tenant_id, process_key, node_id, option_id)`；`semantic`/`outcome_class` 存文本词汇 |
| `src/NetBpm.Persistence/Platform/ApprovalDictionaryStore.cs` | `ApprovalDictionaryEntry` + `IApprovalDictionaryStore` + Dapper / 内存实现 | 只读接口；只存流程自定义选项，1/2/3 由默认字典覆盖不落库 |
| `src/NetBpm.Rest/Platform/ApprovalVocabulary.cs` | `IApprovalVocabulary` + 实现：两层合并（默认字典 + 流程声明），未映射一律 `CUSTOM` | 节点级覆盖流程级；`outcomeClass` 由 `semantic` 推导 |
| `src/NetBpm.Rest/Platform/ApprovalVocabularyServiceExtensions.cs` | `AddNetBpmApprovalVocabulary`（有库 Dapper / 无库内存） | 流程声明按（租户, 流程）缓存 10 分钟 |
| `src/NetBpm.Rest/Diagnostics/ApprovalVocabularyGuard.cs` | 部署期语义守卫：宽容告警 / 严格拒绝无映射选项 | 返回 `ValidationError[]` 而非只打日志，随部署响应回传 |
| `src/NetBpm.Rest/Endpoints/DesignerEndpoints.cs` | 部署后自检接入语义守卫（宽容模式） | 与委托自检 `DeploymentDelegatePrecheck` 并列 |

测试：`tests/NetBpm.IntegrationTests/ApprovalVocabularyTests.cs`（8 项，内存宿主）。

下一步（S3 第四块）：待办端点 `/workflow/v1/tasks`——把 `IApprovalVocabulary` 接入 `TaskView.Options`
（调用方渲染审批按钮的消费端），或继续 `netbpm:parameters` 覆盖扩展补 `writableBy`/`visibleTo`/约束。

---

## 附录 J：S3 第四段落点（待办读模型）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Rest/Platform/TaskCatalog.cs` | `ITaskCatalog` + 实现：`TaskEntity` + BPMN `UserTask` + `IApprovalVocabulary` → `TaskView` / `TaskSummary` | `options` 服务端算好；`currentUserCanSubmit` 判 assignee / 候选用户 / 候选组命中 |
| `src/NetBpm.Rest/Platform/TaskCatalogServiceExtensions.cs` | `AddNetBpmTaskCatalog`（Scoped） | 依赖引擎 + 定义仓储 + 语义字典，均已由宿主注册 |
| `src/NetBpm.Rest/Endpoints/TaskEndpoints.cs` | `GET /workflow/v1/tasks` + `GET /workflow/v1/tasks/{id}` | `scope=MINE\|CLAIMED\|ALL`，`ALL` 需管理权限；未授权按 404 |
| `src/NetBpm.Rest/Platform/PlatformProblems.cs` | 增 `TaskNotFound`（404，与 `ProcessNotFound` 同口径） | 未授权 / 不存在 / 已完成共用 404，不泄露待办归属 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 注册 `AddNetBpmTaskCatalog` + `MapTaskEndpoints` | 待办读模型与目录并列，变更纪律一致 |

测试：`tests/NetBpm.IntegrationTests/TaskCatalogTests.cs`（5 项，内存宿主 + 内联审批 BPMN）。

下一步（S3 第五块）：提交审批端点 `POST /workflow/v1/tasks/{id}/submit`（`SubmitTaskRequest` 契约已就绪，
消费 `options` 的 `requiresComment` / `requiresTargetNode` 校验 + `semantic` 一致性校验 422）；
或 `netbpm:parameters` 覆盖扩展补 `writableBy` / `visibleTo` / 约束。
（已落地，见附录 K。）

---

## 附录 K：S3 第五段落点（实例读模型 + 提交审批）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Rest/Platform/InstanceCatalog.cs` | `IInstanceCatalog` + 实现：执行树 + 历史 + 待办 → `InstanceView` | 存在性双查（执行树 + 历史）；`Status` 运行态优先；结论三级回落（`DECLARED` → `LAST_DECISION` → `FALLBACK`，见附录 M） |
| `src/NetBpm.Rest/Platform/InstanceCatalogServiceExtensions.cs` | `AddNetBpmInstanceCatalog`（Scoped） | 实例读模型与待办读模型并列注册 |
| `src/NetBpm.Rest/Platform/TaskSubmitService.cs` | `ITaskSubmitService` + 实现：校验链 → 落变量 → 完成任务 → **（驳回时）改道到 `targetNodeId`** → 组装结果 | `result=optionId` + 参数剥 `param.` 前缀镜像；`expectedRevision` 乐观锁 409；驳回改道见下 |
| `src/NetBpm.Rest/Platform/TaskSubmitServiceExtensions.cs` | `AddNetBpmTaskSubmit`（Scoped） | 依赖引擎 + 待办读模型 + 实例读模型 |
| `src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` | `GET /workflow/v1/instances/{id}` | scope `workflow:instance:read`；未授权 / 不存在 404 `InstanceNotFound` |
| `src/NetBpm.Rest/Endpoints/TaskEndpoints.cs` | 增 `POST /workflow/v1/tasks/{id}/submit` | scope `workflow:task:submit`；捕获 `SubmitDecisionException` 转 RFC 9457 |
| `src/NetBpm.Rest/Platform/PlatformProblems.cs` | 增 `InstanceNotFound`（404） | 与 `ProcessNotFound` / `TaskNotFound` 同口径 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 注册 `AddNetBpmInstanceCatalog` + `AddNetBpmTaskSubmit` + `MapInstanceEndpoints` | 与目录 / 待办并列，变更纪律一致 |

> **补记（2026-11-02）：驳回改道闭环。** 之前 `targetNodeId` 只做 422 校验、不驱动流程，
> 「驳回到指定节点」在服务端是个空承诺。现在 `SubmitAsync` 在完成任务之后调用
> `IProcessEngine.MoveActivityToAsync`，并额外落一条 `INSTANCE_JUMPED` 时间线。
> **顺序是先完成、后改道**：反过来会让任务被「跳转」删掉而不是「审批完成」，结论与任务历史都落不下来。
> 代价是改道的落点是「完成后的下一个等待点」——审批环节后面直接挂结束事件时（提交即结束），
> 没有等待点可供改道，此时结论照常生效、改道失败只进日志，不自欺欺人地报成功。
> 用例：`TaskSubmitTests.Submit_ShouldMoveBackToTargetNode_WhenDecisionCarriesTargetNode`
> 与 `Submit_ShouldKeepDecision_WhenJumpNotPossible`（各钉住成功与失败两条路）。

测试：`tests/NetBpm.IntegrationTests/InstanceCatalogTests.cs`（2 项，内存宿主）、
`TaskSubmitTests.cs`（8 项，内存宿主）、`InstanceConclusionTests.cs`（MySQL + 历史宿主；
v1.8 落地时 2 项，v1.10 补 2 项三级回落用例）。

下一步（S3 第六块）：`POST /workflow/v1/instances` 发起（`StartInstanceRequest` 契约已就绪，
参数镜像 + `param.` 前缀 + 业务主键发号）。（已落地，见附录 L。）

---

## 附录 L：S3 第六段落点（发起端点）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Platform.Contracts/Instances/InstanceContracts.cs` | 增 `StartInstanceResult`（`InstanceId` / `ProcessKey` / `BusinessKey` / `Instance?`） | `Instance` 复用 `InstanceView` 单一出口；仅「立即结束且无历史」时为 null |
| `src/NetBpm.Rest/Platform/InstanceStartService.cs` | `IInstanceStartService` + 实现 + `IBusinessKeyGenerator`（默认毫秒时间戳 + 进程内序号）+ `StartInstanceException` | 发起人只取令牌 `subject`；参数镜像 `param.` 剥前缀；未声明参数按「无 schema」放行；类型只校验不强转（EL 收窄） |
| `src/NetBpm.Rest/Platform/InstanceStartServiceExtensions.cs` | `AddNetBpmInstanceStart`（发号器单例 + 编排 Scoped） | 与提交审批编排并列注册 |
| `src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` | 增 `POST /workflow/v1/instances`（201 + `Location`） | scope `workflow:instance:start`；捕获 `StartInstanceException` 转 RFC 9457 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 注册 `AddNetBpmInstanceStart` | 与目录 / 待办 / 实例读模型并列 |

测试：`tests/NetBpm.IntegrationTests/InstanceStartTests.cs`（7 项，内存宿主）。

下一步（S3 收尾候选）：§4.5 表单快照（`nb_approval_snapshot`）、§4.6 审批记录（timeline，
`nb_instance_timeline` append-only）、§4.7 完整三级结论回落（`DECLARED` / `LAST_DECISION` / `FALLBACK`）、
`GET /workflow/v1/instances` 实例列表（cursor 分页）；或 `netbpm:parameters` 覆盖扩展补 `writableBy` / `visibleTo`。
（§4.6 与 §4.7 已落地，见附录 M；实例列表已落地，见附录 N。）

---

## 附录 M：S3 第七段落点（审批记录时间线 + 结论三级回落）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs` | 增 `nb_instance_timeline`（append-only，主键 `(process_instance_id, seq)`） | 列名 `seq` / `comment_text` 避保留字；不带外键（引擎删实例不该连带删审计） |
| `src/NetBpm.Persistence/Platform/InstanceTimelineStore.cs` | `IApprovalTimelineStore` + Dapper / 内存两实现；`TimelineKindText` 词汇表 | 序号「读 max + 插入 + 唯一键冲突重试」（`MaxAppendAttempts=5`）；调用方传入的 `Sequence` 一律忽略 |
| `src/NetBpm.Rest/Platform/ApprovalTimeline.cs` | `IApprovalTimeline`：`AppendAsync`（写失败只告警）+ `GetHistoryAsync`（存在性双查 → 条目 → 老实例回填 → `nodeOutcomes` → `nextCursor`） | 出口收敛到单一服务；聚合口径保守（同环节多条 = `ALL_APPROVED`），BPMN 多实例解析后再改 |
| `src/NetBpm.Rest/Platform/ApprovalTimelineServiceExtensions.cs` | `AddNetBpmApprovalTimeline`（store 单例 + 服务 Scoped） | 无库宿主自动落内存实现 |
| `src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` | 增 `GET /workflow/v1/instances/{id}/history` | scope `workflow:history:read`；`?afterSequence=` + `?limit=`；未知实例 404 |
| `src/NetBpm.Rest/Platform/InstanceStartService.cs` | 启动成功后写 `INSTANCE_STARTED` | 纯服务令牌 `actorId=null`，`actorCallerId` 记调用系统 |
| `src/NetBpm.Rest/Platform/TaskSubmitService.cs` | 提交后写 `TASK_COMPLETED`（+ 实例结束时补终态条目），并回填 `timelineDelta` | 先完成引擎任务再写审计；`timelineDelta` 只带本次新增 |
| `src/NetBpm.Rest/Platform/InstanceCatalog.cs` | `BuildConclusion` 改三级回落 + `ResolveDeclaredSemantic` | 声明值先比语义名再走选项 id（`Enum.TryParse("1")` 会落成 `REJECT`，必须绕开） |
| `src/NetBpm.Platform.Contracts/Instances/InstanceContracts.cs` | 增 `PlatformVariables.Outcome`（`netbpm_outcome`） | 前缀防撞名：业务变量叫 `outcome` 很常见 |
| `src/NetBpm.Platform.Contracts/Approval/ApprovalVocabulary.cs` | `ApprovalSemantic` 追加 `Closed = 5` | 只由第三级产生；审计能区分「流程说通过了」与「平台只知道它结束了」 |
| `tests/NetBpm.IntegrationTests/DatabaseScriptTests.cs` | 平台表清单补 `nb_instance_timeline` | 新表同样受建表脚本守门 |

测试：`tests/NetBpm.IntegrationTests/ApprovalTimelineTests.cs`（6 项，内存宿主）、
`InstanceConclusionTests.cs` 增 2 项（MySQL + 历史宿主：`DECLARED` 覆盖 `result` / `FALLBACK` 兜底）。

下一步（S3 收尾候选）：
1. §4.5 表单快照（`nb_approval_snapshot`）——`/tasks/{id}/form` 与 `formSnapshotTaskId` 的落库端；
2. ~~`GET /workflow/v1/instances` 实例列表~~（v1.11 已落地，见附录 N）；
3. `netbpm:parameters` 覆盖扩展补 `writableBy` / `visibleTo` / 约束（`ParameterConstraints` 已支持长度/范围/正则）；
4. 时间线的**人员级可见性**（`workflow:history:read:all` 分级）——参与人模型 v1.12 已就位（附录 O），
   剩下的是把它接到 `IApprovalTimeline.GetHistoryAsync` 的可见性判定上。

---

## 附录 N：S3 第八段落点（实例列表）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Rest/Platform/InstanceListCatalog.cs` | `IInstanceListCatalog` + 实现：双源合并 → 状态精筛 → 游标定位 → 排序切片 → 补办理人与结论 | 运行态 + 历史**两侧都查**（历史关闭时只有运行态能回答、只用运行态会漏掉全部已办）；`status=COMPLETED/TERMINATED` 下推 `Finished=true`；升序明确 400（引擎固定倒序，无法安全下推） |
| `src/NetBpm.Rest/Platform/InstanceReadRules.cs` | `ResolveStatus` / `ResolveConclusion` / `ConclusionLabel` / `ParseKey` / `AsUtcOffset` | 从 `InstanceCatalog` 抽出，列表与详情共用一套判定——两份实现必然漂移，而「列表已通过、详情已办结」没人会先怀疑是两处不一致 |
| `src/NetBpm.Rest/Platform/InstanceListCatalogServiceExtensions.cs` | `AddNetBpmInstanceList` | 与单条视图的注册分开：两条独立读路径，改谁都不该逼另一个重新注册 |
| `src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` | 增 `GET /workflow/v1/instances` | scope `workflow:instance:read`；枚举参数按字符串收（契约取值是 `RUNNING` / `DESCENDING`，Minimal API 默认枚举绑定大小写敏感，`?status=RUNNING` 会直接绑定失败）；游标非法 400 |
| `src/NetBpm.Rest/GlobalExceptionHandler.cs` | `BadHttpRequestException` → 400 | 参数写错一个值不该得到「服务器内部错误」；这是全局修复，不止本端点受益 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 注册 `AddNetBpmInstanceList` | — |

测试：`tests/NetBpm.IntegrationTests/InstanceListTests.cs`（10 项，内存宿主）+
`InstanceListCompletedTests.cs`（1 项，MySQL + 历史宿主）。

**已知上限**（写给下一块，不是「以后再说」）：租户与流程白名单过滤在内存做——引擎的实例查询不支持
「多个定义 id + 空租户视为全局共享」这个组合，所以无法下推。`TaskCatalog.ListAsync` 也是同一口径
（引擎全量 → 内存过滤 → 内存分页）。实例量上万时要靠引擎侧索引（或平台自己的实例索引表）解决，
届时列表的读取路径要整体换掉，而不是在这里加 `Take()`。

---

## 附录 O：S3 第九段落点（参与者维度）

| 文件 | 内容 | 关键决定 |
|---|---|---|
| `src/NetBpm.Engine/History/HistoricIdentityLink.cs`（新） | `HistoricIdentityLink` + `HistoricIdentityLinkFilter` | `ProcessInstanceId` 是**已按 `TASK_ID_` 解析后**的实例 id（任务级候选行本来就没有实例 id） |
| `src/NetBpm.Engine/History/IHistoricQueryManager.cs` | 增 `QueryIdentityLinksAsync` | 读口跟着既有下推端口走（`IHistoryManager` 是写端口），用户与组之间 OR、其余 AND |
| `src/NetBpm.Persistence/History/DapperHistoryManager.cs` | 实现按人下推（双方言） | 必须 `left join ACT_HI_TASKINST` + `coalesce`：候选组**只有任务级行**，不做这层解析会整类漏掉候选组命中的单据 |
| `src/NetBpm.Runtime/HistoricIdentityLinkQuery.cs`（新）+ `IProcessEngine` / `ProcessEngine` | `FindHistoricIdentityLinksAsync` | 沿用 `HistoricTaskQuery` 的 `HasCriteria` 防呆；无下推能力（历史关闭）如实返回空，不用运行态冒充已办 |
| `src/NetBpm.Rest/Platform/InstanceParticipation.cs`（新） | `IInstanceParticipation` + `InstanceInvolvement` | 六个来源（历史 / 运行态 × 发起人 / 审批人 / 候选）合成 `instanceId → ParticipationRole[]`；任一路失败只告警；候选命中吃掉落 `PARTICIPANT` 的重复行 |
| `src/NetBpm.Rest/Platform/InstanceParticipationServiceExtensions.cs`（新） | `AddNetBpmInstanceParticipation` | 跨列表共享的「人 → 单据」入口，独立注册 |
| `src/NetBpm.Rest/Platform/InstanceListCatalog.cs` | 增 `?involvedUser` 的支持 + 回填 `myRoles` | `null` = 没有这个过滤条件，空映射 = 「真的无关」——不区分二者会让「我的单据」在索引查不到时**静默整体消失** |
| `src/NetBpm.Rest/Endpoints/InstanceEndpoints.cs` | 增 `involvedUser` 查询参数 | 与 `startedBy` 同的人员范围规则，越权 403 |
| `src/NetBpm.Rest/Platform/TaskCatalog.cs` | `ListPendingAsync` / `ListDoneAsync` 分岔 + `TryResolveBusinessKeyAsync` | 一个方法只认一条数据路径；已办按**办结时间倒序**（最近办的在前） |
| `src/NetBpm.Rest/Endpoints/TaskEndpoints.cs` | `scope=DONE` 额外要求 `workflow:history:read` | 读的是审批记录不是待办箱；缺 scope 显式 403，不静默降级成「返回自己的待办」 |
| `src/NetBpm.Rest/Hosting/NetBpmHost.cs` | 注册 `AddNetBpmInstanceParticipation` | — |
| `src/NetBpm.Platform.Contracts/Instances/InstanceContracts.cs` | `ParticipationRole` / `InstanceQuery.InvolvedUser` / `InstanceSummary.MyRoles` | 三者均为追加（可选），不改动既有字段顺序 |
| `src/NetBpm.Platform.Contracts/Tasks/TaskContracts.cs` | `TaskScope.Done` / `TaskSummary.CompletedAt` | 已办与待办共用同一个 `TaskSummary`，靠 `completedAt` 区分——两个列表形状不一致迟早分叉 |

测试：`tests/NetBpm.IntegrationTests/ParticipantIndexTests.cs`（3 项，**MySQL + 历史宿主**——
内存宿主没有 `ACT_HI_*`，参与关系物理上不存在）。

**已知上限**：
1. **历史关闭的宿主回答不了「已办」**：只能靠运行态兜底「还在跑」的部分，列表会偏少（与 v1.11 已知上限同口径）。
2. ~~已办列表不带结论~~（v1.13 已补上 `TaskSummary.Decision`）。
   剩下的一句是：**绕过平台提交端点办结的任务永远没有 decision**——
   引擎不会知道「批了什么」，这时老实返回「没有」，不倒推。
3. 参与者索引每次请求查六个来源（历史三 + 运行态三）：都是按 `USER_ID_` / `ASSIGNEE_` 等值命中的查询，
   但六次往返确实是成本。
   若「我参与过的单据」成为高频路径，下一步是给 `ACT_HI_IDENTITYLINK` 补一个
   `(USER_ID_, PROC_INST_ID_)` 索引，并把六个来源收敛成一到两条联合查询。

---

## 附录 P：S3 收尾段（v1.13）落点

| 块 | 代码落点 | 数据落点 |
|---|---|---|
| 审批记录的人员级可见性 | `ApprovalTimeline.ResolveVisibilityAsync` + `HistoryVisibilityLevel`；`IInstanceParticipation.DescribeAsync`；`InstanceEndpoints.HistoryAsync` 注释同步 | —（复用参与者索引） |
| 已办带「我当时的结论」 | `IApprovalTimelineStore.ListDecisionsByTaskIdsAsync`（Dapper + 内存）；`TaskCatalog.AttachDecisionsAsync`；契约 `Decision` / `TaskSummary.Decision` | `nb_instance_timeline`（既有） |
| §4.5 表单快照 | `IApprovalSnapshot`（`CaptureAsync` / `FindAsync` / `ListByInstanceAsync`）；`TaskSubmitService` 提交时先落快照；`GET /tasks/{id}/form`；契约 `FormSnapshot` 扩 `instanceId`/`activityName`/`actorId`/`optionId`/`attachments` | **新表 `nb_approval_snapshot`**（键 `task_id`，附 `definition_version`） |
| `netbpm:parameters` 覆盖 | `BaseBpmnXmlConverter.ReadExtensionElement`（未知扩展元素原样捕获）；`ProcessCatalog.ReadParameterOverrides` / `Merge` / `BuildDeclaredParameter`；`InstanceCatalog.LoadHiddenParametersAsync` | —（BPMN 内的声明） |
| 端点 | `GET /tasks/{id}/form`（`workflow:task:read`） | — |

**本阶段明确没做**：
1. ❌ 实例列表升序（引擎侧还没有「按方向排序」的查询能力，见 §7.6.11 的下一步）；
2. ❌ 快照的**清理 / 归档策略**（`nb_approval_snapshot` 会随审批量线性增长，归档规则与
   `ACT_HI_*` 的保留策略要一起定，单独立一条会两边打架）；
3. ❌ `writableBy` 的写侧强制（schema 已经能表达它，但发起 / 提交的写校验还没按它拒绝——
   现在只是「声明出来了」，执行留给下一块，避免一次改两个信号）。

**v1.13 已把 1~4 全部落地**（见附录 P）：人员级可见性、已办带结论、表单快照、参数覆盖。
（第 2 条的落法与当初的设想不同：没有做「按 actor 跨实例查」，
而是按已知 taskId 精确回捞——后者不用为一致性另建索引，见 §7.6.11。）

下一步（S3 之后）：
1. **实例列表的升序支持**——唯一还挂着的一条，且**依赖引擎侧先给「按方向排序」的查询能力**：
   现在 `ProcessInstanceQuery` / `HistoricProcessInstanceQuery` 固定按开始时间倒序，
   在双源合并 + 游标分页的结构下，升序无法安全下推（翻页会漏），所以目前直接 400。
   做法只有两条：引擎侧加 `SortDirection`，或平台自建实例索引表（同时解决上万量级的内存过滤问题）。
2. `/instances/{id}/terminate`、认领 / 取消认领 / 转办、附件引用：属于**写入型动作**（S4 面），
   且转办 / 终止要先把「跳转 / 终止也写时间线条目」这条规矩立起来，否则审计会出现断档。
