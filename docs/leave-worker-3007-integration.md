# 请假流程外部任务接入：控制台配置 + 3007 侧编码

> 适用前提：`leave_process` 已部署到 v4，「审批通过」的 `activiti:topic="approveComplete"`、「拒绝结束」的 `activiti:topic="refuseComplete"`。
>
> **一句话结论**：BPMN 里没有 URL，NetBpm 也不会反调 3007。是 **3007 的 worker 主动来 5081 领任务**，
> 干完活再回执；控制台这边只有 4 步配置，代码在 `samples/NetBpm.ServerTaskWorker/`（.NET 10）。

本文所有结论都在本机 `localhost:5081` 上跑过，不是照文档推的。实测记录见 §0。

---

## 0. 先看实测结果（省得怀疑方向）

| 动作 | 实际结果 |
|---|---|
| 匿名 `POST /api/runtime/external-tasks/fetch` | `401`，`{"code":"unauthorized","message":"缺少凭据：请提供 Bearer JWT、客户端密钥（nbc.*）或 X-Api-Key。"}` |
| 发起实例（`applyUser=1001`，`days=3`） | 实例 `15155`，首个待办 `task_apply`，`assignee=1001` |
| 提交申请 → 组长通过（`result=1`） | 外部任务 `15172`：`topic=approveComplete`、`status=PENDING`、`tenantId=default`、`retries=3` |
| `fetch`（`workerId=w-3007-1`） | 返回该条，`status` 变 `LOCKED`，`lockExpiresAt` = 领取时刻 + 5 分钟 |
| 先 `unlock`，再用**原** `workerId` 回执 | `409`：「锁不属于 worker 'w-3007-1'」 |
| 重新 `fetch` 后立刻 `complete` | `204`，任务变 `COMPLETED`，实例随后结束（运行态查不到，转历史） |
| 用样例 worker 跑真实客户端凭据 | 领到 `15194`（refuseComplete）→ `27ms` 完成；另起实例 `15203` → `30ms` 完成 |

---

## 1. 控制台要做的 4 件事

### 1.1 核对定义里的两个 topic

两条路径，任选：

- 模型列表 → 行内 **「导出」** → 用文本编辑器看 `<bpmn:serviceTask>`；
- 或者直接查库（`netbpm_admin`）：

```sql
select ID_, KEY_, VERSION_, DEPLOYMENT_ID_ from ACT_RE_PROCDEF where KEY_ = 'leave_process' order by VERSION_ desc;
-- leave_process:4:15153   leave_process   4   15152
```

v4 里应该正好是这两行，**没有** `activiti:delegateExpression`：

```xml
<bpmn:serviceTask id="task_approve_complete" name="审批通过" activiti:topic="approveComplete">
<bpmn:serviceTask id="task_refuse"           name="拒绝结束" activiti:topic="refuseComplete">
```

> 四个实现字段（类名 / 表达式 / 代理的表达式 / 主题）**只能填一个**。混填会在部署时被 400 拦下，
> 提示「服务任务只能指定一种实现方式」。改完记得走 **列表页「部署」**，设计器工具栏那个部署不更新发布状态。

### 1.2 注册接入客户端（worker 的身份证）

侧栏 **「接入管理」→ 「客户端」** 页签 → `注册客户端`。字段逐项如下：

| 字段 | 填什么 | 为什么 |
|---|---|---|
| 接入系统名称 | `请假流程-外部任务worker` | 出问题时能在审计里认出是谁 |
| 所属租户 | **`default`** | 实测外部任务行的 `tenantId` 是 `default`（定义租户为空时存储层归一成 `default`）。这里填别的值 → worker 一条都领不到 |
| 授权 scope | 只勾 **`workflow:external:task — 外部任务领取与回执`** | 别给 `workflow:admin:instance`：那等于让 worker 能自己复活死信，退避与死信机制就白做了 |
| 可用流程 key | `leave_process`（选填） | 该白名单作用于实例读取/发起，不作用于外部任务领取；填上是为了让这个客户端别处也没法乱看 |
| 配额（次/分） | `600` | fetch 是短轮询，`waitSeconds=20` 时每分钟约 3 次请求；600 足够 |
| 密钥有效期（天） | `90` | 过期后 worker 直接 401，注意提前轮换 |

