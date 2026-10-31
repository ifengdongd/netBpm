# NetBpm 平台化改造 —— JIRA 任务与工作日志（可直接复制）

> 来源：`docs/platform-api-refactoring-checklist.md`（勾选版清单）｜ 生成日期：2026-10-31
> 代码基线：`9cfe44f` / `db56e5b` / `fdb0677`（已推送 `origin/main`）
>
> **KEY 说明**：本文用 `NETBPM-1xx` 作为占位 KEY，建单时按贵项目实际 JIRA 前缀替换（如 `CCES-xxxxx`）；
> 史诗（Epic）与任务的父子关系通过 `Epic Link` 建立。
> **估算口径**：`人日` 是纯开发参考值（沿用方案 §6），`故事点` 是含测试与联调的交付估算（斐波那契）。
> 两者口径不同，**不要相加比较**。

---

# 第一部分：JIRA 任务清单

## 0. 总览

| Epic | 主题 | Issue 数 | 故事点合计 | 优先级 | 前置 |
|---|---|---|---|---|---|
| NETBPM-100 | 【S2 收口】身份与授权补齐 | 3 | 14 | P1 | — |
| NETBPM-110 | 【S3 尾巴】业务标准收尾 | 3 | 6 | P2 | — |
| NETBPM-120 | 【S4】独立部署能力 | 6 | 47 | P0/P1 | 部分依赖引擎侧 |
| NETBPM-130 | 【S5】治理与 SDK | 5 | 24 | P1/P2 | 依赖 131 |
| NETBPM-140 | Spike：实例索引方案选型 | 1 | 2 | P2 | 阻塞 111 |

**两个真正的阻塞点**：
- **[!] NETBPM-121 外部任务** —— 不做它，含 `serviceTask` 的 BPMN 在独立部署下起不来，S4 整体无法验收。
- **[!] NETBPM-131 契约治理** —— 不做它（OpenAPI 快照 + CI diff），官方 SDK 无法守兼容性，S5 无从验收。

---

## Epic 1：NETBPM-100 【S2 收口】身份与授权补齐

> **Epic 描述**：S2 的主体（三元组 / 客户端注册 / 轮换 / 按客户端限流）已落地，但出口条件里的 6 项集成用例尚未全部覆盖，
> JWT 仍是 HS256 自签，引擎面端点只做租户隔离、未按 `workflow:*` scope 判定。不补齐这些，"对外提供服务"就不成立。

### NETBPM-101 · Task · JWT 升级 RS256 + JWKS + 可选外部 IdP（P1，8 人日，8 点）

**描述**
现状 `AuthTokenIssuer` 仍是 HS256 自签，密钥即验签密钥，无法对接外部 IdP。落点：`Security/AuthenticationMiddleware.cs`、`AuthTokenIssuer.cs`。

**验收标准**
- [ ] 支持 RS256 签发与验签，公钥通过 JWKS 端点暴露
- [ ] HS256 保留为内网模式（配置切换，不是删掉）
- [ ] 可选接入外部 IdP（OIDC 校验 issuer / audience / kid）
- [ ] 密钥切换窗口内**新旧令牌均可验签**（不能一刀切导致全量掉线）
- [ ] 补 `IdentityTests`：无凭据 401、`client_credentials` submit → 403

### NETBPM-102 · Task · 引擎面端点按 `workflow:*` scope 逐条收紧（P1，3 人日，3 点）

**描述**
`/api/runtime`、`/api/identity`、`/api/designer` 目前只做租户隔离，未按平台 scope 判定，客户端能读到比所需更多的引擎数据。

**验收标准**
- [ ] 三条引擎面路径逐个挂 `workflow:*` / `engine:*` scope 判定
- [ ] 平台面（`workflow/v1`）不经过这些端点，行为零变化
- [ ] 缺 scope 返回 403，错误码与 `Platform.Contracts/Common/ErrorCodes.cs` 对齐

### NETBPM-103 · Task · 补 S2 出口守门用例（P1，3 人日，3 点）

**描述**
S2 的出口条件有 6 项集成用例，目前只覆盖了一部分，严格说 S2 未真正出关。

