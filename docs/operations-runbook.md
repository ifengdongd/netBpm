# NetBpm 运维手册（Runbook）

> 面向**值班与发布**：出问题怎么判断、按什么顺序动手、动手前要备份什么。
> 面向「把 NetBpm 接进自己系统」的开发者请看 `docs/integration-checklist.md`（组合根顺序、事务边界、委托对应关系）。
> 版本基线：v1.27（`110e0b2` 之后）。配置项与端点均以当前代码为准。

---

## 0. 一分钟速查

| 症状 | 先看什么 | 大概率是什么 | 动作 |
|---|---|---|---|
| 所有业务接口 401，但 `/health` 是绿的 | `/api/admin/info` 的 `database` / `historyEnabled` | `RequireCredential=true` 但没有任何校验材料（密钥 / JWT / 客户端） | 补凭据材料；`PlatformServiceGuards` 本应在启动时拦住这种配置，若已被放过说明用的是演示宿主 |
| 重启后单据全没了 | `/api/admin/info` 的 `database` 是不是 `in-memory` | 数据库模式没配连接串，静默退化内存引擎 | 配 `NetBpm__ConnectionString`，重启；数据不可恢复 |
| 流程走到某个节点卡住、没有人能办 | `GET /api/runtime/external-tasks?topic=...` | 外部任务没有 worker 在拉（`external_task.no_worker` 告警） | 起 worker；或确认 topic 拼写与 worker 订阅一致 |
| 流程卡住但待办列表是空的 | `GET /workflow/v1/instances/{id}` 的 `currentActivityIds` | 同上（`serviceTask` 在等外部完成） | 同上；**不要**手工改状态，先看 `nb_external_task` 的 `status` |
| 订阅方说「没收到通知」 | `GET /admin/v1/events/stats` | outbox 积压 / 投递死信 / 被单订阅限速推迟 | 见 §7 |
| 大量 429 | `/admin/v1/quotas`、`NetBpm:RateLimit:Store` | 配额打满，或多副本下 Store=Memory 实际配额被放大后又被打满 | 见 §9 |
| 一张单的审批记录人数对不上 | `GET /workflow/v1/instances/{id}` 的 `nodeOutcomes` | 节点被重办（退回 / 跳转），轮次分组 | 见 §11「轮次」 |
| 升级后一句 `select` 就 500 | 表结构是否缺列 | 新列没被补上（`create table if not exists` 不给已有表补列） | 见 §4 |

---

## 1. 部署形态与最小配置

两种宿主共用同一组合根（`AddNetBpmHost` + `MapNetBpmHostEndpoints`），差别只在**默认值**：

| 宿主 | 用途 | 默认 |
|---|---|---|
| `NetBpm.Rest` | 演示 / 嵌入式 | 开放访问、可内存 |
| `NetBpm.Platform.Service` | 独立部署 | 要求凭据、强制数据库，配置缺失**直接退出** |

生产用 `NetBpm.Platform.Service`，最小可用配置（环境变量写法，容器内冒号不便，用双下划线）：

```bash
NetBpm__ConnectionString="Server=mysql;Port=3306;Database=netbpm;User ID=netbpm;Password=***;Allow User Variables=true;"
NetBpm__DatabaseType=MySql
# 多副本必须换成 Redis，否则同一作业会被多个副本各执行一次
NetBpm__Jobs__Queue__Provider=Redis
NetBpm__Jobs__Queue__Redis__ConnectionString="redis:6379,password=***"
# 多副本必须换成 Redis，否则实际配额 = 副本数 × 配置值
NetBpm__RateLimit__Store=Redis
NetBpm__RateLimit__Redis__ConnectionString="redis:6379,password=***"
# 仅当「异源前端直连 /workflow/v1」才需要；管理端与设计器由 wwwroot 同源托管，不用配
NetBpm__Cors__AllowedOrigins__0=https://approve.example.com
```

启动自检（`PlatformServiceGuards`，**早于**引擎注册）会拒绝这四种配置：

1. 数据库模式但没有连接串 → 抛（否则静默退化成内存引擎，数据随容器消失）；
2. 队列配了 Redis 却没有地址 → 抛（否则退化内存队列，多副本下同一作业执行多次）；
3. 限流 `Store=Redis` 却没有地址 → 抛；
4. 要求凭据但没有任何校验材料 → 抛（否则所有 `/api/**` 401 而健康检查仍然绿）。

> 第 3、4 条的存在理由：这两种错误**不会**让服务挂掉，只会让它「看起来在跑」。

