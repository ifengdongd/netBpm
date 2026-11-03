# 配置外部任务 worker（external task）

面向要**在引擎进程之外**执行某个 serviceTask 节点的同学：让客户系统（可以是 Go / Java / Python）
通过 HTTP 领走任务、干完活、回执结果，引擎再继续推进流程。

本篇的每条论断都在 `NetBpm.Admin`（`http://localhost:5081`）上用真实接口跑过，
响应片段是实测原文（id / 时间戳除外）。

> 适用基线：0.1.3（当前 `main`）。独立部署时把地址换成 `NetBpm.Platform.Service` 的地址即可，
> 外部任务端点与 scope 口径完全一致。

---

## 0. 先确认该不该用外部任务

| | serviceTask 委托 | 外部任务（`activiti:topic`） |
|---|---|---|
| 谁执行 | 引擎进程内的委托类 | 独立进程的 worker，HTTP 拉取 |
| 何时推进 | 委托返回后立刻 `LeaveAsync` | 登记后**原地不动**，等回执才推进 |
| 失败归属 | 引擎异常 → 事务回滚 | worker 上报 → 服务端扣次数 + 退避，耗尽进死信 |
| 适合 | 宿主自己能算、能连库 | 跨语言/跨团队、要独立扩缩容、要调用第三方系统 |

一句话：**能在本进程内同步完成的回写用委托；实现不在引擎进程里的用外部任务。**

---

## 1. 配置分三处，缺一处流程就停住

1. **BPMN**：serviceTask 上写 `activiti:topic`（唯一判定依据）；
2. **平台侧**：外部任务存储（数据库表 `nb_external_task`）+ 巡检告警 + worker 的凭据/scope；
3. **worker 进程**：`BaseUrl` / `Topics` / `WorkerId` / 锁时长 / 并发，以及每个主题的业务实现。

---

## 2. BPMN 侧

```xml
<bpmn:serviceTask id="svc_invoice" name="开票" activiti:topic="invoice" />
```

实测部署 `samples/NetBpm.ExternalWorker/processes/external-task-demo.bpmn20.xml`
（`start → 通知(notify) → 开票(invoice) → 复核(userTask) → end`）返回 `201`、`warnings: null`。

三个坑（均实测）：

1. **不要写 `activiti:type="external"`。** 它不在支持的 `activiti:*` 属性集合内，
   部署仍能过，但只留一条告警：

   ```json
   {"processDefinitionId":"extTypeAndTopic:2:15075","version":2,"deploymentId":"15074",
    "warnings":[{"rule":"unknown-activiti-attribute","elementId":"svc","elementType":"ServiceTask",
      "message":"属性 activiti:type=\"external\" 不在本子集支持范围，引擎会忽略它（不会报错也不会生效）。",
      "severity":0,"isError":false}]}
   ```

   只写 `type` 不写 `topic` 更糟：部署直接被拒。

   ```json
   {"rule":"required-attributes","elementId":"svc","elementType":"ServiceTask",
    "severity":1,"isError":true,
    "message":"服务任务必须指定 activiti:class / activiti:delegateExpression / activiti:expression / activiti:topic 之一……"}
   ```

2. **不能与 `activiti:class` / `delegateExpression` / `expression` 混写**：
   部署 400，提示“服务任务只能指定一种实现方式”。

3. **部署期不会校验“这个 topic 有没有 worker 订阅”。** 外部任务没有委托预解析这一步
   （`ServiceTaskDelegateGuard` 对 `ExternalTopic` 明确跳过）。所以
   **部署成功 ≠ 能跑通**，只有 worker 真接到任务才验证得了。

### 2.1 在控制台里怎么填

两条路都行：

1. **设计器里填**：`流程模型` → 编辑（内嵌设计器）→ 选中 serviceTask →右侧「服务任务」组里
   填 **主题（外部任务）**，对应 `activiti:topic`。同组的「类名 / 表达式 / 代理的表达式」
   保持为空——四个字段互斥，同时填会当场标红，后端也会拒绝部署。
2. **导入现成 XML**：`流程模型` → 导入模型，选 `.bpmn20.xml`。设计器能打开、能保存，
   `activiti:topic` 在 XML 往返中不会丢。

