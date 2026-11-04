# NetBpm.ServerTaskWorker

NetBpm 外部任务 worker 的 **.NET 10 参考实现** —— 也就是「3007 那一侧」的完整样子：
既暴露业务接口 `POST /api/serverTask/{name}`，又在同一个进程里跑 worker 去领任务、回执。

对应流程：`leave_process` v4，两个服务任务分别是 `activiti:topic="approveComplete"` 与 `activiti:topic="refuseComplete"`。

> 与 [`NetBpm.ExternalWorker`](../NetBpm.ExternalWorker/) 的分工：
> 那个是「最小 worker」—— 只有轮询，没有业务接口，用来说明协议；
> 这个是「外部系统的完整一侧」—— 业务 HTTP 接口 + 幂等落账 + worker，用来演示真实接入形态。

## 它是怎么工作的

```
3007 worker  ──fetch──▶  NetBpm 5081        （带 topic 领取，引擎加锁 5 分钟）
             ──处理──▶   ServerTaskDispatcher（本进程内的业务逻辑，HTTP 接口共用同一份）
             ──complete/failure──▶ NetBpm    （写回变量并推进，或扣重试原地不动）
```

网络方向是 **3007 → NetBpm**。BPMN 里没有 URL，引擎也不会反调 3007。

```text
WorkerOptions.cs          配置绑定 + 启动自检（含原 Node 版环境变量名兼容）
NetBpmClient.cs           4 个 HTTP 调用：fetch / complete / failure / unlock
ServerTaskHandlers.cs     业务处理器 + topic 注册表（approveComplete / refuseComplete）
ServerTaskWorkerService.cs主循环：领取 → 处理 → 回执
JsonLeaveBillStore.cs     幂等落账（JSON 文件替身，接口形状对齐真实仓储）
StartupMessages.cs        启动期直接打控制台的提示（没配密钥时的降级说明）
Program.cs                HTTP 入口 /api/serverTask/{name} + /healthz
run-worker.ps1 / .sh      从独立发布目录启动（避开 IDE 重新生成时的文件锁）
stop-worker.ps1 / .sh     按端口停掉运行中的 worker
```

## 在 IDE 里点「重新生成」报 MSB3021 / MSB3027

**先认这条**：它不是代码错误，是 **文件被占用**。

```text
无法将文件 "...apphost.exe" 复制到 "...\bin\Debug\net10.0\NetBpm.ServerTaskWorker.exe"。
文件 "...\bin\Debug\net10.0\NetBpm.ServerTaskWorker.exe" 正由另一进程使用。
```

`dotnet run`（含 IDE 的「运行」）把可执行文件放在 `bin\Debug\net10.0\` 下，而**运行中的进程锁住了自己的 exe**。
MSBuild 重新生成时要覆盖同一个文件 → 覆盖失败 → 错误列表里全是复制文件的红叉。

两种解法，**推荐第二种**：

**① 临时绕开**：先停掉 worker 再重新生成

```powershell
.\samples\NetBpm.ServerTaskWorker\stop-worker.ps1
```

**② 根治**：让运行实例脱离编译输出目录

```powershell
.\samples\NetBpm.ServerTaskWorker\run-worker.ps1
```

这个脚本先 `dotnet publish` 到 `artifacts\server-task-worker\`（已被 `.gitignore` 忽略），
再从那里启动。**运行时的文件与 IDE 的编译输出彻底分离**，两边不再抢同一个 exe ——
worker 开着也能随时重新生成。

> bash/macOS/Linux 用同名的 `run-worker.sh` / `stop-worker.sh`（脚本按 `.gitattributes` 保持 LF）。

密钥来源优先级：`-ClientKey` 参数 > 环境变量 `NETBPM_CLIENT_KEY` > 用户机密 `Worker:ClientKey` > 降级启动。

## 启动

### 第一次直接跑（还没配密钥）

什么都不设直接 `dotnet run --project samples/NetBpm.ServerTaskWorker`（或在 IDE 里按运行）**不会退出**：

```
============================================================
[worker 未启动] 没有配置客户端密钥 —— 进程照常运行，但不去引擎领任务
  业务接口仍然可用：http://localhost:3007/api/serverTask/{name}
  不会访问引擎：http://localhost:5081（配好密钥后重启即开始领取）
  ...