---

## 2. 探针语义

| 端点 | 回答什么 | 建议用途 |
|---|---|---|
| `GET /health/live` | 进程还活着 | 编排的 liveness（失败 → 重启） |
| `GET /health/ready` | 能不能接流量（会探数据库） | 编排的 readiness（失败 → 摘流量，**不重启**） |
| `GET /health` | 版本信息 | 人工核对版本 |
| `GET /api/admin/info` | 方言 / 是否开历史 / 期望结构版本 / 运行时长 | 排障第一站 |

`/health/ready` 在内存模式下恒为 `ready`（没有依赖可探）。**不要把它当 liveness 用**：数据库抖动时它会失败，而重启解决不了数据库。

---

## 3. 死信处置

### 3.1 外部任务死信

```bash
# 1) 定位：看死信任务（含 topic、重试次数、最后错误）
curl -s "$BASE/api/runtime/external-tasks?status=DEAD_LETTER" -H "X-Api-Key: $ADMIN_KEY"

# 2) 先修根因：worker 起没起来？topic 对不对？payload 是否 worker 解析不了？
#    死信的意义就是「这一步确实没做」——不修根因的重投只会再死一次

# 3) 重投（scope: workflow:admin:instance，管理动作，不是 worker 自助）
curl -s -X POST "$BASE/api/runtime/external-tasks/{taskId}/retry" -H "X-Api-Key: $ADMIN_KEY"
```

重投成功返回 `204`；非死信返回 `409`；跨租户返回 `404`（不是 403，避免泄露存在性）。

**永远不要**为了让流程往下走而手工把 `nb_external_task.status` 改成 `COMPLETED`：那等于宣布「这一步做过」，
而它从没执行过，事后没有任何痕迹能发现。

### 3.2 事件投递死信

死信只含 `DEAD_LETTER`（`FAILED` 还在重试额度内，混进来会被误重投）：

```bash
curl -s "$BASE/admin/v1/events/deadletters?limit=20" -H "X-Api-Key: $ADMIN_KEY"

# 重投（scope: workflow:event:subscribe）
curl -s -X POST "$BASE/api/admin/events/{eventId}/deliveries/{subscriptionId}/retry" -H "X-Api-Key: $ADMIN_KEY"
```

> 重投前确认订阅方**已经能收**。至少一次语义在订阅方实现，同一条事件重投时 CloudEvents 的 `id` 不变——
> 订阅方必须按 `id` 去重，否则一次重投就是一次重复记账。

---

## 4. 升级与回滚

### 4.1 升级前

```bash
# 1) 备份（必须）：结构 + 数据。没有迁移记录表，升级靠脚本幂等重跑，回滚靠备份
mysqldump --single-transaction --routines -h $HOST -u $USER -p $DB > netbpm-$(date +%F-%H%M).sql

# 2) 只演练、不落库：先看脚本会做什么
dotnet run --project tools/NetBpm.DbMigrate -- check --provider mysql --connection "$CS"
```

### 4.2 升级

```bash
dotnet run --project tools/NetBpm.DbMigrate -- migrate --provider mysql --connection "$CS"
```

- **没有迁移记录表**：`migrate` 每次都跑全量幂等脚本。新表因 `create table if not exists` 自动出现；
  **新列不会**——它们靠脚本 ⑥ 节的幂等 `alter`（查 `information_schema` / `sys.columns`，缺了才加）。
- 先跑 `migrate`，再把宿主的 `NetBpm__AutoMigrate` 设回 `false`：生产上「应用启动时顺手改结构」是事故源。
- 升级后核对：`GET /api/admin/info` 的 `schemaVersion` 应与发布说明一致；`netbpm` 表应无新增缺列。

### 4.3 升级失败怎么回滚

1. **先回滚镜像**（旧版本对多出来的列是宽容的：它不查这些列）。
2. 只有当旧版本**读**新列失败时，才动数据：按 §4.1 的备份恢复整库。
3. **不要**手工 `drop column`：脚本会在下次 `migrate` 时再加回来，而这段窗口里写进去的数据会丢。

### 4.4 结构自检（升级后跑）

```sql
-- 平台表应存在这些列（v1.27 起）
select column_name from information_schema.columns
 where table_schema = database() and table_name = 'nb_event_subscription'
   and column_name = 'max_per_minute';         -- 缺 → migrate 没跑成功

select column_name from information_schema.columns
 where table_schema = database() and table_name = 'nb_instance_timeline'
   and column_name = 'round_no';
```

