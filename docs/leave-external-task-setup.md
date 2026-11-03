# 请假流程 + 外部任务 approveComplete 配置手册

面向要在 NetBpm 管理控制台（`http://localhost:5081`）上落地一条"按请假天数分级审批、审批通过后调用外部系统"的流程的同学。
本文的四件事与提问一一对应：**画流程 → 部署 → 配外部任务 → 在外部系统写处理代码**。

> 基线：当前 `main`（0.1.3）。命令以控制台宿主 `http://localhost:5081` 为例；独立部署时换成 `NetBpm.Platform.Service` 的地址，端点与 scope 口径完全一致。
> 配套流程文件：`web/designer/examples/leave-days-approval.bpmn20.xml`（`processKey = leave_days_approval`）。
> **两个 topic 的版本请看 `docs/leave-worker-3007-integration.md`**（`leave_process` v4：通过 `approveComplete` / 拒绝 `refuseComplete`，含可直接运行的 worker 样例与实测记录）。

---

## 0. 先对齐一件事：外部任务是"拉"，不是"推"

这是整件事里最容易踩错的地方，先把口径定下来，后面四步才不会白做。

| | 你以为的 | NetBpm 的实际行为 |
|---|---|---|
| BPMN 里写什么 | `approveComplete` + 回调地址 `http://10.25.10.20:3007/api/serverTask/…` | **只写** `activiti:topic="approveComplete"`，**没有任何地方写地址** |
| 谁发起调用 | 引擎执行到节点时 POST 到 3007 | 引擎只往 `nb_external_task` 插一行 `PENDING`，然后**原地不动** |
| 谁干活 | 3007 的接口被动接收 | 3007 里的 **worker 主动来 5081 领**（fetch），干完回执（complete） |
| 地址放哪 | BPMN | worker 的配置里（`Topics` 决定领哪个 topic，业务地址在 3007 自己那侧） |

所以"`approveComplete` 调用 `http://10.25.10.20:3007/api/serverTask/…`"这句话的准确形态是：

```
5081 引擎 ──登记外部任务(topic=approveComplete)──▶ nb_external_task(PENDING)
                                                      ▲
      3007 worker ──① POST /api/runtime/external-tasks/fetch {topics:["approveComplete"]}──┘ 领走并加锁
      3007 worker ──② POST http://10.25.10.20:3007/api/serverTask/approveComplete ──▶ 自己的业务处理
      3007 worker ──③ POST /api/runtime/external-tasks/{id}/complete ──▶ 引擎写回变量、推进流程
```

三条纪律：

1. **topic 是逻辑名**，与 worker 的订阅名逐字对应；它同时可以当作 3007 那侧的路由名（`approveComplete` → `/api/serverTask/approveComplete`）。
2. **网络方向是单向的**：3007 → 5081 出站可达即可，NetBpm 不需要能访问 3007。
3. 如果你的诉求确实是"引擎同步 POST 到 3007"，那要用**委托（delegateExpression）+ HttpClient**，代价见 §3.5。

---

## 1. 第一步：创建请假流程（按天数分级审批）

### 1.1 目标拓扑

```
                    ┌─ days ≤ 3 ──▶ 组长审批 ─┐
发起 → 提交请假申请 → 天数网关 ─ days ≤ 7 ──▶ 经理审批 ─┼─ 通过 ─▶ 审批通过回写(外部任务 approveComplete) ─▶ 结束
                    └─ days > 7 ──▶ 总监审批 ─┘
                              └─ 缺失/非法 ─▶ 申请人修改 ─▶ 回「提交请假申请」

每个审批节点后各有一个结果网关：
  通过(result==1) → 审批通过回写 → 结束
  拒绝(result==2) → 拒绝结束
  其它 / 未选     → 申请人修改（默认流兜底，兜底方向永远选"退回"而不是"通过"）
```

### 1.2 变量契约（BPMN 条件里的名字就是流程变量名）

| 变量 | 类型 | 谁写 | 说明 |
|---|---|---|---|
| `applyUser` | string | 发起（`activiti:initiator="applyUser"`） | 申请人，`assignee="${applyUser}"` 用它 |
| `days` | long | 发起 `param.days` 镜像 / 申请人修改 | 天数网关的判定依据 |
| `reason` | string | 发起 `param.reason` | 事由 |
| `result` | string | 审批提交（`formProperty approvalResult` → `variable="result"`） | `1` 通过 / `2` 拒绝 / `3` 驳回修改 |
| `comment` | string | 审批提交 | 审批说明，会进外部任务的 payload |

⚠️ `businessId` / `applyUser` / `result` / `comment` 是平台保留名，**不能**通过 `parameters` 传入（400 `parameter.conflict_with_reserved`）。
业务侧的关联键请用**流程实例 id**：发起成功后把 `instanceId` 写回业务表，worker 用 `payload.processInstanceId` 反查。
（外部任务响应里的 `businessKey` 目前恒为 `null`，别依赖它做关联。）

> 说明：`days` 从 `parameters` 进来时是字符串 `"5"`，EL 会做数值强制（`${days > 3}` 成立）。这是引擎的既定语义，不需要额外转换。

### 1.3 两条必须遵守的建模规则

1. **排他网关要么有默认流，要么至少有一条无条件出流**（规则 `exclusive-gateway-has-selectable-flow`）。
   默认流上**不要写条件**（`default-flow` 规则会警告"不参与判定"）。
2. **serviceTask 的四种实现方式互斥**：`class` / `delegateExpression` / `expression` / `topic` 只能填一个，
   同时填部署直接 400（`required-attributes`：服务任务只能指定一种实现方式）。
   外部任务**只写 `topic`**，不要加 `activiti:type="external"`（该属性不在支持范围，只会多一条告警）。