**验收标准**
- [ ] `TenantIsolationTests`：A 租户查 B 租户实例 → **404**（不是 403，不泄露存在性）
- [ ] `ClientRegistryTests`：未注册 401；`Suspended`/`Revoked` 401；轮换过渡期新旧密钥**同时可用**，过期后旧的立即 401
- [ ] `IdempotencyTests`：同键同体 → 200 + `Idempotent-Replay: true` 且**只产生一个实例**；同键异体 → 409
- [ ] 纯服务令牌 submit → 403（`auth.subject_required`）
- [ ] `DecisionVocabularyTests`：未知 `optionId` → 422；`semantic` 与 `optionId` 冲突 → 422

---

## Epic 2：NETBPM-110 【S3 尾巴】业务标准收尾

> **Epic 描述**：S3 已全部落地（v1.13），只剩三条刻意推迟的尾巴。它们彼此无依赖，可并行，性价比最高。

### NETBPM-111 · Task · 实例列表升序支持（P2，2 人日，2 点）

**描述**
`ProcessInstanceQuery` / `HistoricProcessInstanceQuery` 固定按开始时间倒序，在双源合并 + 游标分页的结构下升序无法安全下推（翻页会漏），目前直接返回 400。

**验收标准**
- [ ] 方案二选一并写明取舍：① 引擎侧加 `SortDirection`；② 平台自建实例索引表（**同时**解决上万量级的内存过滤问题）
- [ ] 升序翻页不重不漏（与倒序同口径的测试）
- [ ] 若选 ②，需给出索引表的写入时机与回填方案
- **依赖**：先做 NETBPM-140 的选型结论

### NETBPM-112 · Task · `nb_approval_snapshot` 归档与清理策略（P2，2 人日，2 点）

**描述**
快照随审批量线性增长，但归档规则必须与 `ACT_HI_*` 的保留策略一起定——单独立一条会两边打架。

**验收标准**
- [ ] 与 `ACT_HI_*` 保留策略同一份配置/同一套口径
- [ ] 有可配置的保留窗口与清理任务（默认关闭，不误删在用快照）
- [ ] 清理不影响 `formSnapshotTaskId` 指向的引用完整性（或明确"清理后 404"是预期行为）

### NETBPM-113 · Task · `writableBy` 写侧强制（P2，2 人日，2 点）

**描述**
schema 已经能表达 `writableBy`（v1.13），但发起 / 提交的写校验还没按它拒绝——现在只是"声明出来了"。推迟的原因是避免一次改两个信号。

**验收标准**
- [ ] 发起、提交时对 `writableBy` 不满足的参数拒绝（422 + 明确错误码）
- [ ] 未声明 `writableBy` 的老流程完全不受影响（不为新能力付兼容税）
- [ ] 管理视角 / 纯服务令牌不受限（与 `visibleTo` 同一口径）

---

## Epic 3：NETBPM-120 【S4】独立部署能力

> **Epic 描述**：目标是"引擎独立部署给别人用"。若近期目标只是"内部系统统一走一套干净 API"，
> 这个 Epic 可以整体后移——但**外部任务是唯一无法绕开的硬前提**。

### NETBPM-121 · Story · 外部任务 External Task（P0，15 人日，13 点）**[!] 阻塞项**

**描述**
`activiti:class` / `delegateExpression` 在宿主进程内解析（`ServiceTaskDelegateGuard` 部署期预解析，缺一个宿主就起不来）。
必须提供拉模式（External Task）替代进程内委托，否则"独立引擎"只能跑纯人工审批。

**子任务**
- [ ] `nb_external_task` 表（含 `topic` / `worker_id` / `lock_expires_at` / `retries`）
- [ ] `IExternalTaskBroker` + fetch / complete / failure 端点
- [ ] 锁与续租、重试次数递减、`retries=0` 进死信
- [ ] 部署期守卫：无 `topic` 且无 `class` 的 serviceTask 直接拒部署

**验收标准**
- [ ] 一条含 `serviceTask` 的流程在独立部署下**端到端跑通**
- [ ] `ExternalTaskTests`：锁过期后任务可被另一 worker 取走；`retries=0` 进死信
- [ ] 旧 BPMN（进程内委托）不改一字仍可跑（回归）

### NETBPM-122 · Story · 事件对外投递（P1，10 人日，8 点）