---

## 5. 数据迁移：从「内存 / 旧库」到平台形态

| 场景 | 做法 |
|---|---|
| 演示 → 独立部署 | 内存引擎的数据**不迁移**（本来就不落盘）。新库跑 `migrate --create-database`，然后重新部署 BPMN、重新登记租户与客户端 |
| 单机 `NetBpm.Rest` → `Platform.Service` | 同一个库即可直接切：两者共用组合根与表结构。切之前把 `RequireCredential`、限流 `Store`、队列 `Provider` 按 §1 配齐 |
| 老版本 → 新版本 | 只跑 `migrate`（幂等），不导入导出。**新列由 ⑥ 节补**，老数据不需要回填：`round_no = 0` 会被读端识别为「老数据」，退回连续段近似 |

**双跑校验**（切换前后各取一份，比对语义）：同一张单在两个宿主下 `GET /workflow/v1/instances/{id}/history`
的条目类型、环节、轮次、选项、办理人应完全一致——这正是 `DualModeTests` 在 CI 里守的东西。

---

## 6. 告警响应

告警出口只有两个：**日志**（`LoggingAlertSink`）与**指标计数**（`netbpm.external_task.alert` 等）。
没有内置的邮件 / webhook——请在你的采集侧做规则，不要指望 NetBpm 自己发通知。

| 告警码 / 指标 | 含义 | 响应 |
|---|---|---|
| `external_task.no_worker` | 任务登记后超阈值无人领取 | 起 worker / 修 topic / 查网络策略。**只告警不动数据**：自动重投会把「worker 有 bug」掩盖成「在重试」 |
| `external_task.dead_letter` | 重试耗尽，已进死信 | §3.1 |
| `event.delivery.dead_letter` | 某订阅投递重试耗尽 | §3.2 |
| `netbpm.outbox.failure{dead_letter="true"}` 增长 | 事件发布失败并进死信 | 先看订阅方是否 5xx，再看签名与地址 |
| `netbpm.timer_job.failure` / `netbpm.async_job.failure` 增长 | 定时 / 异步作业失败 | 多为表达式或委托解析问题，查对应实例的 `LAST_ERROR_` |
| `netbpm.admin.authorization.denied` 突增 | 大量 403 | 接入方配错 scope，或有人在扫接口 |
| `netbpm.platform.surface.denied` 突增 | 客户端在 `/api/**` 做业务写入 | 接入方还在用旧面（已封存），让他们改走 `/workflow/v1` |
| `netbpm.outbox.oldest_pending_age` > 900s | 发布链路已断（订阅方 5xx / 发布器没起 / 限速无限推迟） | §7，**打在年龄上而不是条数上**：`pending` 高可能只是瞬时洪峰 |
| `netbpm.outbox.backlog_stale` = 1 | 积压采样已停，上面三个数不可信 | 查宿主进程与 `NetBpm:Outbox:Metrics:Enabled`；**别把此时的「积压 0 条」当成正常** |

冷却与去重：外部任务告警按「告警码 + 任务 id」冷却，冷却期内只报一次。**冷却不是抑制问题**，
连续多次冷却到期后仍然报警，说明根因没解决。

---

## 7. 事件投递排障

```bash
# 一眼看全貌：outbox 积压 + 每订阅分布
curl -s "$BASE/admin/v1/events/stats?since=1h" -H "X-Api-Key: $ADMIN_KEY"
```

返回三块：

- `outbox.pending` / `outbox.deadLetter` / `outbox.oldestPendingAt`：**最老一条的年龄**比总数更能说明问题。
  这三个数同时也进了 Meter（`netbpm.outbox.*`），Prometheus 可直接告警 ——
  端点仍然保留，因为排查时「是哪几条」比「有多少条」更关键。
- `subscriptions[].delivered / failed / deadLetter / pending`：`failed` 是「还有重试额度」（会自愈），
  `deadLetter` 是「已停投」（要人管）——两者分开计，别混着看。
- `name` 为 `null` 的行是**孤儿订阅**：配置删了但投递记录还在。它照样出现，否则「配置删了、死信留一堆」永远发现不了。

常见原因：