填完用模型行内的「部署」发布（走服务端校验，`topic` 是合法实现方式）。
⚠️ 不要用独立设计器（`/designer/`）列表里的「部署」——那条会跑前端即时校验，
历史上不认 `topic` 会误拦（现已在 `validation.ts` 中对齐）。

---

## 3. 平台侧

### 3.1 存储

| 宿主 | 实现 | 位置 |
|---|---|---|
| 数据库宿主（Admin / Platform.Service） | `DapperExternalTaskBroker` | 表 `nb_external_task` |
| 内存宿主 | `InMemoryExternalTaskBroker` | 进程内，重启即丢 |

嵌入式宿主若**没有** `IExternalTaskBroker`，执行到该节点会抛
`EngineException`（“需要外部任务支撑，但当前宿主未注册 IExternalTaskBroker”），
不会静默跳过——这是刻意的。

### 3.2 配置项（`NetBpm:ExternalTasks`）

```jsonc
"NetBpm": {
  "ExternalTasks": {
    "LockSeconds": 300,          // fetch 未带 lockDurationMs 时的默认锁时长
    "Retries": 3,                // ⚠️ 只影响「死信重投」的默认恢复次数
    "Watchdog": {
      "Enabled": true,
      "StartupDelaySeconds": 30,
      "PollIntervalSeconds": 60,
      "NoWorkerThresholdSeconds": 300,   // 登记后多久没人领算异常
      "AlertCooldownSeconds": 900,
      "AlertOnDeadLetter": true,
      "MaxResults": 500
    }
  }
}
```

两个容易误判的点：

- **登记时的初始剩余次数固定为 3**（行为类里直接用 `ExternalTaskRetryPolicy.DefaultRetries`），
  `NetBpm:ExternalTasks:Retries` 改不动它，只决定 `retry` 端点不传 `retries` 时的默认值。
- **巡检不会自动挂**，宿主必须显式 `AddNetBpmExternalTaskWatchdog(configuration)`；
  `NetBpmHost` 已挂（`src/NetBpm.Rest/Hosting/NetBpmHost.cs:178`），自写宿主别漏。
  它**只告警不动数据**，告警码 `external_task.no_worker` / `external_task.dead_letter`。

> 为什么必须有巡检：外部任务的失败**不会体现在任何 HTTP 返回码上**。worker 全挂时
> `fetch` 只是“正常返回空列表”，任务进死信也没有调用会报错——流程就那么静静地卡在节点上。

### 3.3 凭据与 scope

端点都在引擎原语面 `/api/runtime/external-tasks/*`：

| 端点 | 所需 scope（仅对注册客户端强制） |
|---|---|
| `fetch` / `complete` / `failure` / `unlock` / `GET external-tasks` | `workflow:external:task` |
| `retry`（死信重投） | `workflow:admin:instance` |

scope 校验**只对 `RequestAuthMode.Client` 生效**（`EngineSurfaceScopes.Ensure`）；
JWT、旧 `X-Api-Key`、以及“未配置鉴权”的开放模式不过这道闸。本地演示可以不配凭据，
生产必须走客户端凭据。

注册一个只干活的客户端（管理面，需要 `workflow:admin:client`）：

```bash
curl -s -X POST http://<host>/admin/v1/clients \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"invoice-worker","tenantId":"default",
       "scopes":["workflow:external:task"],
       "allowedProcessKeys":["externalTaskDemo"],
       "quotaPerMinute":600,"secretValidDays":90}'
# → {"clientId":"cl_xxx","clientSecret":"<明文，只出现这一次>","secretExpiresAt":"…"}
```

worker 侧带 `X-Api-Key: nbc.{clientId}.{clientSecret}`：

- `nbc.` 前缀本身就是承诺——格式损坏一律 401，**绝不降级成匿名**；
- 租户由客户端记录钉死；非 JWT 模式可另带 `X-Tenant-Id`，**JWT 模式不读该头**；
- **不要把 `workflow:admin:instance` 给 worker**：重投是管理干预，
  让 worker 能自己复活死信等于把退避机制架空。

---

## 4. worker 进程