### 1.4 现成文件（推荐直接导入）

已生成好一份零阻断错误的流程文件：`web/designer/examples/leave-days-approval.bpmn20.xml`。

关键片段（审批通过那一段）：

```xml
<bpmn:serviceTask id="svc_approve_complete" name="审批通过回写" activiti:topic="approveComplete">
  <bpmn:incoming>flow_leader_pass</bpmn:incoming>
  <bpmn:incoming>flow_manager_pass</bpmn:incoming>
  <bpmn:incoming>flow_director_pass</bpmn:incoming>
  <bpmn:outgoing>flow_approve_end</bpmn:outgoing>
</bpmn:serviceTask>
```

```xml
<bpmn:sequenceFlow id="flow_days_le7" name="4~7 天" sourceRef="gw_days" targetRef="task_approve_manager">
  <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression"><![CDATA[${days > 3 && days <= 7}]]></bpmn:conditionExpression>
</bpmn:sequenceFlow>
```

```xml
<bpmn:exclusiveGateway id="gw_leader" name="组长审批结果" default="flow_leader_modify">
```

---

## 2. 第二步：在控制台部署

### 2.1 导入模型（不发布）

1. 打开 `http://localhost:5081/models`。
2. 右上角 **「导入模型」** → 选 `leave-days-approval.bpmn20.xml`。
   导入只写模型库（`bpmn_model`），`processKey` 由 XML 里的 `<bpmn:process id="…">` 推导，名称取 `name` 属性。
3. 列表出现一行：**请假流程（按天数分级审批） / `leave_days_approval` / 未发布**。

> 也可以点「新建模型」在嵌入设计器里从零画：调色板只有「开始事件 / 用户任务 / 服务任务 / 单一网关 / 并行网关 / 包含网关 / 结束事件 / 连线」。
> 手绘时按 §1.1 的拓扑连线，再逐项填 §2.2 的属性。

### 2.2 在设计器里核对属性（选中节点 → 右侧属性面板）

| 节点 | 面板分组 | 字段 | 值 |
|---|---|---|---|
| 提交请假申请 `task_apply` | 用户任务 | 办理人（审批人） | `${applyUser}` |
| 同上 | 表单属性 | `days`（long，必填）/ `reason`（string） | 变量名分别 `days`、`reason` |
| 组长审批 `task_approve_leader` | 用户任务 | 候选组 | `leaders` |
| 经理审批 `task_approve_manager` | 用户任务 | 候选组 | `managers` |
| 总监审批 `task_approve_director` | 用户任务 | 候选组 | `directors` |
| 三个审批节点 | 表单属性 | `approvalResult`（enum，必填）+ `comment`（string） | enum 子项 id/name：`1/通过`、`2/拒绝`、`3/驳回修改`；`variable="result"` |
| 天数网关 `gw_days` | 网关 | 默认流 | `flow_days_retry` |
| 三条出流 | 顺序流 | 条件表达式 | `${days <= 3}` / `${days > 3 && days <= 7}` / `${days > 7}` |
| 三个结果网关 | 网关 | 默认流 | 各自的 `flow_*_modify` |
| 审批通过回写 `svc_approve_complete` | **服务任务** | **主题（外部任务）** | `approveComplete` |
| 同上 | 服务任务 | 类名 / 表达式 / 代理的表达式 | **全部留空**（互斥，填了当场标红） |

保存前先看设计器底部的**校验清单**：阻断级问题（红色）会拦住部署，提示级（黄色）不拦但会在部署响应里以 `warnings` 回来。

### 2.3 保存 → 部署（两步，不要混）

1. 设计器工具栏 **「保存」** → 写 `bpmn_model` 并追加一个快照（乐观并发用 `If-Match`，被他人改过返回 409）。
2. 返回列表，在该行点 **「部署」** → 弹窗确认。
   控制台会先 `POST /api/admin/models/{id}/validate` 预检，再渲染流程图 SVG，最后 `POST /api/admin/models/{id}/publish`。
3. 成功提示：**「已部署：leave_days_approval v1」**。若带提示会显示「（N 条提示）」——
   `warnings` 非空**不代表失败**，定义已经落库，但实例跑到相关节点可能失败（见 §6 第一条）。

⚠️ 两个常见误操作：

- **改了 XML 没保存就点部署** → 发布的是模型库里的旧内容。
- 重复点部署且内容没变 → 后端 `skipIfUnchanged` 跳过，**不会**把版本号刷上去。

### 2.4 验证部署结果

> 下面命令里的 `$TOKEN` 是控制台管理令牌，获取方式见 §5 第 0 步。

```bash
# 1) 部署列表（控制台「部署管理」页同源）
curl -s http://localhost:5081/api/designer/deployments -H "Authorization: Bearer $TOKEN"

# 2) 定义版本
curl -s "http://localhost:5081/api/designer/definitions?key=leave_days_approval" -H "Authorization: Bearer $TOKEN"

# 3) 平台面目录（客户端凭据 / JWT 都能查）
curl -s http://localhost:5081/workflow/v1/processes -H "Authorization: Bearer $TOKEN"
```

`warnings` 里如果出现 `rule = "delegate-resolution"`，说明某个 serviceTask 写了 `class` / `delegateExpression` 但宿主解析不到——
本流程用的是 `topic`，**不该**出现这条；出现就说明实现字段没清空。

### 2.5 关于版本