| 现象 | 原因 | 处理 |
|---|---|---|
| `pending` 持续增长且不下降 | 没有副本在发布（outbox 服务未启用 / `EnableOutbox=false`） | 打开 outbox；确认后台服务已启动 |
| 某订阅 `failed` 卡在高位 | 订阅方 5xx 或超时 | 看订阅方日志；必要时先 `POST /api/admin/event-subscriptions/{id}/status` 停投 |
| 某订阅 `pending` 不降但没失败 | 被**单订阅限速**推迟（`max_per_minute`） | 这是设计行为：推迟不消耗尝试次数。确认配额是否太小 |
| 签名校验不过 | 时钟偏移 / 密钥轮换未同步 | 对齐 NTP；订阅方要接受轮换宽限期内的双密钥 |

---

## 8. 密钥轮换

| 对象 | 命令 | 宽限 |
|---|---|---|
| 平台客户端（`nb_api_client`） | `POST /admin/v1/clients/{clientId}/rotate` | 旧密钥继续可用，直到宽限期结束（默认 7 天，见表定义） |
| 事件订阅（`nb_event_subscription`） | `POST /api/admin/event-subscriptions/{id}/rotate` | 新密钥立即生效；订阅方需**先**更新再切断 |

两条纪律：

1. **签发密钥不做幂等**。平台面幂等键会把首次响应体存进 `ACT_IDEMPOTENCY`，而这里响应体含明文密钥——
   为了一次重试拿回同一密钥就得把明文落进另一张表，等于破坏「只存摘要、只出现一次」。
   **丢响应的恢复动作是再 `rotate` 一次**，不是重放。
2. 密钥只在创建 / 轮换的响应里出现一次；读接口只有 `secretPreview`（`****` + 后 4 位）。

---

## 9. 限流与配额排障

| 现象 | 先确认 | 说明 |
|---|---|---|
| 突然大量 429 | `NetBpm__RateLimit__Store` | `Memory` 只对单副本正确；多副本实际配额 = 副本数 × 配置值 |
| 429 时没有 `Retry-After` / 限流头 | Redis 不可用（默认 **fail-open**） | 放行但 `Counted=false`，中间件不写头——这是「这次没被计数」，不是「额度无限」 |
| 想「宁可拒绝」 | 把 `FailOpenOnError` 设为 `false` | 代价是 Redis 抖动期间全站 429 |
| 单订阅投递被推迟 | 订阅的 `max_per_minute` | 0 = 不限；API 上限 6000。**超限是推迟，不是失败**（不消耗尝试次数） |

配额现状：`GET /admin/v1/quotas`（scope `workflow:admin:client`，返回租户级与客户端级两层）。

---

## 10. SLO 与度量口径

| SLO | 目标 | 度量来源 | 当前实测（本机，v1.27） |
|---|---|---|---|
| 可用性 | `/health/ready` 成功 ≥ 99.9% / 月 | 探针采集 | — |
| 读延迟（详情） | p95 < 80ms（100 并发） | `LoadTests` / APM | **51ms** |
| 读延迟（列表） | p95 < 300ms（回归线，非目标） | `LoadTests` | **150ms** |
| 写延迟（发起） | 串行 p95 < 200ms | `LoadTests` | **21ms** |
| 写并发 | p95 < 400ms（回归线） | `LoadTests` | **340ms** |
| 事件投递成功率 | ≥ 99%（`result=success` 占比） | `netbpm.event.delivery` | — |
| 事件积压 | 最老一条待发布事件 < 5 分钟 | `netbpm.outbox.oldest_pending_age` / `GET /admin/v1/events/stats` | — |
| 死信 | 新增死信 = 0（出现即处理） | §3 两个查询 | — |

> 列表读的 300ms 不是「达标」，是**回归线**：租户内的过滤与排序在内存里做（清单 §3 ① 第 1 条），
> 要根治得自建实例索引表，那是另一个项目。详情读的 80ms 才是真目标。

压测怎么跑：

```bash
NETBPM_RUN_LOAD_TESTS=1 \
  ./NetBpm.IntegrationTests.exe -class NetBpm.IntegrationTests.LoadTests -noLogo
```

默认不跑（结果随机器跳动，常驻门禁会「今天红明天绿」）。跑之前它会**清空引擎表**并把线程池最小线程数抬到并发数以上——
前者保证基线口径固定，后者避免把 .NET 的线程注入速率测成服务端延迟（不清线程池时会出现「p50 ≈ p95 ≈ 10s」的假象）。

指标清单（OTel，Meter `NetBpm.Engine` / `NetBpm.Admin`）：