`samples/NetBpm.ExternalWorker` 是参考实现，**刻意不引用任何 NetBpm 程序集**，
只依赖 HTTP + JSON，换成 Go / Java / Python 同样成立。

### 4.1 参数（全部可用环境变量 `NetBpm__xxx` 覆盖）

| 参数 | 默认 | 说明 |
|---|---|---|
| `BaseUrl` | `http://localhost:8080` | 平台地址，末尾斜杠自动去掉 |
| `ApiKey` | 空 | `nbc.{clientId}.{secret}`，走 `X-Api-Key` |
| `WorkerId` | 机器名 | **锁持有者标识，必须稳定且各副本不同** |
| `Topics` | `["invoice"]` | 订阅主题，与 BPMN 的 `activiti:topic` 逐字对应 |
| `TenantId` | 空 | 不填表示不收窄 |
| `PollIntervalSeconds` | 5 | 同时作为 fetch 的 `waitSeconds`（服务端短轮询，上限 30s） |
| `LockDurationMs` | 120000 | 单条任务持锁时长，要覆盖“处理耗时 + 一次网络抖动” |
| `MaxTasks` | 5 | 每轮最多领几条 |
| `MaxConcurrency` | 4 | 本地并发上限（闸住下游容量） |
| `ErrorBackoffMs` | 3000 | 拉取异常后的退避 |

启动：

```powershell
dotnet run --project samples/NetBpm.ExternalWorker `
  --NetBpm:BaseUrl=http://localhost:5081 `
  --NetBpm:Topics=invoice,notify `
  --NetBpm:WorkerId=worker-01
```

> `Topics` 在 appsettings 里是数组，环境变量只能给字符串：样例里有一层 `FlattenTopics`
> 把 `invoice,notify` 摊平成数组。少了这一步，容器里 `NetBpm__Topics=invoice,notify`
> 会变成单个元素 `"invoice,notify"`，两个主题都收不到。

### 4.2 接一个主题

实现 `IExternalTaskHandler` 即可，返回值就是要写回流程实例的变量：

```csharp
public sealed class InvoiceHandler : IExternalTaskHandler
{
    public string Topic => "invoice";

    public Task<Dictionary<string, object?>> ExecuteAsync(ExternalTaskDto task, CancellationToken ct)
    {
        var amount = ReadDecimal(task.Payload, "amount") ?? 0m;
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["invoiceNo"] = $"INV-{DateTime.UtcNow:yyyyMMdd}-{task.Id}",
            ["invoiceTotal"] = Math.Round(amount * 1.06m, 2),
        });
    }
}
```

注册：`AddSingleton<IExternalTaskHandler, InvoiceHandler>()` +
`AddHostedService<ExternalTaskPollingService>()`，循环固定是**领 → 做 → 回执**。

---

## 5. HTTP 契约

### 5.1 fetch —— 领取并加锁

```http
POST /api/runtime/external-tasks/fetch
{"workerId":"verify-w1","topics":["notify"],"maxTasks":1,"lockDurationMs":60000,"waitSeconds":2}
```

实测 `200`：

```json
[{"id":"15120","processInstanceId":"15115","executionId":"15116",
  "processDefinitionId":"externalTaskDemo:2:15113",
  "activityId":"svc_notify","activityName":"通知申请人","topic":"notify",
  "tenantId":"default","businessKey":null,
  "payload":{"amount":1000},"priority":0,"retries":3,
  "status":"LOCKED","workerId":"verify-w1","lockExpiresAt":"…",
  "nextDueAt":null,"errorMessage":null,"createdAt":"…","completedAt":null}]
```

- `workerId` 必填、`topics` 至少一个，否则 **400**
  （“请求体必须带 workerId：它是锁的持有者，回执时要对得上。” /
  “请求体必须带至少一个 topic。”）
- `maxTasks` 默认 10、上限 100；`waitSeconds` 默认 0、上限 30（内部每 1 秒看一次，短轮询）
- `lockDurationMs` 缺省取 `LockSeconds`
- `payload` 是**节点登记那一刻**的变量快照，不是实时视图：之后引擎侧改变量不会推给 worker，
  worker 回执的变量才写回流程