- 版本号由引擎按 `(KEY_, TENANT_ID_)` 递增，`max(VERSION_)+1`，与模型自己的 `revision` 无关。
- 已在跑的实例**仍走部署时的那个版本**；改流程后新发起的实例才用新版本。
- 生产上想"新老并存"最稳的做法是**换一个 processKey**（例如 `leave_days_approval_v2`），而不是原地改。

---

## 3. 第三步：配置"审批通过"外部任务（approveComplete）

配置分散在三处，**缺一处流程就静静卡住**（不会报错，只有巡检告警）。

### 3.1 BPMN 侧（唯一判定依据）

```xml
<bpmn:serviceTask id="svc_approve_complete" name="审批通过回写" activiti:topic="approveComplete" />
```

- 判定只看 `activiti:topic` 有没有值；`ServiceTaskDelegateGuard` 对它有值的情况**明确跳过**。
- 因此 **部署成功 ≠ 能跑通**：部署期不校验"这个 topic 有没有 worker 订阅"，只有 worker 真接到任务才验证得了。

### 3.2 平台侧：存储与配置项

| 宿主 | 实现 | 位置 |
|---|---|---|
| 控制台 / Platform.Service（数据库宿主） | `DapperExternalTaskBroker` | 表 `nb_external_task` |
| 内存宿主 | `InMemoryExternalTaskBroker` | 进程内，重启即丢 |

控制台宿主用数据库，**不需要额外配置就能用**。可调项（`NetBpm:ExternalTasks`）：

```jsonc
"NetBpm": {
  "ExternalTasks": {
    "LockSeconds": 300,          // fetch 未带 lockDurationMs 时的默认锁时长
    "Retries": 3,                // ⚠️ 只影响死信重投的默认次数
    "Watchdog": {
      "Enabled": true,
      "NoWorkerThresholdSeconds": 300,   // 登记后多久没人领算异常
      "AlertCooldownSeconds": 900,
      "AlertOnDeadLetter": true
    }
  }
}
```

两个容易误判的点：

- **登记时的初始剩余次数固定为 3**，改 `Retries` 动不了它。
- **巡检只告警不动数据**，告警码 `external_task.no_worker` / `external_task.dead_letter`。控制台宿主已自动挂载
  （`AddNetBpmHost` → `AddNetBpmExternalTaskWatchdog`），自写宿主别漏。

### 3.3 凭据：注册一个只干活的客户端

开发配置 `NetBpm:Security:RequireCredential = false` 时可以不带凭据，但**生产必须走客户端凭据**。
控制台 **「接入管理」→「注册接入系统」**（或 `POST /admin/v1/clients`）：

| 字段 | 值 |
|---|---|
| 接入系统名称 | `leave-approve-worker` |
| 所属租户 | `default` |
| 授权 scope | `workflow:external:task` |
| 可用流程 key | `leave_days_approval` |
| 配额（次/分） | 600 |
| 密钥有效期（天） | 90 |

保存后弹窗里的 `clientSecret` **只出现一次**，立刻记下来。worker 侧用请求头：

```
X-Api-Key: nbc.{clientId}.{clientSecret}
```

注意：

- `nbc.` 前缀本身就是承诺，格式坏了直接 401，**绝不降级成匿名**。
- **不要**给 worker `workflow:admin:instance`：死信重投是管理干预，让 worker 自己能复活死信等于把退避机制架空。
- 查/解锁/回执需要 `workflow:external:task`；**重投（retry）额外需要 `workflow:admin:instance`**。

### 3.4 观察与干预

侧栏 **「外部任务」**（`/external-tasks`，接口同源 `GET /api/runtime/external-tasks`）：

- 按 topic / 状态 / 流程实例筛选，看 `payload`（领取那一刻的变量快照）、`lockExpiresAt`、剩余 `retries`；
- 死信**一键重投**（`POST /api/runtime/external-tasks/{id}/retry`，需管理 scope）；
- 锁定任务可**释放锁**（`POST /…/unlock`，用当前持锁者的 `workerId`；不符返回 **400**）。

⚠️ 页面**故意不提供领取与回执**——那会抢走 worker 的锁。想手工验一次 `fetch`，请先把 3007 的 worker 停掉。
（当前 5081 若跑的是旧构建，侧栏可能没有这一项，直接访问 `/external-tasks` 即可。）

状态机（排错时对着看）：

```
PENDING ──fetch──▶ LOCKED ──complete──▶ COMPLETED
   ▲                 │
   │                 └─failure──▶ PENDING(退避 5s→10s→20s…封顶 5min) ──retries≤0──▶ DEAD_LETTER
   │                 └──锁到期──▶ 可被任意 worker 重领
   └──────────────── retry（仅死信，管理端点）────────────────────────┘
```

### 3.5 如果你真心想要"引擎直接 POST 到 3007"

那就不是外部任务，而是**委托**：

```xml
<bpmn:serviceTask id="svc_approve_complete" name="审批通过回写"
                  activiti:delegateExpression="leave.ApprovePush" activiti:asyncBefore="true" />
```

```csharp
[ServiceTaskName("leave.ApprovePush")]
public sealed class ApprovePushDelegate(HttpClient http, ILogger<ApprovePushDelegate> logger) : IServiceTaskDelegate
{
    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        var variables = await execution.GetVariablesAsync(ct);
        using var response = await http.PostAsJsonAsync(
            "http://10.25.10.20:3007/api/serverTask/approveComplete",
            new { taskId = execution.Id, processInstanceId = execution.ProcessInstanceId, payload = variables }, ct);

        if (!response.IsSuccessStatusCode)
        {
            // 抛异常 = 本次推进整体回滚；配了 asyncBefore 才会按退避重试
            throw new InvalidOperationException($"回写业务系统失败：HTTP {(int)response.StatusCode}");
        }
    }
}
```