| 指标 | 类型 | 标签 |
|---|---|---|
| `netbpm.command.executed` | Counter | `command` / `result` |
| `netbpm.command.duration` | Histogram(ms) | — |
| `netbpm.command.retry` | Counter | — |
| `netbpm.timer_job.failure` / `netbpm.async_job.failure` | Counter | `dead_letter` |
| `netbpm.outbox.failure` | Counter | `dead_letter` |
| `netbpm.external_task.alert` | Counter | `code` / `tenant` |
| `netbpm.event.delivery` | Counter | `subscription` / `result` |
| `netbpm.admin.authorization.denied` | Counter | `netbpm.admin.scope` / `netbpm.auth.mode` |
| `netbpm.admin.audit.recorded` / `.purged` | Counter | `netbpm.admin.action` / `netbpm.audit.success` |
| `netbpm.admin.audit.write_failures` | Counter | `netbpm.admin.action` / `netbpm.auth.mode` |
| `netbpm.platform.surface.denied` | Counter | `netbpm.admin.action` / `netbpm.auth.mode` |
| `netbpm.outbox.pending` | Gauge | — |
| `netbpm.outbox.dead_letter` | Gauge | — |
| `netbpm.outbox.oldest_pending_age` | Gauge(s) | — |
| `netbpm.outbox.backlog_stale` | Gauge | — |

积压四个 Gauge 由 `OutboxBacklogMetricsService` 按间隔采样写入（默认 15 秒，
`NetBpm:Outbox:Metrics:IntervalSeconds`）：**看年龄不看条数**（`oldest_pending_age`），
且 `backlog_stale=1` 时三个数都不可信 —— 采样停了之后，看板会一直显示最后一次的数字。

**审计的两个指标不能混着看**：

- `netbpm.admin.audit.recorded{netbpm.audit.success="false"}`：**动作本身失败**（目标不存在、状态冲突）。
  对应 `NetBpmAdminActionFailed`（ticket）。
- `netbpm.admin.audit.write_failures`：**审计没写进去**，而动作成功了。对应
  `NetBpmAdminAuditWriteFailing`（**page**）。后者才是「动作发生了、没人知道」——
  审计写入是 fail-open（不阻断运维），所以除了这条告警没有别的信号；
  恢复之后，故障窗口内的动作只能靠访问日志回溯。

导出默认**关闭**。打开方式二选一：`NetBpm__Otel__Enabled=true`，或直接设 `OTEL_EXPORTER_OTLP_ENDPOINT`
（容器编排里注入端点即生效）。

---

## 11. 常见口径（免得当成 bug 报上来）

- **`nodeOutcomes.pending` 可能大于当前待办数**：或签提前结束会删掉剩余任务，但那些人确实没投票，
  所以 `pending = 应到 − 已投`，不是「当前待办数」。
- **同一 `activityId` 出现两次**：节点被重办（退回 / 跳转），按 `round` 分成两轮各自成账。
  `round` 为 0 的是老数据，读端退回「连续段近似」。
- **`actorId` 为空但 `actorCallerId` 有值**：纯服务令牌发起的单，没有人这一层。
- **结论为空**：运行中的实例没有结论；三级回落只对结束实例生效。
- **跨租户查询返回 404 而不是 403**：读操作不泄露「存在性」。写操作的租户就在请求里，
  调用方本来就知道它存在，所以跨租户**写**返回 `403 tenant.forbidden`。
- **异源前端调接口全挂、控制台只有一句 CORS error**：CORS 默认**不启用**（同源托管不需要）。
  必须显式配 `NetBpm:Cors:AllowedOrigins`（逗号分隔的数组，环境变量写 `__0` / `__1`），
  逐个列来源；**不支持通配**，也不开 `AllowCredentials` —— 凭据走 `X-Api-Key` / `Authorization`
  请求头，不依赖浏览器自动带 Cookie。白名单外的来源不会收到 `Access-Control-Allow-Origin`，
  服务端照常返回业务结果（拦截由浏览器完成，不是 403）。
  预检（`OPTIONS`）装在鉴权之前，所以它不带凭据也能过；真实请求仍然要凭据。

---

## 12. 保留期与清理

| 数据 | 配置 | 默认 |
|---|---|---|
| 历史归档（`ACT_HI_*`） | `NetBpm:Admin:ArchiveRetentionDays` | 见 `AdminArchiveCleanupService` |
| 表单快照 `nb_approval_snapshot` | 同上（共用同一个 cutoff） | 同上 |
| 参数修订 `nb_parameter_snapshot` | 同上 | 同上 |
| 管理审计 `admin_audit_log` | 同上 | 同上 |
| 事件投递 `nb_event_delivery` | `NetBpm:Events:DeliveryRetentionDays` | `0`（关闭，永久保留） |