### 1.3 保存密钥（只有这一次）

注册成功会弹一次性密钥。**响应里给的是整串** `nbc.cl_xxxxxxxx.yyyyyyyy`，
**不要自己再拼 `nbc.` 前缀**（会变成 `nbc.nbc.…`，鉴权直接失败）。

- 只显示这一次，关掉就再也拿不到；
- 丢了不要重建客户端 —— 行内 **「轮换密钥」** 会给新密钥，且**旧密钥还能用 7 天**（`RotationGraceDays`），足够滚动发布；
- 密钥过期时间在客户端列表里能看，建议设个提前一周的提醒。

> 🔑 **密钥只能进「用户机密」或环境变量，绝不能进仓库**：
>
> ```bash
> dotnet user-secrets set "Worker:ClientKey" "nbc.cl_xxxxxxxx.yyyyyyyy" \
>   --project samples/NetBpm.ServerTaskWorker
> ```
>
> `samples/NetBpm.ServerTaskWorker/appsettings.json` 是**被 git 跟踪**的，
> 而本仓库推到了 GitHub / Gitee 的**公开远端** —— 明文密钥写进去等于公开可用凭据
> （别人拿它就能以你的名义领任务、回执业务单）。
> 该文件的 `Worker:ClientKey` 因此留空，真实密钥只存在于用户机密（`%APPDATA%/Microsoft/UserSecrets/`，不进仓库）或环境变量。
>
> ⚠️ **密钥一旦进过 git 历史，删掉文件里的明文不等于失效**（历史里还在）：
> 正解是 **轮换** —— 控制台「接入管理 → 客户端 → 轮换密钥」签发新密钥 → 换进用户机密 → 旧密钥自动作废。
> 本机已于 2026-11-04 实测做过一次：旧密钥在轮换后立即 `401`，新密钥 fetch 正常。


### 1.4 观察与干预：侧栏「外部任务」

| 列 / 操作 | 含义 |
|---|---|
| 主题 | `approveComplete` / `refuseComplete`，**大小写敏感** |
| 状态 | `PENDING` 可领 / `LOCKED` 已被领走 / `COMPLETED` 已回执 / `DEAD_LETTER` 重试耗尽 |
| 剩余重试 | 登记时默认 3；每次失败回执 −1 |
| 锁到期 | `LOCKED` 时才有；过期后任务会**自动回到可领取**（见 §2 末） |
| 最近失败原因 | 来自 failure 回执的 `errorMessage` / `errorDetails` |
| 重投死信 | 需要 `workflow:admin:instance`；重投**不推进流程**，只是让它重新可领 |

> 这一页**故意没有「领取」按钮**：领取意味着加锁，控制台点一下就会把 worker 的活抢走 5 分钟。

---

## 2. 协议：worker 只需要会 5 个 HTTP 调用

基址 `http://<netbpm-host>:5081`，全部走 `X-Api-Key: nbc.{clientId}.{token}`。

| 用途 | 方法 / 路径 | 请求体 | 成功 |
|---|---|---|---|
| 领取 | `POST /api/runtime/external-tasks/fetch` | `{workerId, topics[], maxTasks, lockDurationMs, waitSeconds}` | `200 [任务]` |
| 成功回执 | `POST /api/runtime/external-tasks/{id}/complete` | `{workerId, variables{}}` | `204` |
| 失败回执 | `POST /api/runtime/external-tasks/{id}/failure` | `{workerId, errorMessage, errorDetails, retries, retryAfter}` | `204` |
| 放弃 | `POST /api/runtime/external-tasks/{id}/unlock` | `{workerId}` | `204` |
| 查询 | `GET /api/runtime/external-tasks?topic=&status=&processInstanceId=` | — | `200 [任务]` |

领取请求示例：

```json
{ "workerId": "stw-host1-8842-a1b2c3", "topics": ["approveComplete", "refuseComplete"],
  "maxTasks": 5, "lockDurationMs": 120000, "waitSeconds": 20 }
```

单条任务的实际返回（实测裁剪）：