============================================================
```

这时 HTTP 业务接口照常可用，可以先用 curl 把业务逻辑（幂等落账、通过/拒绝状态机）验完，
再去控制台签发客户端、配好密钥重启进程。`/healthz` 会明确回 `status=degraded`。

### 配好密钥后

```bash
export NETBPM_BASE_URL=http://localhost:5081
export NETBPM_CLIENT_KEY='nbc.cl_xxxxxxxx.yyyyyyyy'   # 控制台「接入管理」签发，整串照贴
export WORKER_TOPICS=approveComplete,refuseComplete
export PORT=3007

dotnet run --project samples/NetBpm.ServerTaskWorker
```

不想每次 export，可以用用户机密（项目已带 `UserSecretsId`）：

```bash
dotnet user-secrets set "Worker:ClientKey" "nbc.cl_xxxxxxxx.yyyyyyyy" --project samples/NetBpm.ServerTaskWorker
dotnet run --project samples/NetBpm.ServerTaskWorker
```

> 🔑 **密钥只放用户机密，不要写回 `appsettings.json`**：
> `appsettings.json` 是**被 git 跟踪**的，仓库又推到了 GitHub / Gitee 的公开远端 ——
> 写进去等于把可用的客户端密钥公开出去（别人拿它就能以你的名义领任务、回执）。
> `appsettings.json` 里的 `Worker:ClientKey` 因此留空，真实密钥只存在于
> 用户机密（`%APPDATA%/Microsoft/UserSecrets/`，不进仓库）或环境变量。
>
> 密钥一旦进过 git 历史，**轮换才是正解**（仓库里删掉不等于失效）：
> 控制台「接入管理 → 客户端 → 轮换密钥」签发新密钥 → 换到用户机密 → 旧密钥按宽限期自动作废。

PowerShell：

```powershell
$env:NETBPM_BASE_URL='http://localhost:5081'
$env:NETBPM_CLIENT_KEY='nbc.cl_xxxxxxxx.yyyyyyyy'
dotnet run --project samples/NetBpm.ServerTaskWorker
```

启动日志：

```
2026-11-03T07:55:19.850Z info: Program[0] 业务接口已监听 http://localhost:3007/api/serverTask/{approveComplete|refuseComplete}
2026-11-03T07:55:19.890Z info: ...[0] [worker] 启动 workerId=stw-host-1304-8579aa topics=approveComplete,refuseComplete 引擎=http://localhost:5081 租户=default
```

## 配置

优先级：**原 Node 版环境变量名 > 标准 `Worker__xxx` 环境变量 > appsettings.json**。
保留同名环境变量是为了换语言重写时运维脚本不用改。

| 环境变量（兼容名） | 配置键 | 默认 | 说明 |
|---|---|---|---|
| `NETBPM_BASE_URL` | `Worker:NetBpmBaseUrl` | `http://localhost:5081` | 控制台与引擎端点同端口 |
| `NETBPM_CLIENT_KEY` | `Worker:ClientKey` | 空（不领任务） | `nbc.{clientId}.{secret}` 原文；**没配时降级成「只有业务接口」** |
| `NETBPM_TENANT_ID` | `Worker:TenantId` | `default` | 仅用于日志/健康检查展示；**领取范围由客户端绑定决定** |
| `WORKER_TOPICS` | `Worker:Topics` | `approveComplete,refuseComplete` | 与 BPMN 的 topic 严格对应，大小写敏感 |
| `WORKER_ID` | `Worker:WorkerId` | `stw-{host}-{pid}-{rand}` | **每进程一个**，别写死 |
| `WORKER_MAX_TASKS` | `Worker:MaxTasks` | `5` | 单次领取条数（服务端上限 100） |
| `WORKER_LOCK_MS` | `Worker:LockDurationMs` | `120000` | 必须显著大于单条处理超时 |
| `WORKER_WAIT_SECONDS` | `Worker:WaitSeconds` | `20` | 没任务时等多久（上限 30） |
| `WORKER_TASK_TIMEOUT_MS` | `Worker:TaskTimeoutMs` | `30000` | 单条业务超时（超时按可重试失败） |
| `PORT` | `Worker:ListenUrl` | `3007` | 换算成 `http://localhost:{port}`；容器里可用 `LISTEN_URL=http://0.0.0.0:3007` |
| `RUN_WORKER` | `Worker:RunWorker` | `true` | `false` 时只提供业务接口 |
| `LEAVE_BILL_FILE` | `Worker:DataFile` | `data/leave-bills.json` | 单据落盘位置（样例替身） |