清理在 `AdminArchiveCleanupService` 的同一循环里做，共用同一个保留期——分开配会出现「快照删了、时间线还在」
这种半截状态，事后无法还原当时看到的表单。

事件投递走**独立**的一个键（`EventDeliveryCleanupService`，节奏同审计/归档：启动后 5 分钟首轮、之后每 6 小时）：

- 只删**终结态**：`DELIVERED` 按 `delivered_at`、`DEAD_LETTER` 按 `updated_at`；
- `PENDING` / `FAILED` **永不删**——它们还在重试链路上，删掉等于把「这条还没投成」伪装成「从来没有这条」；
- ⚠️ 死信同时是运维的人工重投队列，保留期到点即「放弃这条投递」。该值应当 ≥ 运维实际响应窗口；
  开启后每轮日志会写明清理条数，死信被清掉这件事有据可查。

---

## 13. 崩溃恢复演练

> 这一章回答的是一个**正确性**问题，不是可用性问题：
> 进程可以死，但「已经答应的工作」不能假装没发生过。压测、SLO、看板都齐了之后，
> 唯一还没被验证的就是「强杀之后那些在途状态到底还在不在、能不能续做」。

### 13.1 准备：制造在途状态

空库演练没有意义。先让库里出现下面四类在途状态中的**任意一类**（起一个带 `activiti:asyncBefore`
节点的实例是最省事的，它会同时产生作业与 outbox 事件）：

| 在途状态 | 表 | 崩溃后的预期 |
|---|---|---|
| 异步作业 | `ACT_RU_JOB` | 行还在；租约（`LOCK_OWNER_` / `LOCK_EXP_TIME_`）到期即回收，重启后由执行器续做 |
| 定时作业 | `ACT_RU_TIMER_JOB` | 同上 |
| 待投递事件 | `ACT_EVT_OUTBOX`（`PUBLISHED_=0`） | 行还在；`NEXT_ATTEMPT_` 兼作租约，到期重投；失败累计 5 次才进死信 |
| 外部任务锁 | `nb_external_task`（`status='LOCKED'`、锁列 `lock_expires_at`） | 锁到期自动回收，**不需要有人解锁**；迟到的 worker 回执会被拒 |
| 幂等占位 | `ACT_IDEMPOTENCY` | 过期行由 `IdempotencyCleanupService` 清理（宿主启用后台服务才有） |

关键设计前提（这三句话决定了上面那张表）：作业 / outbox 行与业务变更在**同一个事务**里写入，
所以「推进了但没落作业」和「落了作业但没推进」都不可能发生；租约一律「到期自动释放」而不是靠显式解锁。

### 13.2 跑演练

```powershell
# 会强杀宿主进程（等价 kill -9：没有 graceful shutdown、没有 flush、没有 finally）
./tools/drills/crash-recovery.ps1 `
  -Connection "Server=127.0.0.1;Port=3306;Database=netbpm;User ID=root;Password=netbpm" `
  -ProcessId 12345            # ⚠️ 用 -ProcessName dotnet 会强杀机器上所有同名进程
```

脚本做三件事：**记基线 → 强杀 → 复查**（崩溃后一次、重启并等一轮轮询后再一次），最后打判定表。
退出码：`0` 全通过 / `1` 有 FAIL / `2` 没得练（库里没有在途状态，或找不到宿主进程）。

### 13.3 判定标准

| 判定项 | 通过条件 | 不通过意味着 |
|---|---|---|
| 崩溃不丢异步作业 | `ACT_RU_JOB` 条数不减少 | 作业与业务不同事务 —— 这是架构缺陷，必须修代码 |
| 崩溃不丢待投递事件 | 待投递条数不减少 | 同上（outbox 不生效） |
| 崩溃不吞历史 | `ACT_HI_ACTINST` 条数不减少 | 历史写入在事务外 |
| 重启后作业被续做 | 作业条数下降，或历史活动行增加 | 执行器没有扫到期作业，或租约没到期回收 |
| 重启后事件被续投 | 已投递条数增加 | outbox 发布器没跑，或租约没回收 |
| 过期外锁可被接管 | 能查到 `status='LOCKED' and lock_expires_at <= now` 的锁 | ——（这一项只做观测，锁靠到期回收，不需要人工解锁） |

### 13.4 本机实跑记录（2026-11-02）

环境：本机 MySQL（root / 空口令），宿主 = `NetBpm.Rest`（`--urls http://127.0.0.1:5199`），
在途状态来自集成测试留下的 outbox 待投递与外锁。