代价（这是为什么默认推荐外部任务）：

| | 外部任务（topic） | 委托 + HttpClient |
|---|---|---|
| 代码放哪 | 3007，自己的仓 | **NetBpm 宿主进程内**，要改宿主、重新发版、重启 |
| 事务 | 独立 | 在流程命令的事务里（同步路径会拖长事务、放大锁竞争） |
| 重试 | 服务端退避 + 死信 + 可重投 | 只有 `asyncBefore` 的作业退避，没有"重投"这个动作 |
| 谁掌握节奏 | 3007（并发、限流自己控） | 引擎（3007 挂了会把流程一起拖住） |

结论：**跨系统一律用 topic**；只有"宿主机房内、毫秒级、无外部依赖"的回写才考虑委托。

---

## 4. 第四步：在 `http://10.25.10.20:3007/api/serverTask/…` 写处理代码

### 4.1 3007 侧要落的两块东西

```
3007 服务
├── POST /api/serverTask/{taskName}     ← 业务入口（approveComplete 就是其中一个 taskName）
└── ExternalTaskWorker（后台服务）       ← 向 5081 fetch → 调上面的入口 → 回执 complete/failure
```

worker 与业务入口**放在同一个进程**也没问题（本文就是），一次 loopback 调用换来"单一业务入口"：
人工补偿、联调、批量重放都打同一个 URL。

### 4.2 目录与文件

```
LeaveBridge/
├── Program.cs
├── Options.cs
├── ExternalTasks/ExternalTaskWorker.cs
├── ServerTasks/IServerTask.cs
├── ServerTasks/ServerTaskDispatcher.cs
├── ServerTasks/ApproveCompleteServerTask.cs
├── Data/LeaveBillRepository.cs
└── appsettings.json
```

### 4.3 `Options.cs`

```csharp
namespace LeaveBridge;

public sealed class BridgeOptions
{
    // ── NetBpm 侧 ───────────────────────────────────────────────
    public string BaseUrl { get; set; } = "http://127.0.0.1:5081";
    public string ApiKey { get; set; } = "";                       // nbc.{clientId}.{secret}
    public string TenantId { get; set; } = "";                     // 走 nbc.* 时租户已钉死，可留空
    public string WorkerId { get; set; } = Environment.MachineName; // 锁持有者：稳定且各副本不同
    public string[] Topics { get; set; } = ["approveComplete"];
    public int MaxTasks { get; set; } = 5;
    public int MaxConcurrency { get; set; } = 4;
    public int LockDurationMs { get; set; } = 120_000;             // 必须覆盖"处理耗时 + 一次网络抖动"
    public int WaitSeconds { get; set; } = 10;                     // 服务端短轮询，上限 30

    // ── 本机业务入口 ─────────────────────────────────────────────
    public string SelfBaseUrl { get; set; } = "http://127.0.0.1:3007";
    public string InternalToken { get; set; } = "";                // 内部调用口令（可选）

    public string ConnectionString { get; set; } = "";
}
```

### 4.4 `ServerTasks/IServerTask.cs`

```csharp
using System.Text.Json;

namespace LeaveBridge.ServerTasks;

/// <summary>一个 serverTask 的业务实现。<see cref="Name"/> 与 BPMN 的 activiti:topic 逐字一致。</summary>
public interface IServerTask
{
    string Name { get; }

    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(ServerTaskContext context, CancellationToken ct);
}

/// <summary>外部任务交给业务层的入参（字段名与 /api/serverTask 的请求体一一对应）。</summary>
public sealed record ServerTaskContext(
    string TaskId,
    string? ProcessInstanceId,
    string Topic,
    string? ActivityId,
    int Retries,
    IReadOnlyDictionary<string, JsonElement> Payload)
{
    public string? ReadString(string name) =>
        Payload.TryGetValue(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.ToString(),
            }
            : null;

    public int? ReadInt(string name) =>
        int.TryParse(ReadString(name), out var parsed) ? parsed : null;
}

/// <summary>业务口径的失败：worker 会把它转成 failure 回执，服务端扣重试并退避。</summary>
public sealed class ServerTaskRejectedException(string message, string? details = null)
    : Exception(message)
{
    public string? Details { get; } = details;
}
```

### 4.5 `ServerTasks/ApproveCompleteServerTask.cs`（核心业务）