**描述**
outbox 目前只写不发。需要 CloudEvents 格式 + 签名 + 重试 + 订阅管理，订阅按 client 记账（避免一个系统退订影响别家）。

**验收标准**
- [ ] `WebhookTests`：签名可验证；重复投递按 `eventId` 去重；死信可重放
- [ ] `nb_event_subscription` 表，订阅按 `client_id` 隔离
- [ ] 投递失败有退避重试与死信队列

### NETBPM-123 · Task · 平台存储收尾（P1，5 人日，5 点）

**验收标准**
- [ ] 六张表进 `SchemaCatalog` 并生成双方言脚本：`nb_process_parameter`、`nb_parameter_snapshot`、`nb_idempotency`、`nb_external_task`、`nb_event_subscription`、`nb_audit_log`
- [ ] 幂等键从"全局"改为 `(client_id, idem_key)`
- [ ] `DatabaseScriptTests` 守门通过（13 张 `ACT_*` 红线不动）
- [ ] 老库重跑 `DbMigrate migrate` 验证补表（无迁移记录表，缺表只降级不报错）

### NETBPM-124 · Task · 全局配额改 Redis 版（P2，2 人日，2 点）

**描述**
跨实例配额现为进程内计数，多实例部署会失效。

**验收标准**
- [ ] 配额计数走 Redis（与作业队列同一 `KeyPrefix` 约定）
- [ ] 单实例降级路径明确（Redis 不可用时按本地计数还是放行，要写清楚）

### NETBPM-125 · Task · 写入型端点族（P1，8 人日，8 点）

**描述**
`POST /instances/{id}/terminate`、`POST /tasks/{id}/claim|unclaim|transfer`、`GET /tasks/{id}/attachments`、
`GET /instances/{id}/diagram.svg`、`GET /instances/{id}/parameters`、`GET /processes/{key}/form/schema`、
`/admin/v1/processes` 写路径。

**验收标准**
- [ ] 上述端点全部落地并挂对应 scope
- [ ] **前置规矩**：跳转 / 终止也写时间线条目（`INSTANCE_TERMINATED` / 跳转条目），否则审计出现断档
- [ ] 转办后待办归属正确（`scope=CLAIMED` 语义不变）
- [ ] 附件只存引用，不做存储

### NETBPM-126 · Task · 容器化 + S4 出口验证（P1，3 人日，3 点）

**验收标准**
- [ ] `Platform.Service` 容器可独立启动
- [ ] S4 出口条件达成：含 `serviceTask` 的流程端到端跑通 + worker 宕机后任务被重投
- [ ] `DualModeTests`：同一 BPMN 在嵌入式与独立服务下 `submit → history` 的 timeline 语义等价

---

## Epic 4：NETBPM-130 【S5】治理与 SDK

### NETBPM-131 · Task · 契约治理：OpenAPI 快照 + CI diff（P1，6 人日，5 点）**[!] SDK 前置**

**验收标准**
- [ ] `contracts/openapi.v1.json` 快照入库，与代码生成结果**字节一致**
- [ ] CI 里跑 `openapi-diff`，breaking 变更直接失败（拒绝"顺手改"）
- [ ] `ContractSnapshotTests` / `CompatibilityTests` 全绿

### NETBPM-132 · Story · 官方 SDK（P2，10 人日，8 点）

**依赖**：NETBPM-131
**验收标准**
- [ ] `NetBpm.Platform.Client`（.NET）发布，覆盖 `/workflow/v1` 全族
- [ ] 由 OpenAPI 生成 TS / Java 版本，各语言客户端生成器均能编译
- [ ] `CompatibilityTests`：上一版 SDK 打新服务全部端点通过

### NETBPM-133 · Task · M3 收尾：响应强类型（P2，3 人日，3 点）

**验收标准**
- [ ] `Decorate*` 迁到 `admin/v1` 或删除
- [ ] `WorkflowEndpointOptions` 标记 `[Obsolete]`，新 `PlatformApiOptions`
- [ ] `WorkflowEndpoints.cs` 收口，平台面端点一律返回 `Platform.Contracts` 类型

### NETBPM-134 · Task · `nodeOutcomes` 会签 / 或签聚合（P2，3 人日，3 点）

**描述**
当前是临时实现。聚合口径来自 BPMN 多实例配置。