```powershell
powershell -File tools/drills/crash-recovery.ps1 `
  -Connection "Server=127.0.0.1;Database=netbpm;User ID=root;Password=;Allow User Variables=true;" `
  -ProcessName dotnet -StartCommand .\tools\drills\restart-host.example.cmd
```

结果：**6 项全 PASS**（真实强杀宿主进程，脚本自动拉起后判定续做）。
附带确认了一件不是纸面结论的事：宿主启动后 `ACT_EVT_OUTBOX` 里那 10 条待投递被继续投递完。

⚠️ 这一轮实跑抓到三个真问题，都已修（**没有实跑就不会发现**）：

| 问题 | 现象 | 修法 |
|---|---|---|
| 列名写错 | 外部任务锁写成作业的 `LOCK_EXP_TIME_`，真实列是 `status='LOCKED'` + `lock_expires_at` | 按 DDL 改正脚本与本章的表 |
| 判定假阳性 | 库里本来没有作业时，「重启后作业被续做」判 FAIL —— 把「没造在途状态」报成「没续做」 | 崩溃后作业数为 0 时该项记为**不适用**。假阳性比漏报更伤：它会让人习惯性忽略这条判定 |
| 硬依赖 mysql 客户端 | 机器上没装，脚本第一条查询就挂 | 自动回退到 `NetBpm.DbMigrate query`（新增只读查询子命令）；顺带修掉连接串缺库名、以及两处 PowerShell 7 专属语法（PS 5.1 下直接语法错） |

### 13.5 没有 mysql 客户端时

照着查就行，判定同上：

```sql
select count(*) from ACT_RU_JOB;
select count(*) from ACT_RU_TIMER_JOB;
select count(*) from ACT_EVT_OUTBOX where PUBLISHED_ = 0;
select count(*) from ACT_EVT_OUTBOX where PUBLISHED_ = 1;
select count(*) from nb_external_task where status = 'LOCKED';
select count(*) from ACT_IDEMPOTENCY;
select count(*) from ACT_HI_ACTINST;
```

### 13.6 多副本演练（两个副本 + 同一个库）

单副本演练证明的是「进程死了，数据还在」。多副本要回答的是另一件事：
**别人做了一半的活，另一个副本会不会再做一遍**。

前提（见 §1）：`NetBpm__Jobs__Queue__Provider=Redis`、`NetBpm__RateLimit__Store=Redis`。
这两个不换，多副本下同一作业会被每个副本各执行一次，实际配额也变成「副本数 × 配置值」。

```powershell
# 1) 起两个副本（不同端口，同一个库）
dotnet run --project src/NetBpm.Rest -- --urls http://127.0.0.1:5199   # 副本 A
dotnet run --project src/NetBpm.Rest -- --urls http://127.0.0.1:5299   # 副本 B

# 2) 造在途状态（同 13.1），记下作业 id 与租约到期时间
# 3) 只强杀 A（等价 kill -9）—— 脚本只认库与进程 id，两副本场景照用
./tools/drills/crash-recovery.ps1 -Connection "$CS" -ProcessId <A 的 pid> `
  -StartCommand .\tools\drills\restart-replica-a.cmd
```

> ⚠️ 别用 `-ProcessName dotnet`：两个副本同名，会把 B 一起杀掉，那就退化成单副本演练了。

判定表（查同一张库，不查进程）：

| 判定项 | 通过条件 | 说明 |
|---|---|---|
| 崩溃副本的租约不会永久占位 | 租约到期后 `LOCK_OWNER_` 被另一副本接走 | 抢租约的条件是「无人持有 / 已过期 / 本来就是自己」，所以刚崩溃那一会儿别人**抢不到是对的** |
| 作业不被执行两次 | 目标活动在 `ACT_HI_ACTINST` 里只多一行 | 靠 `LOCK_EXP_TIME_` 到期 + `ACT_CMD_IDEMPOTENCY` 去重双保险 |
| outbox 事件不被投两次 | 该行 `PUBLISHED_=1`，订阅侧按事件 id 去重 | `NEXT_ATTEMPT_` 兼作租约；失败累计 5 次才进死信 |
| 外锁可被另一副本接管 | `lock_expires_at` 过期后能被重新 fetch | 迟到的旧持有者回执会被拒 |

⚠️ **本机未能实跑的部分（如实记录，别当成已验证）**：这台机器没有 Docker，也没有 Redis，
所以 `Jobs__Queue__Provider=Redis` 这条路径（Redis 分发 + 在途集合）没被真跑过；
13.4 那轮覆盖的是**数据库租约语义**（`DapperJobStoreBase` 的 acquire / renew / release），
它不是 Redis 队列本身。要补齐：

```powershell
# 单元测试：给了连接串，RedisJobQueueTests 整组自动参与（否则整组跳过）
$env:NETBPM_TEST_REDIS = "127.0.0.1:6379"     # 带口令："127.0.0.1:6379,password=***"
```

宿主侧再按 §1 配好队列与限流，把 13.2 的脚本原样跑一遍即可（脚本只认库与进程，不认副本数）。

---

## 14. 附：常用命令

```bash
# 库探活 / 建库 / 迁移 / 只校验版本
dotnet run --project tools/NetBpm.DbMigrate -- migrate --create-database --provider mysql --connection "$CS"
dotnet run --project tools/NetBpm.DbMigrate -- check   --provider mysql --connection "$CS"