```csharp
using LeaveBridge.Data;

namespace LeaveBridge.ServerTasks;

/// <summary>
/// 审批通过回写：把流程的「已通过」结论落到请假单上。
/// topic = approveComplete ↔ /api/serverTask/approveComplete ↔ 本类。
/// </summary>
public sealed class ApproveCompleteServerTask(
    LeaveBillRepository bills,
    ILogger<ApproveCompleteServerTask> logger) : IServerTask
{
    public string Name => "approveComplete";

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(ServerTaskContext context, CancellationToken ct)
    {
        // 1) 定位业务单据：用流程实例 id（payload 里没有 businessId，businessKey 目前不回填）
        if (string.IsNullOrWhiteSpace(context.ProcessInstanceId))
        {
            throw new ServerTaskRejectedException("payload 缺少 processInstanceId，无法定位请假单");
        }

        // 2) 结论必须真的是「通过」才允许推进（1=通过，见 BPMN 的 approvalResult）
        var result = context.ReadString("result");
        if (!string.Equals(result, "1", StringComparison.Ordinal))
        {
            throw new ServerTaskRejectedException(
                $"审批结论为 '{result ?? "空"}'，不是「通过」，拒绝执行回写",
                details: "检查 BPMN 结果网关的条件，或审批提交时的 optionId");
        }

        var days = context.ReadInt("days");
        var comment = context.ReadString("comment");

        logger.LogInformation(
            "回写请假单：instance={InstanceId} task={TaskId} days={Days} operator={User}",
            context.ProcessInstanceId, context.TaskId, days, context.ReadString("applyUser"));

        // 3) 业务更新（幂等：UPDATE ... WHERE 天然可重复执行）
        var updated = await bills.MarkApprovedAsync(context.ProcessInstanceId, days, comment, ct);

        if (!updated)
        {
            // 找不到单据就报失败，别静默推进——流程走完了业务没变，是最难查的那类问题
            throw new ServerTaskRejectedException(
                $"按流程实例 {context.ProcessInstanceId} 找不到请假单（或状态已不是待审批）",
                details: "确认发起时是否把 instanceId 写回了 leave_bill.workflow_instance_id");
        }

        // 4) 写回流程变量：让审批时间线/后续节点能看到回写结果
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["leaveStatus"] = "APPROVED",
            ["approveSyncedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["approveSyncTaskId"] = context.TaskId,
        };
    }
}
```

### 4.6 `Data/LeaveBillRepository.cs`

```csharp
using Dapper;
using MySqlConnector;

namespace LeaveBridge.Data;

public sealed class LeaveBillRepository(IConfiguration configuration)
{
    private readonly string _connectionString =
        configuration["Bridge:ConnectionString"] ?? throw new InvalidOperationException("缺少 Bridge:ConnectionString");

    /// <summary>把请假单置为已通过。可重复执行（已通过的再执行返回 true）。</summary>
    public async Task<bool> MarkApprovedAsync(string instanceId, int? days, string? comment, CancellationToken ct)
    {
        const string update = """
            UPDATE leave_bill
               SET status          = 'APPROVED',
                   approve_days    = COALESCE(@days, approve_days),
                   approve_comment = COALESCE(@comment, approve_comment),
                   approve_time    = COALESCE(approve_time, UTC_TIMESTAMP()),
                   updated_at      = UTC_TIMESTAMP()
             WHERE workflow_instance_id = @instanceId
               AND status <> 'APPROVED';
            """;

        const string already = """
            SELECT COUNT(1) FROM leave_bill
             WHERE workflow_instance_id = @instanceId AND status = 'APPROVED';
            """;

        await using var connection = new MySqlConnection(_connectionString);

        var affected = await connection.ExecuteAsync(
            new CommandDefinition(update, new { instanceId, days, comment }, cancellationToken: ct));

        if (affected > 0)
        {
            return true;
        }

        // 0 行有两种可能：已经回写过（幂等重放），或压根没有这张单
        var exists = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(already, new { instanceId }, cancellationToken: ct));

        return exists > 0;
    }
}
```

配套的最小表结构（换成你自己的表名/字段即可，`workflow_instance_id` 必须有唯一索引）：

```sql
CREATE TABLE IF NOT EXISTS leave_bill (
  id                    BIGINT       NOT NULL AUTO_INCREMENT PRIMARY KEY,
  workflow_instance_id  VARCHAR(64)  NULL,
  apply_user            VARCHAR(64)  NOT NULL,
  days                  INT          NOT NULL,
  reason                VARCHAR(500) NULL,
  status                VARCHAR(32)  NOT NULL DEFAULT 'PENDING',  -- PENDING / APPROVED / REJECTED
  approve_days          INT          NULL,
  approve_comment       VARCHAR(500) NULL,
  approve_time          DATETIME     NULL,
  created_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at            DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
  UNIQUE KEY uk_leave_instance (workflow_instance_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

### 4.7 `ServerTasks/ServerTaskDispatcher.cs`

```csharp
namespace LeaveBridge.ServerTasks;