### 5.2 complete —— 成功回执

```http
POST /api/runtime/external-tasks/{taskId}/complete
{"workerId":"verify-w1","variables":{"invoiceNo":"INV-VERIFY-5","invoiceTotal":1060.00}}
```

实测 `204`。变量写回并推进节点：下一个节点的 `payload` 里能看到 `"notified":true`，
流程最终停在“复核”，实例变量为
`{"amount":1000,"notified":true,"invoiceNo":"INV-VERIFY-5","invoiceTotal":1060}`。

- 同一任务再 complete → **409**
- `workerId` 不匹配 → **409**（“锁不属于 worker …”）

锁校验在**命令层**做，不只是端点：A 领取后锁过期被 B 接手，A 的回执必须被拒，
否则一次调用会写入两次副作用。

### 5.3 failure —— 失败回执

```http
POST /api/runtime/external-tasks/{taskId}/failure
{"workerId":"verify-w1","errorMessage":"验证死信","errorDetails":"ScriptVerify","retries":0}
```

实测 `204`，随后查该任务：

```json
{"id":"15073","topic":"notify","retries":0,"status":"DEAD_LETTER","workerId":null,
 "errorMessage":"验证死信","errorDetails":"ScriptVerify","payload":{"amount":5}}
```

- `retries` 语义是“**剩余**次数”：不传则当前值 −1；结果 ≤ 0 进死信
- 死信后再 complete → **409**（“已进死信：重试耗尽，需由管理端点重投后才能再回执”）
- **流程原地不动**：失败绝不跳过节点
- 退避：`5s → 10s → 20s … 封顶 5min`（写入 `next_due_at`，期间领不到）

### 5.4 unlock / retry / 查询

| 请求 | 结果 |
|---|---|
| `POST /external-tasks/{id}/unlock` `{"workerId":"…"}` | `204`，立刻回 `PENDING`，**不扣重试**；workerId 不符 → **400** |
| `POST /external-tasks/{id}/retry` `{}` 或 `{"retries":3}` | `204`，仅死信可重投；非死信 → **409** `external_task_not_dead_letter` |
| `GET /external-tasks?topic=&status=&processInstanceId=&maxResults=` | 列表，默认 50、上限 200 |

注意 unlock 与 complete 的失败码不同：**unlock 用 400，complete 用 409**（均实测）。

查询 / 重投 / 解锁现在也有控制台入口：侧栏「外部任务」（`/external-tasks`）可按 topic、状态、
流程实例筛选，死信一键重投、锁定任务释放锁、查看领取时的变量快照。
页面**故意不提供领取与回执** —— 那会抢走 worker 的锁，控制台只做观察与干预。

### 5.5 状态机

```
PENDING ──fetch──▶ LOCKED ──complete──▶ COMPLETED
   ▲                 │
   │                 └─failure──▶ PENDING(退避, next_due_at) ──retries≤0──▶ DEAD_LETTER
   │                 └──锁到期（worker 崩溃/超时）──▶ 可被任意 worker 重领
   └────────────────── retry（管理端点，仅死信）──────────────────────────┘
```

---

## 6. 写 worker 的 7 条硬规则

1. **workerId 稳定、各副本不同。** 它是锁的归属键；换 id 等于放弃已领任务，只能等锁过期。
2. **锁时长一次给足。** REST 只有 fetch/complete/failure/unlock/retry，**没有续租端点**
   （broker 层有 `ExtendLockAsync`，未暴露成 HTTP）。处理超过锁时长 → 被别人重领 → 重复执行。
3. **业务必须可重复执行（at-least-once）。** 用 `task.id` 或自己的业务单号做幂等键。
4. **业务失败只上报 failure，不在 worker 里重试。** worker 重试 3 次 × 服务端 3 次 = 9 次重复。
5. **网络异常既不回执也不本地重做**：让锁自然过期，由服务端回收后重发。
6. **领到没注册处理逻辑的 topic，立刻上报 failure**，别“领了不做”等锁过期才被发现。
7. **并发闸门分开看**：`MaxTasks` 决定领多少，`MaxConcurrency` 决定同时往下游压多少。

---