**验收标准**
- [ ] `nodeOutcomes[]` 正确表达会签 / 或签聚合（如"3 人 2 通过 1 拒绝"）
- [ ] `DecisionVocabularyTests` 覆盖 `ALL_APPROVED`

### NETBPM-135 · Task · 治理配套：配额看板 / 审计 / SLO / 接入 Runbook（P3，5 人日，5 点）

**验收标准**
- [ ] 配额与限流看板、管理动作审计（`nb_audit_log`）、SLO 指标
- [ ] 两家业务系统按 Runbook 自助接入成功
- [ ] `LoadTests`：读 p95 < 80ms、写 p95 < 200ms（100 并发，MySQL 5.7）

### NETBPM-136 · Task · 补齐一致性 / 性能守门（P2，4 人日，3 点）

**验收标准**
- [ ] `IdempotencyTests` / `DualModeTests` / `LoadTests` / `ContractSnapshotTests` / `CompatibilityTests` 全部落地
- [ ] 守门清单 25 条中未绿的 11 条全部转绿

---

## Spike：NETBPM-140 实例索引方案选型（P2，2 人日，2 点）

**描述**
实例列表的租户 + 流程白名单过滤目前只能在内存做（引擎不支持"多定义 id + 空租户共享"的组合下推），
实例量上万时不可接受；升序支持也卡在同一个结构问题上。

**产出**
- [ ] 结论文档：引擎侧索引 vs 平台自建实例索引表（成本、一致性、回填方案）
- [ ] 明确"升序"走哪条路
**阻塞**：NETBPM-111

---

## 1.1 依赖关系

```
NETBPM-140 选型 ──► NETBPM-111 升序
                                    ┌──► NETBPM-122 事件对外 ──┐
NETBPM-121 外部任务 [!] ────────────┤                          ├──► NETBPM-126 出口验证
                                    └──► NETBPM-123 存储收尾 ──┘
NETBPM-131 契约治理 [!] ──► NETBPM-132 SDK
NETBPM-101/102/103（S2 收口）与 NETBPM-111/112/113（S3 尾巴）互不依赖，可与上面并行
```

## 1.2 建议的迭代划分

| 迭代 | 内容 | 出口 |
|---|---|---|
| 迭代 1 | NETBPM-101/102/103 + 111/112/113 + 140 | S2 真正出关、S3 无尾巴、技术选型有结论 |
| 迭代 2 | NETBPM-121 + 122 + 123 + 124 | S4 独立部署可验收 |
| 迭代 3 | NETBPM-125 + 126 + 131 + 132 + 133 + 134 + 135 + 136 | S5 治理与 SDK 交付 |

> 若只需「内部统一接入」，迭代 1 做完即可收口，迭代 2/3 整体后移。

---

## 1.3 JIRA CSV 导入（复制保存为 `.csv`，导入时字段映射）