public sealed class ServerTaskDispatcher(IEnumerable<IServerTask> tasks)
{
    private readonly Dictionary<string, IServerTask> _tasks =
        tasks.ToDictionary(task => task.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _tasks.Keys;

    public bool TryResolve(string name, out IServerTask task) => _tasks.TryGetValue(name, out task!);
}
```

### 4.8 `ExternalTasks/ExternalTaskWorker.cs`（拉取循环）

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeaveBridge.ServerTasks;
using Microsoft.Extensions.Options;

namespace LeaveBridge.ExternalTasks;

/// <summary>领 → 做 → 回执。这个进程里不做重试：退避与死信全在服务端。</summary>
public sealed class ExternalTaskWorker(
    IHttpClientFactory factory,
    IOptions<BridgeOptions> options,
    ILogger<ExternalTaskWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BridgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "外部任务 worker 启动：workerId={WorkerId} topics={Topics} netbpm={BaseUrl}",
            _options.WorkerId, string.Join(",", _options.Topics), _options.BaseUrl);

        using var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tasks = await FetchAsync(stoppingToken);

                if (tasks.Count == 0)
                {
                    continue;   // fetch 是服务端短轮询，不用自己 delay
                }

                await Task.WhenAll(tasks.Select(task => RunOneAsync(task, gate, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "拉取外部任务失败，3s 后重试：{Message}", exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<IReadOnlyList<ExternalTaskDto>> FetchAsync(CancellationToken ct)
    {
        var client = factory.CreateClient("netbpm");

        using var response = await client.PostAsJsonAsync(
            "api/runtime/external-tasks/fetch",
            new
            {
                workerId = _options.WorkerId,
                topics = _options.Topics,
                maxTasks = _options.MaxTasks,
                lockDurationMs = _options.LockDurationMs,
                waitSeconds = _options.WaitSeconds,
            },
            Json, ct);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<ExternalTaskDto>>(Json, ct) ?? [];
    }

    private async Task RunOneAsync(ExternalTaskDto task, SemaphoreSlim gate, CancellationToken stoppingToken)
    {
        await gate.WaitAsync(stoppingToken);

        try
        {
            await ProcessAsync(task, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机中：不回执，让锁自然过期
        }
        catch (Exception exception)
        {
            // 连回执都失败时只能放弃：锁会过期，任务会被重新领走
            logger.LogError(exception, "任务 {TaskId} 处理异常，等待服务端回收锁", task.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ProcessAsync(ExternalTaskDto task, CancellationToken ct)
    {
        logger.LogInformation("领到任务 {TaskId}（topic={Topic}，实例 {InstanceId}）",
            task.Id, task.Topic, task.ProcessInstanceId);

        // ① 调自己的业务入口：/api/serverTask/{topic} —— topic 即 taskName
        var self = factory.CreateClient("self");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/serverTask/{task.Topic}")
        {
            Content = JsonContent.Create(new
            {
                taskId = task.Id,
                processInstanceId = task.ProcessInstanceId,
                topic = task.Topic,
                activityId = task.ActivityId,
                retries = task.Retries,
                payload = task.Payload,
            }, options: Json),
        };

        if (!string.IsNullOrEmpty(_options.InternalToken))
        {
            request.Headers.Add("X-Internal-Token", _options.InternalToken);
        }

        using var response = await self.SendAsync(request, ct);

        var body = await response.Content.ReadFromJsonAsync<ServerTaskResponse>(Json, ct);

        if (!response.IsSuccessStatusCode || body is not { Ok: true })
        {
            var message = body?.Message ?? $"业务入口返回 HTTP {(int)response.StatusCode}";
            await ReportFailureAsync(task, message, body?.Details, ct);
            logger.LogWarning("任务 {TaskId} 业务失败：{Message}", task.Id, message);
            return;
        }

        await CompleteAsync(task, body.Variables, ct);
        logger.LogInformation("任务 {TaskId} 已完成，写回变量：{Keys}", task.Id, string.Join(",", body.Variables.Keys));
    }

    private async Task CompleteAsync(ExternalTaskDto task, IReadOnlyDictionary<string, object?> variables, CancellationToken ct)
    {
        var client = factory.CreateClient("netbpm");

        using var response = await client.PostAsJsonAsync(
            $"api/runtime/external-tasks/{Uri.EscapeDataString(task.Id)}/complete",
            new { workerId = _options.WorkerId, variables },
            Json, ct);

        // 409 = 锁已不属于我（过期被重领 / 已完成）→ 不能重试，交给服务端语义
        response.EnsureSuccessStatusCode();
    }

    private async Task ReportFailureAsync(ExternalTaskDto task, string message, string? details, CancellationToken ct)
    {
        var client = factory.CreateClient("netbpm");

        using var response = await client.PostAsJsonAsync(
            $"api/runtime/external-tasks/{Uri.EscapeDataString(task.Id)}/failure",
            new { workerId = _options.WorkerId, errorMessage = message, errorDetails = details },
            Json, ct);

        response.EnsureSuccessStatusCode();
    }
}

// ── 契约（只取用得上的字段）─────────────────────────────────────
public sealed class ExternalTaskDto
{
    public string Id { get; set; } = "";
    public string Topic { get; set; } = "";
    public string? ProcessInstanceId { get; set; }
    public string? ActivityId { get; set; }
    public int Retries { get; set; }
    public Dictionary<string, JsonElement> Payload { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ServerTaskResponse
{
    public bool Ok { get; set; }
    public Dictionary<string, object?> Variables { get; set; } = new(StringComparer.Ordinal);
    public string? Message { get; set; }
    public string? Details { get; set; }
}
```

> `failure` 里的 `retries` 语义是"**剩余**次数"：不传则当前值 −1，减到 ≤ 0 进死信。
> 该字段是可选的，本文不传，退避节奏交给服务端（5s → 10s → 20s … 封顶 5min）。

### 4.9 `Program.cs`

```csharp
using LeaveBridge;
using LeaveBridge.Data;
using LeaveBridge.ExternalTasks;
using LeaveBridge.ServerTasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection("Bridge"));
builder.Services.AddSingleton<LeaveBillRepository>();
builder.Services.AddSingleton<IServerTask, ApproveCompleteServerTask>();
builder.Services.AddSingleton<ServerTaskDispatcher>();
builder.Services.AddHostedService<ExternalTaskWorker>();

builder.Services.AddHttpClient("netbpm", (provider, http) =>
{
    var options = provider.GetRequiredService<IOptions<BridgeOptions>>().Value;

    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(30);

    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
    }

    if (!string.IsNullOrWhiteSpace(options.TenantId))
    {
        http.DefaultRequestHeaders.Add("X-Tenant-Id", options.TenantId);   // JWT 模式不读该头
    }
});

builder.Services.AddHttpClient("self", (provider, http) =>
{
    var options = provider.GetRequiredService<IOptions<BridgeOptions>>().Value;
    http.BaseAddress = new Uri(options.SelfBaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(20);
});

var app = builder.Build();

// 业务入口：/api/serverTask/{taskName}，taskName 与 activiti:topic 同名
app.MapPost("/api/serverTask/{taskName}", async (
    string taskName,
    ServerTaskRequest request,
    ServerTaskDispatcher dispatcher,
    IOptions<BridgeOptions> options,
    HttpRequest http,
    CancellationToken ct) =>
{
    var expected = options.Value.InternalToken;

    if (!string.IsNullOrEmpty(expected) && http.Headers["X-Internal-Token"] != expected)
    {
        return Results.Unauthorized();
    }

    if (!dispatcher.TryResolve(taskName, out var task))
    {
        return Results.NotFound(new { ok = false, message = $"未注册的 serverTask：{taskName}" });
    }

    try
    {
        var variables = await task.ExecuteAsync(
            new ServerTaskContext(
                request.TaskId,
                request.ProcessInstanceId,
                request.Topic ?? taskName,
                request.ActivityId,
                request.Retries,
                request.Payload ?? new Dictionary<string, JsonElement>()),
            ct);

        return Results.Ok(new { ok = true, variables });
    }
    catch (ServerTaskRejectedException rejection)
    {
        // 422：业务拒绝，worker 会转成 failure 回执（扣重试、不推进流程）
        return Results.UnprocessableEntity(new
        {
            ok = false,
            message = rejection.Message,
            details = rejection.Details,
        });
    }
    catch (Exception exception)
    {
        return Results.Problem(title: "serverTask 执行异常", detail: exception.Message, statusCode: 500);
    }
});

app.MapGet("/api/serverTask", (ServerTaskDispatcher dispatcher) => dispatcher.Names);
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

app.Run();

/// <summary>/api/serverTask/{taskName} 的请求体。</summary>
public sealed record ServerTaskRequest(
    string TaskId,
    string? ProcessInstanceId = null,
    string? Topic = null,
    string? ActivityId = null,
    int Retries = 0,
    Dictionary<string, JsonElement>? Payload = null);
```

### 4.10 `appsettings.json`

```jsonc
{
  "Bridge": {
    "BaseUrl": "http://<NetBpm 主机>:5081",
    "ApiKey": "nbc.cl_xxxxxxxx.<clientSecret>",
    "WorkerId": "leave-worker-01",
    "Topics": [ "approveComplete" ],
    "MaxTasks": 5,
    "MaxConcurrency": 4,
    "LockDurationMs": 120000,
    "WaitSeconds": 10,
    "SelfBaseUrl": "http://127.0.0.1:3007",
    "InternalToken": "<随机串>",
    "ConnectionString": "Server=127.0.0.1;Port=3306;Database=leave;User ID=root;Password=***;Allow User Variables=true"
  }
}
```

容器里用环境变量覆盖时注意：**数组要用下标形式**，`Bridge__Topics=approveComplete` 不会变成数组。

```
Bridge__Topics__0=approveComplete
Bridge__WorkerId=leave-worker-01
```

### 4.11 五个必须守住的点

1. **workerId 稳定且各副本不同**：它是锁的归属键，换 id 等于放弃已领任务。
2. **锁时长一次给足**：REST **没有续租端点**（broker 层有 `ExtendLockAsync`，未暴露成 HTTP）。
   处理超过锁时长 → 被别人重领 → 重复执行。
3. **业务必须可重复执行**（at-least-once）：本文用 `UPDATE … WHERE status <> 'APPROVED'` 天然幂等；
   若业务是"扣款 / 发短信 / 开票"，请用 `taskId` 或业务单号做幂等键，并落一张去重表。
4. **业务失败只上报 failure，不在 worker 里重试**：worker 重试 3 次 × 服务端 3 次 = 9 次重复。
5. **网络异常既不回执也不本地重做**：让锁自然过期，由服务端回收后重发。

### 4.12 非 .NET 的最小实现（语言无关）

HTTP 契约就三个请求，Go / Java / Node 照抄即可：

```javascript
// 1) 领（带 10s 短轮询）
const fetched = await fetch(`${NETBPM}/api/runtime/external-tasks/fetch`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'X-Api-Key': API_KEY },
  body: JSON.stringify({ workerId: 'leave-worker-01', topics: ['approveComplete'], maxTasks: 5, waitSeconds: 10 })
}).then(r => r.json());

// 2) 做（调自家业务入口）
for (const task of fetched) {
  const result = await handleApproveComplete(task.payload);   // 你的业务代码
  // 3) 回执
  await fetch(`${NETBPM}/api/runtime/external-tasks/${task.id}/complete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': API_KEY },
    body: JSON.stringify({ workerId: 'leave-worker-01', variables: result })
  });
}
```

---

## 5. 端到端验收（按顺序做，每步都有可见结果）

```bash
# 0) 拿一个管理令牌（控制台账号，见 samples/process-authoring-guide.md §1.2）
TOKEN=$(curl -s -X POST http://localhost:5081/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"***"}' | jq -r .token)

# 1) 发起（days=5 → 交完「提交请假申请」后应进入「经理审批」）
curl -s -X POST http://localhost:5081/workflow/v1/instances \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"processKey":"leave_days_approval","parameters":{"param.days":"5","param.reason":"年假"}}'

# 2) 看当前待办（拿 taskId 与 optionId）
curl -s "http://localhost:5081/workflow/v1/instances/<instanceId>" \
  -H "Authorization: Bearer $TOKEN" | jq '.tasks[] | {taskId, activityName, options}'

# 3a) 「提交请假申请」是填表节点（没有审批选项）→ 走引擎面 complete 交变量
curl -s -X POST "http://localhost:5081/api/runtime/tasks/<taskId>/complete" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"variables":{"days":5,"reason":"年假"}}'