```json
{
  "id": "15172",
  "processInstanceId": "15155",
  "processDefinitionId": "leave_process:4:15153",
  "processDefinitionKey": null,
  "activityId": "task_approve_complete",
  "activityName": "审批通过",
  "topic": "approveComplete",
  "tenantId": "default",
  "businessKey": "Leave-2026-1101",
  "payload": { "applyUser": "1001", "days": 3, "reason": "事假", "result": 1, "comment": "同意" },
  "retries": 3,
  "status": "LOCKED",
  "lockExpiresAt": "2026-11-03T07:37:29.121Z"
}
```

**四个必须知道的数据事实**：

1. `payload` 是**领取那一刻**的变量快照，之后引擎侧再变也不会回灌；worker 回执里的 `variables` 才会写回实例。
2. `businessKey` 是发起实例时传的**业务单号**（引擎会沿执行树找到根执行取 `BUSINESS_KEY_`）。
   发起时没传就是 null —— 样例 worker 这时用 `LEAVE-{processInstanceId}` 兜底落账。
3. `processDefinitionKey` **也是 null**（引擎为了少查一次库没填）。要 key 就从 `processDefinitionId` 里截：`leave_process:4:15153` → `leave_process`。
4. 时间字段是 **UTC**（`Z` 结尾），落库/展示前自己转。

**锁与退避的真实语义**（容易想错）：

- 锁默认 5 分钟（`NetBpm:ExternalTasks:LockSeconds`），也可以按主题单条指定；
- 领取时 `waitSeconds>0` 是**短轮询**：没任务就每秒看一次，最多等 30 秒，不是长轮询；
- 任务 `LOCKED` 且锁过期 → **自动回到可领取**，而**重试次数不减**。也就是说：worker 崩了不会被算作失败，只会被别人重新领走；
- 只有 `failure` 回执才扣重试，扣到 0 进 `DEAD_LETTER`，流程**原地不动**；
- 因此「worker 一直崩」不会进死信，需要靠巡检告警（`NetBpm:ExternalTasks:Watchdog`，默认开，60 秒一扫，死信也告警）。

---

## 3. 3007 侧编码

### 3.1 目录与启动

参考实现是 .NET 10 工程 `samples/NetBpm.ServerTaskWorker/`（不引用任何 NetBpm 工程，只靠 HTTP + JSON）：

```
WorkerOptions.cs             配置绑定 + 启动自检（兼容原 Node 版环境变量名）
NetBpmClient.cs              4 个 HTTP 调用：fetch / complete / failure / unlock
ServerTaskHandlers.cs        业务处理器 + topic 注册表（approveComplete / refuseComplete）
ServerTaskWorkerService.cs   主循环：领取 → 处理 → 回执
JsonLeaveBillStore.cs        幂等落账（JSON 文件替身，接口形状对齐真实仓储）
Program.cs                   HTTP 入口 /api/serverTask/{name} + /healthz
StartupMessages.cs           启动期直接打控制台的提示（降级说明）
run-worker.ps1 / .sh         从独立发布目录启动（避开 IDE 重新生成时的文件锁）
stop-worker.ps1 / .sh        按端口停掉运行中的 worker
```

> **在 IDE 里点「重新生成」报 MSB3021 / MSB3027？** 那不是代码错误，是**文件被占用**：
> `dotnet run` 把 exe 放在 `bin/Debug/net10.0/` 下，运行中的进程锁住了它，
> MSBuild 覆盖不了 —— 错误列表里全是复制文件的红叉，看不出真实原因。
> 用下面的 `run-worker.ps1`（先 publish 到 `artifacts/` 再从那里跑）就能根治：
> 运行实例与 IDE 的编译输出彻底分离，worker 开着也能随时重新生成。

```bash
cd samples/NetBpm.ServerTaskWorker

export NETBPM_BASE_URL=http://10.25.10.20:5081          # 或控制台所在地址
export NETBPM_CLIENT_KEY='nbc.cl_xxxxxxxx.yyyyyyyy'     # 1.3 拿到的整串
export WORKER_TOPICS=approveComplete,refuseComplete
export PORT=3007

dotnet run
```

在 IDE 里开发时改用脚本更省事（Windows 用 `.ps1`，bash 用 `.sh`）：

```
samples/NetBpm.ServerTaskWorker/run-worker.ps1
```