```csv
Issue Type,Summary,Priority,Labels,Story Points,Epic Link,Description
Epic,【S2 收口】身份与授权补齐,P1,platform;security,14,,补齐 JWT/引擎面 scope/出口用例，让对外提供服务成立
Task,JWT 升级 RS256 + JWKS + 可选外部 IdP,P1,security;jwt,8,NETBPM-100,现状 HS256 自签｜支持 RS256+JWKS｜HS256 保留为内网模式｜切换窗口内新旧令牌均可验签｜补 IdentityTests
Task,引擎面端点按 workflow:* scope 逐条收紧,P1,security;scope,3,NETBPM-100,/api/runtime、/api/identity、/api/designer 挂平台 scope｜平台面行为零变化｜缺 scope 403
Task,补 S2 出口守门用例,P1,test;security,3,NETBPM-100,跨租户 404｜轮换期双密钥可用｜幂等重放只产生一个实例｜纯服务 submit 403｜未知 optionId 422
Epic,【S3 尾巴】业务标准收尾,P2,platform,6,,三条刻意推迟的尾巴，彼此无依赖可并行
Task,实例列表升序支持,P2,api;paging,2,NETBPM-110,引擎侧加 SortDirection 或平台自建索引表｜升序翻页不重不漏｜依赖 NETBPM-140 选型
Task,nb_approval_snapshot 归档与清理策略,P2,db;retention,2,NETBPM-110,与 ACT_HI_* 保留策略同口径｜可配置保留窗口｜不误删在用快照
Task,writableBy 写侧强制,P2,api;authz,2,NETBPM-110,发起/提交按 writableBy 拒绝｜未声明的老流程不受影响｜管理视角不受限
Epic,【S4】独立部署能力,P0,platform;deploy,47,,外部任务是硬前提；若只做内部统一接入可整体后移
Story,外部任务 External Task,P0,engine;external-task,13,NETBPM-120,nb_external_task 表｜fetch/complete/failure 端点｜锁与续租重试死信｜部署期守卫拒无 topic 的 serviceTask｜含 serviceTask 流程端到端跑通
Story,事件对外投递,P1,events;webhook,8,NETBPM-120,CloudEvents+签名｜按 eventId 去重｜死信可重放｜订阅按 client 隔离
Task,平台存储收尾,P1,db;schema,5,NETBPM-120,六张 nb_* 表进 SchemaCatalog｜幂等键改为按 client｜老库 migrate 验证补表
Task,全局配额改 Redis 版,P2,quota;redis,2,NETBPM-120,跨实例配额计数走 Redis｜Redis 不可用的降级路径写清楚
Task,写入型端点族,P1,api;write,8,NETBPM-120,terminate/claim/unclaim/transfer/attachments/diagram.svg/parameters/form.schema｜跳转与终止也要写时间线条目
Task,容器化 + S4 出口验证,P1,deploy,3,NETBPM-120,Platform.Service 容器可独立启动｜含 serviceTask 流程端到端跑通｜DualModeTests 语义等价
Epic,【S5】治理与 SDK,P1,governance;sdk,24,,契约治理是 SDK 的前置
Task,契约治理：OpenAPI 快照 + CI diff,P1,contract;ci,5,NETBPM-130,openapi.v1.json 字节一致｜CI 跑 openapi-diff，breaking 直接失败
Story,官方 SDK,P2,sdk,8,NETBPM-130,NetBpm.Platform.Client(.NET)｜生成 TS/Java｜CompatibilityTests 通过｜依赖 NETBPM-131
Task,M3 收尾：响应强类型,P2,contract,3,NETBPM-130,Decorate* 迁移或删除｜WorkflowEndpointOptions 标 [Obsolete]｜PlatformApiOptions
Task,nodeOutcomes 会签/或签聚合,P2,engine;semantics,3,NETBPM-130,nodeOutcomes[] 正确表达聚合｜覆盖 ALL_APPROVED
Task,治理配套：看板/审计/SLO/Runbook,P3,governance,5,NETBPM-130,配额看板｜管理动作审计｜SLO｜两家业务系统按 Runbook 自助接入成功｜LoadTests p95 达标
Task,补齐一致性与性能守门,P2,test,3,NETBPM-130,守门清单 25 条中未绿的 11 条全部转绿
Spike,实例索引方案选型,P2,spike;index,2,,引擎侧索引 vs 平台实例索引表｜成本与一致性对比｜明确升序走哪条路｜阻塞 NETBPM-111
```

---

# 第二部分：工作日志

> ⚠️ **2026-10-31 是周六**（工作日缓存未收录，技能规则默认跳过）。今天确有 3 笔提交推送，
> 故按"节假日有提交"的例外处理生成。如用于补登或改填工作日，把 `date` 改成目标日期即可（建议 `2026-11-02`）。

## 2.1 今日日志（2026-10-31，合计 7.5h）

```
1. 推送前的最后一道检查：全量跑了一遍 dotnet build NetBpm.slnx 确认 0 错，又逐个核了 103 个待纳入文件，确认没有 bin/obj/artifacts 混进暂存区(0.75h)
2. 攒了一段时间的平台化改造拆成三笔提交推到 origin/main——feat(platform) 78 个文件（契约工程 + /workflow/v1 全端点族）、feat(db) 5 个（nb_* 平台表进生成器）、test(platform) 20 个（16 个集成测试类 + 方案文档 v1.13）(1.25h)
3. 项目记忆超限，归并了一轮：MEMORY.md 从 16.4KB 压到 13.8KB，合并同类项只留硬知识，补写今日日志(1.5h)
4. 通读 2102 行的改造方案，把五个阶段、16 个改造项、25 条守门测试、22 个端点的落地状态逐条跟代码对了一遍，部分落地的拆成子项，标出 M12 外部任务与 M14 契约治理两个阻塞点(3.0h)
5. 导出带勾选状态的 checklist 并核准计数，改了两处错（端点未做数是 8 不是 7、M 表严格完成数是 6），再把剩余任务拆成可建的 JIRA issue 与填报内容(1.0h)
合计: 7.5h
```