启动自检分两种，别混在一起：

- **配错了**（密钥不是 `nbc.` 开头、订阅主题为空）→ 进程直接退出并说明错在哪。
  带病启动只会用 401 或空转刷日志，早退出更好查。
- **还没配**（密钥为空）→ 进程照常启动，只提供业务接口，worker 不启动，控制台打降级提示。
  第一次跑、本地只想验业务逻辑时不需要先去签客户端。

## 接口

| 方法 | 路径 | 用途 |
|---|---|---|
| POST | `/api/serverTask/approveComplete` | 业务处理：审批通过（worker 与外部系统共用） |
| POST | `/api/serverTask/refuseComplete` | 业务处理：拒绝结束 |
| GET | `/healthz` | 存活、`status`（`ok`/`degraded`）、`clientKeyConfigured`、订阅主题、`lastPollAt` |

```bash
# 手工验证业务逻辑（不需要先跑一条流程实例）
curl -X POST http://127.0.0.1:3007/api/serverTask/approveComplete \
  -H 'Content-Type: application/json' \
  -d '{"processInstanceId":"15155","payload":{"applyUser":"1001","days":3,"result":1}}'
```

```json
{"ok":true,"variables":{"externalResult":"APPROVED","externalRef":"LEAVE-15155",
 "externalDuplicate":false,"externalStatusConflict":false,"externalHandledAt":"2026-11-03T07:53:41.317Z"}}
```

重复调用同一 `processInstanceId`：`externalDuplicate=true`（状态已经是目标值，不重复落账）。
先通过再拒绝：`externalStatusConflict=true`，**原状态不被覆盖**。

`lastPollAt` 是 `/healthz` 里最该看的字段：进程活着但 fetch 一直失败时，
`status` 照样是 `ok`，只有它停在原地。而 `status=degraded` + `clientKeyConfigured=false`
表示 worker **根本没启动**（没配密钥），这两种「不在干活」要分开看。

## 三条不能改的规则

1. 回执必须用**领取时**的 `workerId`（锁按 id 归属，换了 id 一律 409）；
2. 业务成功但回执失败时**不要**补发 `failure`（业务已落库，补 failure 会记成失败并扣重试；
   正确做法是让锁过期、被重新领取，靠业务幂等去重）；
3. 处理失败走 `failure`（扣重试、流程原地不动），不走 `unlock`（等于「我没看见过」，把失败原因抹掉）。
   不可恢复的业务错误用 `retries: 0` 直接进死信，别白耗三次额度。

## 接真实系统时要改的地方

1. `ILeaveBillStore` 换成你们的仓储（`JsonLeaveBillStore` 只是能跑起来的替身）。
   幂等 SQL 只有一句，影响行数为 0 就再查一次现状决定怎么回执：

   ```sql
   update leave_bill set status = 'APPROVED', handled_at = now()
    where process_instance_id = ? and status = 'PENDING'
   ```

2. `LeaveCompletionHandlerBase` 里的业务字段映射（`applyUser` / `days` / `reason` / `comment`）
   换成真实表字段；两个处理器只差 `ExpectedResult` / `TargetStatus` 三个常量。
3. 多副本部署：每个进程 `WorkerId` 不同（默认已带 pid 与随机串），锁竞争由数据库仲裁。

## 实测记录（本机 2026-11-03）

| 动作 | 结果 |
|---|---|
| 发起 `leave_process`（`days=3`）→ 提交 → 组长通过 | 外部任务 `15244` `topic=approveComplete`，`PENDING` → `COMPLETED`，worker 耗时 32ms |
| 发起 `leave_process`（`days=8`）→ 提交 → 拒绝 | 外部任务 `15267` `topic=refuseComplete`，`PENDING` → `COMPLETED` |
| 落账文件 | `15227=APPROVED`、`15251=REJECTED`，`Via` 均为 `netbpm-external-task` |
| 手工重复通过 / 通过后拒绝 | `externalDuplicate=true` / `externalStatusConflict=true`，状态未变 |
| `result` 与节点语义不符 | `422`，且 worker 会以 `retries: 0` 进死信 |

控制台侧的 4 步配置（注册客户端、租户填 `default`、只给 `workflow:external:task`、观察外部任务页）
见 [`docs/leave-worker-3007-integration.md`](../../docs/leave-worker-3007-integration.md)。