# 控制台账号（管理面登录用；JWT 里的 netbpm.admin 是引擎超管，等价于全部 workflow:* 平台 scope，只授给平台运维）
dotnet run --project tools/NetBpm.DbMigrate -- users add --username admin --display-name 管理员 --scopes netbpm.admin
dotnet run --project tools/NetBpm.DbMigrate -- users list

# 清库（**只用于测试库**；会清空全部引擎表数据，结构保留）
dotnet run --project tools/NetBpm.DbMigrate -- purge --provider mysql --connection "$CS"
```

集成测试口径（本地复现 CI 的跑法）：

```bash
# ⚠️ 绝不能不带 -class 全跑：EngineTableCleaner.Purge 会清库
./NetBpm.IntegrationTests.exe -class NetBpm.IntegrationTests.DualModeTests -noLogo
```

---

## 15. 双库矩阵：让 SqlServer 那一行真的跑起来

B1/B2 的功能矩阵要在 MySQL 与 SqlServer 上各跑一遍（`DualDatabaseFeatureMatrixTests` 等六组）。
SqlServer 那一行**默认可能整行跳过** —— 跳过不是通过，CI 里那片绿要看得出来是跑过的绿。

供给方式按序探测：

1. `NETBPM_SQLSERVER_CONNECTION`（指向已有实例）——最省事，不需要 Docker；
2. Testcontainers 拉 `mcr.microsoft.com/mssql/server:2022-latest`（需要 Docker 在跑）；
3. 都没有 → 该方言动态跳过。

**没有 Docker 的 Windows 机器上，用 LocalDB 顶上**（`sqllocaldb` 随 SQL Server 工具装好）：

```powershell
sqllocaldb create "NetBpmTest" 17.0 -s        # 17.0 = SQL Server 2025；-s 立即启动
$env:NETBPM_SQLSERVER_CONNECTION = "Server=(localdb)\NetBpmTest;Integrated Security=True;TrustServerCertificate=True;"
```

库与表会自动创建（连接串没带 `Database=` 时按 `netbpm` 兜底）。先探一眼：

```bash
dotnet run --project tools/NetBpm.DbMigrate -- check --provider sqlserver \
  --connection "Server=(localdb)\NetBpmTest;Database=netbpm;Integrated Security=True;TrustServerCertificate=True;"
```

### 15.1 本机实跑记录（2026-11-02）

环境：SQL Server LocalDB `17.0.4025.3`，MySQL 走默认 `localhost`。

| 范围 | 结果 |
|---|---|
| `DualDatabaseFeatureMatrixTests` | 32/32（mysql + sqlserver 各 16） |
| 全量集成套件（SqlServer 参与后） | 486：通过 481 / 跳过 5（全是 Redis）/ 失败 0 |

这一轮顺手修了两个「假绿 / 假跳过」：

| 问题 | 现象 | 修法 |
|---|---|---|
| 跳过理由不可诊断 | 只说「方言 sqlserver 不可用」，看不出是 Docker 没跑、连不上，还是建表只跑了一半 | `TestDatabases.UnavailableReason` 带上真实异常；六处跳过文案统一改用它 |
| 连接串不带库名时误判不可用 | 库建好了、表也迁移完了，探测会话却落在默认库（master）上查 `sys.tables` → 判成不可用 | 探测前把 `InitialCatalog` / `Database` 补成引擎库名（MySQL 同口径） |

看板模板与告警规则在 `deploy/observability/`（Grafana JSON + Prometheus 规则），指标名以你采集链路的
实际规整规则为准，见该目录的 README。