## 2.2 后续推进时的填报示例（有 JIRA 号时以 `KEY：` 开头）

```
1. NETBPM-121：外部任务开了个头，nb_external_task 表结构定稿并进 SchemaCatalog，双方言脚本重新生成(2.0h)
2. NETBPM-103：补跨租户隔离用例，A 租户查 B 租户实例确认返回 404 而不是 403，顺带把缺 scope 的错误码对齐了一遍(0.75h)
3. NETBPM-113 的写侧强制：发起与提交按 writableBy 拒绝未授权参数，老流程没声明的不受影响，回归跑完没炸(4.0h)
合计: 6.75h
```

> 上面是**示例**（时长未凑满 7h），实际填报时按当天真实分布补条目即可；
> 规则：3–5 条、每条 15 分钟为单位、总计 7–8h 且不等于 8h、时长跨度拉开（混入 0.75h 与 3–4h）。

## 2.3 WTS 推送用 JSON

数据已生成到 `C:\Users\fengdong\.workbuddy\skills\auto-worklog\worklog_2026-10-31.json`。
**本次只生成数据，未推送**（WTS 写入需你确认后再执行）。需要推送时自行运行：

```bash
cd C:/Users/fengdong/.workbuddy/skills/auto-worklog
python scripts/push_worklog.py --entries worklog_2026-10-31.json --dry-run   # 先预览
python scripts/push_worklog.py --entries worklog_2026-10-31.json             # 确认后推送
```

内容：

```json
{
  "date": "2026-10-31",
  "entries": [
    {
      "task_title": "推送前的最后一道检查：全量跑了一遍 dotnet build NetBpm.slnx 确认 0 错，又逐个核了 103 个待纳入文件，确认没有 bin/obj/artifacts 混进暂存区",
      "work_time_long": 45,
      "work_start_at": "09:00",
      "work_end_at": "09:45",
      "jira_id": []
    },
    {
      "task_title": "攒了一段时间的平台化改造拆成三笔提交推到 origin/main——feat(platform) 78 个文件（契约工程 + /workflow/v1 全端点族）、feat(db) 5 个（nb_* 平台表进生成器）、test(platform) 20 个（16 个集成测试类 + 方案文档 v1.13）",
      "work_time_long": 75,
      "work_start_at": "09:45",
      "work_end_at": "11:00",
      "jira_id": []
    },
    {
      "task_title": "项目记忆超限，归并了一轮：MEMORY.md 从 16.4KB 压到 13.8KB，合并同类项只留硬知识，补写今日日志",
      "work_time_long": 90,
      "work_start_at": "11:00",
      "work_end_at": "12:30",
      "jira_id": []
    },
    {
      "task_title": "通读 2102 行的改造方案，把五个阶段、16 个改造项、25 条守门测试、22 个端点的落地状态逐条跟代码对了一遍，部分落地的拆成子项，标出 M12 外部任务与 M14 契约治理两个阻塞点",
      "work_time_long": 180,
      "work_start_at": "13:30",
      "work_end_at": "16:30",
      "jira_id": []
    },
    {
      "task_title": "导出带勾选状态的 checklist 并核准计数，改了两处错（端点未做数是 8 不是 7、M 表严格完成数是 6），再把剩余任务拆成可建的 JIRA issue 与填报内容",
      "work_time_long": 60,
      "work_start_at": "16:45",
      "work_end_at": "17:45",
      "jira_id": []
    }
  ]
}
```

> `jira_id` 留空是因为本项目改造目前没有关联 JIRA 单。等 NETBPM-1xx 建好后，
> 按 `"jira_id": [{"jira_key":"NETBPM-121","issue_subject":"外部任务 External Task"}]` 的格式填，
> 描述则以 `NETBPM-121：` 开头。