# 3b) 「经理审批」是审批节点 → 走平台面 submit 交结论（optionId=1 通过）
curl -s -X POST "http://localhost:5081/workflow/v1/tasks/<taskId>/submit" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"optionId":"1","comment":"同意"}'
```

> 两个口子别混：**有审批选项（`enum` formProperty）的节点用平台面 `submit`**——它顺带写 `result` / `comment`；
> **纯填表节点平台面推不动**（没有 `optionId` → 422 `decision.*`），要用引擎面 `complete` 直接交变量。
> 真实项目里这两步都由你们自己的 BFF 调（引擎面需要管理/服务凭据），控制台只做观察与干预。

期望结果：

| 检查点 | 期望 |
|---|---|
| 发起 `days=5` | 先停在 `task_apply`（提交请假申请）；完成该节点后进入 `task_approve_manager`（经理审批） |
| 审批通过 | 实例先停在 `svc_approve_complete`，`nb_external_task` 多一行 `PENDING` |
| 3007 worker 在跑 | 任务被 `LOCKED` → 调 `/api/serverTask/approveComplete` → `COMPLETED`，实例 `COMPLETED`，`leave_bill.status = APPROVED` |
| 控制台「外部任务」 | 该行状态 `COMPLETED`，`payload` 里有 `days` / `result` / `comment` / `applyUser` |
| 控制台「实例监控」 | 时间线出现 `svc_approve_complete`（serviceTask）与 `end_approved` |

再各跑一次 `days=2`（组长）、`days=10`（总监），验证分级是否正确。

---

## 6. 排错对照表

| 现象 | 原因 | 处置 |
|---|---|---|
| 部署 201 但 `warnings` 有 `delegate-resolution` | serviceTask 误填了 `class` / `delegateExpression` | 清空那三个字段，只留 `topic` |
| 部署 400 `required-attributes` | 只写了 `activiti:type="external"` 没写 `topic` | 补 `activiti:topic` |
| 发起 400 `parameter.conflict_with_reserved` | 传了 `businessId` / `result` / `comment` / `applyUser` | 换名（如 `bizNo`），或用 businessKey |
| 提交返回 422 `decision.*` | 该节点没有审批选项（纯填表节点，如「提交请假申请」） | 改用引擎面 `POST /api/runtime/tasks/{id}/complete` 交变量 |
| 流程停在审批节点、待办查不到 | 候选组没对上（`leaders`/`managers`/`directors` 在你们组织里不存在） | 用候选组映射或临时加 `candidateUsers` 自测 |
| 走到 serviceTask 后实例不动 | 正常现象：等 worker 领取 | 看「外部任务」页有没有 `PENDING` 行 |
| 没有 `nb_external_task` 行 | BPMN 写的是 `class`；或宿主没注册 broker；或实例跑在旧定义上 | 改 `topic`、重新发布、对**新**实例验证 |
| `fetch` 一直返回 `[]` | topic 拼错 / 订阅串带空格 / 领的是旧版本 | `GET /api/runtime/external-tasks?topic=approveComplete&status=PENDING` 核对字面值 |
| 任务 `LOCKED` 但没人处理 | 被别的 worker 领走 / 自己换了 workerId | 看 `workerId` 与 `lockExpiresAt`，等锁过期或持锁者 unlock |
| complete 409「锁不属于…」 | 锁过期被重领 / workerId 不符 / 已完成 | 重新 fetch 再回执，别重试同一次 complete |
| complete 409「已进死信」 | 重试耗尽 | 管理端「外部任务」页一键重投 |
| 业务被执行两次 | 锁短于处理耗时 / worker 本地重试 / 非幂等 | 加大 `LockDurationMs`、去掉本地重试、加幂等键 |
| 死信无人知晓 | 没挂 watchdog | 确认 `NetBpm:ExternalTasks:Watchdog:Enabled=true`（控制台默认已挂） |
| 流程走完但业务表没变 | 业务入口返回 ok 却没写库 / 写错了库 | 看 3007 日志与 `leave_bill`；回执只认 `ok:true` |

---

## 7. 源码索引（想深挖时看这里）

| 关注点 | 位置 |
|---|---|
| `activiti:topic` 解析 | `src/NetBpm.Bpmn.Converter/ElementConverters.cs`（`ServiceTaskXmlConverter`） |
| "只能有一种实现" 校验 | `src/NetBpm.Bpmn.Validation/ProcessValidationRules.cs`（`RequiredAttributesRule`） |
| 登记任务、原地等待 | `src/NetBpm.Engine/Behavior/ExternalTaskActivityBehavior.cs` |
| 领取 / 回执命令 | `src/NetBpm.Engine/Command/FetchExternalTasksCommand.cs`、`CompleteExternalTaskCommand.cs`、`FailExternalTaskCommand.cs` |
| HTTP 端点 | `src/NetBpm.Rest/Endpoints/RuntimeEndpoints.ExternalTasks.cs` |
| 持久化 | `src/NetBpm.Persistence/Jobs/DapperExternalTaskBroker.cs`（表 `nb_external_task`） |
| scope 闸门 | `src/NetBpm.Rest/Security/EngineSurfaceScopes.cs`、`src/NetBpm.Platform.Contracts/Common/PlatformScopes.cs` |
| 巡检告警 | `src/NetBpm.Runtime/Hosting/ExternalTaskWatchdogService.cs` |
| 控制台页面 | `web/admin/src/pages/ExternalTasksPage.tsx`、`web/admin/src/api/externalTasks.ts` |
| 参考 worker（C#） | `samples/NetBpm.ExternalWorker/` |
| 外部任务专题文档 | `samples/external-task-worker-guide.md` |
| 流程建模指南 | `samples/process-authoring-guide.md` |