## 7. 跑通一次（最小步骤）

```bash
# 1) 宿主（管理端 5081，开发配置免凭据；库 netbpm_admin）
dotnet run --project src/NetBpm.Admin

# 2) 部署含 topic 的流程
curl -X POST "http://localhost:5081/api/designer/deploy?resourceName=ext.bpmn20.xml&deploymentName=ext" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: text/xml; charset=utf-8' \
  --data-binary @samples/NetBpm.ExternalWorker/processes/external-task-demo.bpmn20.xml

# 3) 发起（amount 由发起方给，worker 算完写回）
curl -X POST http://localhost:5081/api/runtime/process-instances \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"processDefinitionKey":"externalTaskDemo","variables":{"amount":1000}}'

# 4) worker
dotnet run --project samples/NetBpm.ExternalWorker \
  --NetBpm:BaseUrl=http://localhost:5081 --NetBpm:Topics=invoice,notify

# 5) 验收：外部任务应全部 COMPLETED，人工待办出现「复核」
curl "http://localhost:5081/api/runtime/external-tasks?processInstanceId=<id>" -H "Authorization: Bearer $TOKEN"
curl "http://localhost:5081/api/runtime/tasks?processInstanceId=<id>"   -H "Authorization: Bearer $TOKEN"
```

也可以不开 curl：控制台侧栏「外部任务」（`http://localhost:5081/external-tasks`）能直接看到
这两个任务的状态、锁与剩余次数，并在死信时一键重投。

> 想先验证「宿主里注册了 handler 之后流程能不能被推完」，可以不装独立 worker：
> `samples/NetBpm.Demo` 自带一条闭环用例（handler 用 DI 注册 + 进程内 worker 领取回执），
> `curl -X POST http://localhost:22613/api/external-task/case/run` 会返回 10 步断言结果，
> `Passed: true` 即链路通。细节见 [NetBpm.Demo README](NetBpm.Demo/README.md)。

Linux / macOS 可直接跑现成脚本
`samples/NetBpm.ExternalWorker/run-demo.sh`（起宿主、部署、起 worker、发起、
演示失败与死信重投，并在最后断言“失败实例没有推进”）。

---

## 8. 排错对照表

| 现象 | 原因 | 处置 |
|---|---|---|
| `fetch` 一直 `[]` | topic 拼错 / 订阅串带空格 / 部署的是旧版本 | `GET /external-tasks?topic=…&status=PENDING` 核对字面值 |
| 任务 `LOCKED` 但没人处理 | 被别的 worker 领走 / 自己换了 workerId | 看 `workerId` 与 `lockExpiresAt`，等锁过期或持锁者 unlock |
| 流程卡住、查不到外部任务行 | 节点写的是 `activiti:class`；或宿主没注册 broker；或实例在旧定义上 | 改 BPMN 为 `topic`；看宿主日志；重新发布并对新实例验证 |
| 部署 400 `required-attributes` | 只写了 `activiti:type` 没写 `topic` | 补 `activiti:topic` |
| 部署 201 但 warnings 有 `unknown-activiti-attribute` | 写了 `type="external"`，被忽略 | 删掉该属性，只留 `topic` |
| complete 409“锁不属于…” | 锁过期被重领 / workerId 不符 / 已完成 | 重新 fetch 再回执 |
| complete 409“已进死信” | 重试耗尽 | 管理端 `POST /external-tasks/{id}/retry` |
| unlock 400“锁不属于…” | 同上（注意是 400 不是 409） | 用当前持锁者的 workerId |
| 业务被执行两次 | 锁短于处理耗时 / worker 本地重试 / 非幂等 | 加大 `LockDurationMs`、去掉本地重试、加幂等键 |
| 死信无人知晓 | 没挂 watchdog | 启用 `AddNetBpmExternalTaskWatchdog` + `AlertOnDeadLetter` |

---

## 9. 已知边界（实测，别踩）