它会先 `dotnet publish` 到 `artifacts/server-task-worker/` 再从那里启动；
密钥来源优先级：`-ClientKey` 参数 > 环境变量 `NETBPM_CLIENT_KEY` > 用户机密 `Worker:ClientKey`。

启动日志：

```
2026-11-03T07:55:19.850Z info: Program[0] 业务接口已监听 http://localhost:3007/api/serverTask/{approveComplete|refuseComplete}
2026-11-03T07:55:19.890Z info: ...[0] [worker] 启动 workerId=stw-host-1304-8579aa topics=approveComplete,refuseComplete 引擎=http://localhost:5081 租户=default
```

> 环境变量名与早期 Node 版**完全一致**，换语言重写不用改运维脚本；
> .NET 侧同时支持标准层级写法 `Worker__NetBpmBaseUrl` / `Worker__Topics`（优先级低于上面这些兼容名）。
>
> 还没签客户端时也可以先跑：不带 `NETBPM_CLIENT_KEY` 启动，进程照常提供
> `/api/serverTask/{name}`（可先用 curl 验业务逻辑），只是不起 worker，
> `/healthz` 回 `status=degraded` 并给出缺什么。配好密钥重启即开始领取。

### 3.2 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `NETBPM_BASE_URL` | `http://localhost:5081` | 控制台与引擎端点同端口 |
| `NETBPM_CLIENT_KEY` | 空（不领任务） | `nbc.{clientId}.{token}` 原文；没配时降级为只提供业务接口 |
| `WORKER_TOPICS` | `approveComplete,refuseComplete` | 与 BPMN 的 topic 严格对应 |
| `WORKER_ID` | `stw-{host}-{pid}-{rand}` | **每进程一个**，别写死 |
| `WORKER_MAX_TASKS` | `5` | 单次领取条数（服务端上限 100） |
| `WORKER_LOCK_MS` | `120000` | 必须显著大于单条处理超时 |
| `WORKER_WAIT_SECONDS` | `20` | 没任务时等多久（上限 30） |
| `WORKER_TASK_TIMEOUT_MS` | `30000` | 单条业务超时 |
| `PORT` | `3007` | 本进程端口 |
| `RUN_WORKER` | `true` | `false` 时只提供业务接口 |

### 3.3 主循环的三条铁律

```csharp
// ServerTaskWorkerService.cs 摘要
var tasks = await _client.FetchAsync(stoppingToken);            // 领取（服务端短轮询）

foreach (var task in tasks)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    timeout.CancelAfter(_options.TaskTimeoutMs);

    var variables = await _dispatcher.DispatchAsync(task.Topic, task, timeout.Token);  // 业务处理
    await _client.CompleteAsync(task.Id, variables, CancellationToken.None);           // 回执
}
```

1. **回执必须用领取时的 `workerId`**。锁按 workerId 记；`unlock` 之后锁已清空，也必须重新 `fetch` 才能回执（实测用旧 id 直接 409）。
2. **业务成功但回执失败时，绝不能补发 `failure`**。业务已经落库了，补 failure 会把它记成失败并扣重试。正确做法：什么都不做，让锁过期被重新领取，靠业务幂等去重。
3. **处理失败走 `failure`，不走 `unlock`**。`failure` 扣重试 + 退避且流程不动；`unlock` 等于「我没看见过」，失败原因会被抹掉。

### 3.4 业务处理器（这就是「外部系统怎么处理」）

`/api/serverTask/{name}` 背后就是它，worker 复用同一函数，不存在两套实现漂移。

```csharp
// 通过 / 拒绝只差四个常量，校验、幂等、回写都在基类里
public sealed class ApproveCompleteHandler : LeaveCompletionHandlerBase
{
    public override string Topic => "approveComplete";
    protected override int ExpectedResult => 1;                        // 节点语义
    protected override string TargetStatus => LeaveBillStatus.Approved;
    protected override string ResultLabel => "APPROVED";
}
```