- **没有续租端点。** 长耗时任务只能靠调大 `lockDurationMs`；超时即被重领，务必幂等。
- **`NetBpm:ExternalTasks:Retries` 不改初始次数**，登记时固定为 3。
- **`priority` 恒为 0**（行为里写死），当前无法在 BPMN 上设优先级，实际按登记顺序领。
- **`businessKey` 未回填**：实测发起时传了业务键，外部任务行与响应里的 `businessKey` 仍是 `null`。
  需要业务关联时把单号放进变量（会出现在 `payload` 里）。
- **删除流程实例不会清理 `nb_external_task`。** 实测删完实例后仍能查到 `LOCKED` / `PENDING` /
  `COMPLETED` 残留行（执行管理器只清定时作业与异步作业；
  `IExternalTaskBroker.DeleteByProcessInstanceAsync` 目前没有调用方）。
  这些残留行会占用同名 topic 的领取队列，建议按 `process_instance_id` 定期巡检/清理。
- **控制台只做观察与干预，不做领取。** 侧栏「外部任务」（`/external-tasks`）支持筛选、
  死信重投、释放锁、看变量快照（见 §5.4）；页面不提供 fetch / complete —— 那是 worker 的活，
  控制台代劳只会抢走锁。另外该页的数据源是 `GET /api/runtime/external-tasks`，
  一次最多 200 条，**没有分页**，任务多了要用筛选收窄。
- **样例 worker 的 `waitSeconds` 必须 > 0。** 若服务端立即返回空（`waitSeconds=0`），
  当前循环没有本地 delay 兜底，会退化成忙循环。

---

## 10. 源码索引

| 关注点 | 位置 |
|---|---|
| `activiti:topic` 解析与判定 | `src/NetBpm.Bpmn.Converter/ElementConverters.cs:416`（`ServiceTaskXmlConverter`） |
| 部署期“只能有一种实现” | `src/NetBpm.Bpmn.Validation/ProcessValidationRules.cs:604`（`RequiredAttributesRule`） |
| 行为分发 | `src/NetBpm.Engine/Behavior/ActivityBehaviorFactory.cs:48` |
| 登记任务、原地等待 | `src/NetBpm.Engine/Behavior/ExternalTaskActivityBehavior.cs:24` |
| 实体 / 状态词汇 / 退避策略 | `src/NetBpm.Engine/Jobs/ExternalTask.cs:6`、`:133`、`:162` |
| 领取命令（写操作走事务） | `src/NetBpm.Engine/Command/FetchExternalTasksCommand.cs` |
| 成功回执（锁校验 + 写回 + 推进） | `src/NetBpm.Engine/Command/CompleteExternalTaskCommand.cs` |
| 失败回执（不推进） | `src/NetBpm.Engine/Command/FailExternalTaskCommand.cs` |
| HTTP 端点与请求/响应契约 | `src/NetBpm.Rest/Endpoints/RuntimeEndpoints.ExternalTasks.cs:32` |
| scope 闸门 | `src/NetBpm.Rest/Security/EngineSurfaceScopes.cs:134`；`src/NetBpm.Platform.Contracts/Common/PlatformScopes.cs:83` |
| 配置绑定 | `src/NetBpm.Runtime/NetBpmServiceCollectionExtensions.cs:518`（选项）、`:551`（巡检） |
| 巡检告警 | `src/NetBpm.Runtime/Hosting/ExternalTaskWatchdogService.cs:14` |
| 持久化 | `src/NetBpm.Persistence/Jobs/DapperExternalTaskBroker.cs` |
| 表结构 | `tools/NetBpm.DbInit/Schema/SchemaCatalog.cs:1183`（`nb_external_task`） |
| 客户端注册 | `src/NetBpm.Rest/Endpoints/AdminClientEndpoints.cs:76` |
| 控制台页面 | `web/admin/src/pages/ExternalTasksPage.tsx`（列表 / 重投 / 解锁）、`web/admin/src/api/externalTasks.ts`（前端契约） |
| 样例 worker | `samples/NetBpm.ExternalWorker/`（`Program.cs`、`ExternalWorkerOptions.cs`、`ExternalTaskPollingService.cs`、`ExternalTaskHandlers.cs`） |
| 闭环用例（Demo） | `samples/NetBpm.Demo/ExternalTasks/`（handler 注册、进程内 worker、用例执行器与端点）、`processes/external-task-case.bpmn20.xml` |