```csharp
// LeaveCompletionHandlerBase.ExecuteAsync 摘要
if (TaskPayload.TryGetInt(payload, "result", out var result) && result != ExpectedResult)
{
    // 值不对说明定义被改过，重试没有意义 → retries: 0 直接进死信
    throw new BusinessRuleException($"{Topic} 收到了 result={result}，与节点语义不符", retryable: false);
}

var upsert = await _store.MarkProcessedAsync(
    task.ProcessInstanceId!, TargetStatus,
    new LeaveBillPatch(Applicant: ..., Days: ..., Reason: ..., Comment: ..., Via: Via), ct);

return new Dictionary<string, object?>
{
    ["externalResult"] = ResultLabel,
    ["externalRef"] = upsert.Bill.Id,
    ["externalDuplicate"] = upsert.Duplicated,
    ["externalStatusConflict"] = upsert.Conflict,
};
```

`refuseComplete` 同结构，状态写 `REJECTED`。

业务关联键优先用 `task.businessKey`（发起时传的业务单号，一行 `select` 都不用回查）；
没传 businessKey 的老实例才退回 `LEAVE-{processInstanceId}`。真实库里的写法：

```sql
-- 幂等：只有 PENDING 才能被改成终态，影响行数 0 就说明别人处理过了
update leave_bill
   set status = 'APPROVED', handled_at = now(3), comment = ?,
       process_instance_id = ?
 where bill_no = ? and status = 'PENDING';
```

### 3.5 幂等必须做两层

外部任务是**至少一次**语义，同一实例可能被处理两次（回执丢包后锁过期、重投死信）。两层都要：

1. **状态机幂等**：`PENDING → 终态` 只用条件更新；已是目标状态直接返回成功。
2. **终态不可变**：已经是**另一个**终态时**不覆盖**，只回一个 `conflict` 标记。
   「已拒绝的单据被后来的通过请求改成已通过」是最难查的一类数据错误 —— 样例里这条是实测过的：
   对已 `APPROVED` 的单据再打 `refuseComplete`，返回 `externalStatusConflict: true`，库里状态不变。

### 3.6 吞吐与多副本

- 单进程串行处理即可满足控制台场景（实测单条 27~30ms）；
- 要多副本就多起进程：`workerId` 带 pid 与随机串天然不同，锁竞争由数据库仲裁（`update ... where status='PENDING' or 锁已过期`，影响行数即抢锁结果）；
- 别为了快把 `maxTasks` 调很大：一次领太多、处理又慢，锁会集体过期被别人重复领走。

---

## 4. 换成你们自己的技术栈

协议只有 5 个 HTTP 调用，任何语言都是同一套。要点排序：**先 fetch 拿 payload → 业务落库 → complete/failure**。

Java（Spring Boot）骨架：

```java
@Scheduled(fixedDelay = 1000)
public void poll() {
    var tasks = client.fetch(workerId, List.of("approveComplete", "refuseComplete"), 5);
    for (var task : tasks) {
        try {
            var vars = handlers.get(task.topic()).handle(task);          // 业务
            client.complete(task.id(), workerId, vars);                  // 回执
        } catch (PermanentException e) {
            client.fail(task.id(), workerId, e.getMessage(), 0);         // 直接死信
        } catch (Exception e) {
            client.fail(task.id(), workerId, e.getMessage(), null);      // 扣一次重试
        }
    }
}
```

.NET：`samples/NetBpm.ExternalWorker/` 里已有 `ExternalTaskPollingService` + DTO，把 topic 换成这两个即可。
`docs/leave-external-task-setup.md` §4 还有一份含 Dapper 仓储与幂等 DDL 的完整版。

---

## 5. 端到端联调（照着做）

前置：控制台在 5081、3007 的 worker 已用 §3.1 起来、`/healthz` 返回 `{"status":"ok", ...}`。

```bash
# 1) worker 活着、订阅的主题对
curl -s http://10.25.10.20:3007/healthz

# 2) 控制台发起一条 3 天的请假（引擎面；days<=3 会跳过经理审批直达 approveComplete）
curl -s -X POST "http://localhost:5081/api/runtime/process-instances" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"processDefinitionKey":"leave_process","startUserId":"1001",
       "variables":{"applyUser":"1001","days":3,"reason":"事假"}}'

# 3) 申请人提交（引擎面 complete，不带 optionId）
curl -s -X POST "http://localhost:5081/api/runtime/tasks/<taskId>/complete" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"variables":{"days":3}}'

# 4) 组长通过（result=1 走通过分支，result=2 走拒绝分支）
curl -s -X POST "http://localhost:5081/api/runtime/tasks/<leaderTaskId>/complete" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"variables":{"result":1,"comment":"同意"}}'

# 5) 看外部任务：应当很快从 PENDING → LOCKED → COMPLETED
curl -s "http://localhost:5081/api/runtime/external-tasks?maxResults=10" -H "Authorization: Bearer $TOKEN"

# 6) 看实例：结束的实例在运行态查不到，控制台「实例监控」里按历史查；
#    变量页签能看到 externalResult / externalRef
```

对不上时按顺序看：**外部任务页有没有这行** → 有就没到 worker（topic 拼错 / worker 没起）；
**有行一直 PENDING** → worker 没订阅这个 topic 或租户不符；
**LOCKED 不动** → worker 领了但没回执（进程崩了、业务超时）。

---

## 6. 排错对照表

| 现象 | 原因 | 处理 |
|---|---|---|
| `401 unauthorized` | 没带 `X-Api-Key`，或密钥被截断 | 用整串 `nbc.…`；确认没有多余空格 |
| `401` 但密钥看起来对 | 客户端被停用 / 已注销 / 密钥过期 | 客户端列表看状态与到期时间；轮换密钥 |
| `403` | scope 里没有 `workflow:external:task` | 用 PATCH 补 scope，别重建客户端 |
| `fetch` 返回 `[]` 但页面明明有任务 | ①topic 大小写不符 ②租户不符 ③任务在退避中（`nextDueAt`） ④锁未过期 | 依次核对；`waitSeconds` 不必调大 |
| `complete` 返回 `409` | 锁不属于该 `workerId`（换了 id / 已 `unlock` / 锁过期被抢） | 重新 `fetch` 再回执，不要复用旧 id |
| 任务一直 `PENDING` | worker 没起、订阅主题不对 | 看 `/healthz` 的 `topics` |
| 任务 `LOCKED` 不动 | worker 领了没回执 | 查 worker 日志；锁过期会自动回到可领（不扣重试） |
| 任务进 `DEAD_LETTER` | 连续失败把 3 次额度用完 | 修业务后「重投死信」（需管理 scope），或让 worker 改完代码重投 |
| 流程停在服务任务 | 回执没成功 | 外部任务页看状态与最近失败原因 |
| 部署时提示 `delegate-resolution` | 主题和 `delegateExpression` 混填，或名字打错 | 四个实现字段只留一个；名字精确匹配 |
| 回执成功但流程没走 | 变量没写对 / 网关条件不满足 | 看实例变量页签；`result` 必须是 1 或 2 |
| 控制台「我的待办」看不到 | `scope=MINE` 只捞候选人任务，不含已指派 | 用 `scope=CLAIMED` 或实例详情 |
| 时间比本地早 8 小时 | 接口返回 UTC | 展示层转换 |

---

## 7. 源码索引

| 关注点 | 文件 |
|---|---|
| 5 个端点与 DTO | `src/NetBpm.Rest/Endpoints/RuntimeEndpoints.ExternalTasks.cs` |
| 节点登记任务、payload 快照 | `src/NetBpm.Engine/Behavior/ExternalTaskActivityBehavior.cs` |
| 锁 / 抢占 / 可领取谓词 | `src/NetBpm.Persistence/Jobs/DapperExternalTaskBroker.cs` |
| 完成回执：写变量再推进 | `src/NetBpm.Engine/Command/CompleteExternalTaskCommand.cs` |
| 失败回执：扣重试 + 退避 | `src/NetBpm.Engine/Command/FailExternalTaskCommand.cs` |
| 巡检告警（默认 60s） | `src/NetBpm.Runtime/Hosting/ExternalTaskWatchdogService.cs` |
| 默认锁时长与重试数 | `src/NetBpm.Engine/Jobs/ExternalTask.cs`（`ExternalTaskOptions`） |
| 3007 侧完整样例（业务接口 + worker） | `samples/NetBpm.ServerTaskWorker/` |
| 最小 worker 样例（纯轮询，无业务接口） | `samples/NetBpm.ExternalWorker/` |
