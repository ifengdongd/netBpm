# 在 NetBpm 平台新建流程并配置 serviceTask

面向第一次在 NetBpm 上落一条流程的同学。目标：从零建一条流程、发布它、让结束分支执行一段业务代码，
并知道每一步失败时该看哪里。

> 适用基线：0.1.3（当前 `main`）。命令以控制台宿主 `http://localhost:5081` 为例；
> 独立部署时把地址换成 `NetBpm.Platform.Service` 的地址即可（管理面同样是 `/admin/v1` 与 `/api/admin`）。

---

## 0. 先记住三件事

1. **保存 ≠ 发布。** 保存只写「模型库」（`bpmn_model`），发布才产生可运行的流程定义（`ACT_RE_PROCDEF`）。
   控制台里是两次操作，API 也是两个端点。
2. **版本号由引擎递增，不由模型决定。** 同一 `processKey`（同租户）每次发布 `VERSION_ + 1`；
   模型自己的 `revision` 只用来做保存时的乐观并发（`If-Match`）。
3. **serviceTask 的"名字"必须能在宿主容器里解析到。** 解析不到时：
   - 启动即部署（代码里 `DeployProcessesAsync`）→ **直接抛异常，宿主起不来**；
   - 控制台/HTTP 发布 → **只返回 warning，定义照样落库**，等流程跑到该节点才 500。
   这是本篇最容易被忽略的一点，第 7 节专门讲。

---

## 1. 准备工作

### 1.1 启动控制台

```bash
dotnet run --project src/NetBpm.Admin
# http://localhost:5081
```

控制台要连数据库（默认 `netbpm_admin`）。首次启动前：

```bash
dotnet run --project tools/NetBpm.DbMigrate -- migrate \
  --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm_admin;User ID=root;Password=;"
```

### 1.2 建一个登录账号

控制台没有默认账号：

```bash
dotnet run --project tools/NetBpm.DbMigrate -- users add \
  --username admin --display-name 管理员 --scopes netbpm.admin --password "改成你的口令" \
  --provider mysql \
  --connection "Server=127.0.0.1;Port=3306;Database=netbpm_admin;User ID=root;Password=;"
```

`--scopes netbpm.admin` 是超级范围，覆盖全部 `netbpm.admin.*` 与平台面 `workflow:*`（含 `:admin`）。

### 1.3 换一个令牌（后续 curl 都用它）

```bash
TOKEN=$(curl -s -X POST http://localhost:5081/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"改成你的口令"}' | jq -r .token)
```

> Windows PowerShell：`curl` 是 `Invoke-WebRequest` 的别名，行为不同；
> 请用 `curl.exe` 或 `Invoke-RestMethod`，别直接照抄。

---

## 2. 新建并发布（界面路径，推荐）

### 2.1 新建模型

1. 打开 http://localhost:5081/models （模型库）。
2. 点「新建」，进入设计器（内容嵌在 `/models/editor` 的 iframe 里，实际是 `web/designer` 应用）。
3. 填模型名称与 **processKey**（对应 BPMN 里的 `<bpmn:process id="...">`，发布后不建议再改）。
4. 画最小骨架：`startEvent → userTask → endEvent`，连线用顺序流。

也可以直接「导入」一份现成 XML（`POST /api/admin/models/import`，`Content-Type: text/xml`）。

### 2.2 画流程时的两个即时反馈

- 设计器底部有**校验清单**（`#issues`）：本地规则实时跑，点条目能定位到出问题的元素。
- 阻断级错误（`hasBlockingError`）会**拦住部署**，状态栏提示「部署已阻止：…」。
  这批规则与后端同源，所以"本地过了、后端拒了"通常只剩 XML 解析与资源层面的问题。

### 2.3 保存（第一步）

设计器工具栏「保存」→ `PATCH /api/admin/models/{id}`，带 `If-Match: "<revision>"`：

- 服务端先跑一遍与发布同口径的校验，**阻断级问题返回 422 并列出 rule id**；
- 通过后写 `bpmn_model`（upsert）并追加快照 `bpmn_model_history`（`revision + 1`）；
- `If-Match` 与当前 revision 不一致 → 409，返回 `CurrentRevision`，前端提示刷新。

### 2.4 发布（第二步）

在模型库列表对该行点「部署」（代码里叫 publish）：

1. 先 `POST /api/admin/models/{id}/validate` 预检；
2. 再取详情渲染流程图 SVG；
3. 最后 `POST /api/admin/models/{id}/publish`，body `{"svg":"<svg…>","skipIfUnchanged":true}`。

发布成功后的返回与提示：

- `processDefinitionKey` + `version`（引擎版本号，例如 v3）；
- **`warnings` 非空不代表失败**：定义已经落库了。控制台会弹「已部署：xxx v3（N 条提示）」，
  并明确写"实例运行到相关节点时可能失败"。

### 2.5 验证发布结果

```bash
# 1) 部署列表（控制台「部署管理」页同源）
curl -s http://localhost:5081/api/designer/deployments -H "Authorization: Bearer $TOKEN"

# 2) 平台面目录：已发布的流程会出现在这里（客户端凭据/JWT 都能查）
curl -s http://localhost:5081/workflow/v1/processes -H "Authorization: Bearer $TOKEN"

# 3) 看某个 key 的定义版本
curl -s "http://localhost:5081/api/designer/definitions?key=<processKey>" -H "Authorization: Bearer $TOKEN"
```

最快的一次真实发起：

```bash
curl -s -X POST http://localhost:5081/workflow/v1/instances \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"processKey":"<processKey>","parameters":{}}'
```

---

## 3. 保存与发布各写了什么

| 动作 | 端点 | 写入 | 事务 |
|---|---|---|---|
| 保存 | `PATCH /api/admin/models/{id}`（`PUT` 为全量） | `bpmn_model` + `bpmn_model_history`（新 revision） | 一个事务 |
| 发布 | `POST /api/admin/models/{id}/publish` | `ACT_RE_DEPLOYMENT` / `ACT_GE_BYTEARRAY` / `ACT_RE_PROCDEF` + publish 快照 | 部署与快照同一事务（`PublishModelCommand`） |
| 直传部署 | `POST /api/designer/deploy` | 同上（不经模型表、不写 publish 快照） | 同上 |

几个容易踩的点：

- **发布用的是模型表里的 XML**，资源名是 `{modelId}.bpmn20.xml`；
  所以"改了 XML 没保存就发布"发布的是旧内容。
- `skipIfUnchanged=true` 时，若内容与最后一次发布一致 → **跳过部署**，直接回放线上版本，不产生新版本。
  重复点发布不会把版本号刷上去。
- 版本号在 `(KEY_, TENANT_ID_)` 维度上 `max(VERSION_) + 1`，并发靠 `ACT_UNIQ_PROCDEF` 兜底。

---

## 4. 另外两条发布途径

### 4.1 直传 XML：`POST /api/designer/deploy`

设计器内列表的「部署」走这条（`web/designer/src/main.ts` 的 `deployXml`），也适合 CI / 脚本：

```bash
curl -s -X POST "http://localhost:5081/api/designer/deploy?resourceName=leave.bpmn20.xml&deploymentName=leave" \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: text/xml' \
  -H "Idempotency-Key: $(uuidgen)" \
  --data-binary @web/designer/examples/leave-demo-process.bpmn20.xml
```

- body 是 **BPMN XML 纯文本**（不是 multipart），空 body → 400；
- query 全部可选：`resourceName`（默认 `designer.bpmn20.xml`）、`deploymentName`、`tenantId`；
- 成功 201 + `DeployResponse`（`processDefinitionId` / `version` / `warnings`）；
- 需要 `workflow:admin:process` **且** `netbpm.admin.model.write`；
- 不写模型表，因此控制台模型库里看不到这次发布（部署列表能看到）。

### 4.2 宿主启动自动部署（`samples/NetBpm.Demo` 的用法）

```csharp
// 流程定义唯一来源：web/designer/examples/*.bpmn20.xml（csproj 链接进输出目录）
await workflowBootstrapper.DeployProcessesAsync();
```

- 适合"流程定义随代码走"的交付方式：部署失败会让宿主**启动失败**（见第 7 节）；
- 不经过 HTTP 与模型表，也不需要控制台账号；
- Demo 还顺带把渲染好的 SVG 发布到 `wwwroot`，用于流程图回显。

### 4.3 选哪条

| 场景 | 用哪条 |
|---|---|
| 业务人员在控制台维护流程 | 模型库保存 + 发布（有版本快照，可回看历史图） |
| 设计器里改完立刻生效 | 设计器「部署」（直传 XML，无模型表痕迹） |
| 流程定义随代码走 / 环境初始化 | 启动自动部署 |
| 流水线里发布 | `POST /api/designer/deploy` + `Idempotency-Key` |

---

## 5. 在 BPMN 里写 serviceTask

### 5.1 三种写法

```xml
<!-- ① 推荐：按容器里的名字解析（与注册代码一一对应） -->
<bpmn:serviceTask id="svc_approve" name="审批通过回写"
                  activiti:delegateExpression="leave.ApproveComplete" />

<!-- ② 按 .NET 类型解析（类型要能反射创建，或已注册到 DI） -->
<bpmn:serviceTask id="svc_approve" name="审批通过回写"
                  activiti:class="MyCompany.Flow.ApproveDelegate" />

<!-- ③ 表达式：求值；结果是委托就执行，否则写进 resultVariableName -->
<bpmn:serviceTask id="svc_calc" name="算分"
                  activiti:expression="${scoringService.Weight(amount)}"
                  activiti:resultVariableName="score" />
```

优先级（同时写了多个属性时以靠前者为准）：`class` > `delegateExpression` > `expression` > `topic`。

`activiti:type="external"` + `topic` 走的是**外部任务**，不是委托，见 `NetBpm.ExternalWorker` 样例。
既没有 topic 也没有上述三种写法的 serviceTask，运行到该节点会报「未指定实现方式」。

### 5.2 字段注入

委托需要静态配置（如目标系统编码）时，用 `activiti:field`：

```xml
<bpmn:serviceTask id="svc_notify" name="发通知" activiti:delegateExpression="notify.Approved">
  <bpmn:extensionElements>
    <activiti:field name="templateCode" stringValue="LEAVE_APPROVED" />
    <activiti:field name="retryLimit" expression="${notifyRetryLimit}" />
  </bpmn:extensionElements>
</bpmn:serviceTask>
```

字段在委托执行前注入，`stringValue` 是字面量，`expression` 会求值。

---

## 6. 实现并注册委托

### 6.1 最小实现

```csharp
using NetBpm.Engine.Behavior;      // IServiceTaskDelegate
using NetBpm.Engine.Execution;     // IDelegateExecution
using NetBpm.Runtime;              // ServiceTaskNameAttribute

[ServiceTaskName("leave.ApproveComplete")]
public sealed class LeaveApproveDelegate : IServiceTaskDelegate
{
    private readonly ILeaveRepository _leaves;   // 业务依赖走构造函数注入

    public LeaveApproveDelegate(ILeaveRepository leaves) => _leaves = leaves;

    public async ValueTask ExecuteAsync(IDelegateExecution execution, CancellationToken ct = default)
    {
        // 1) 读流程变量（审批结论 / 业务单号都由前序节点写入）
        var businessId = (await execution.GetVariableAsync("businessId", ct))?.ToString();
        var comment = (await execution.GetVariableAsync("comment", ct))?.ToString();

        if (string.IsNullOrWhiteSpace(businessId))
        {
            throw new InvalidOperationException(
                $"服务任务 '{execution.CurrentActivityId}' 缺少 businessId，无法回写业务状态。");
        }

        // 2) 业务回写
        await _leaves.MarkApprovedAsync(businessId, comment, ct);

        // 3) 把结论写回流程变量（后续节点 / 时间线可视化可用）
        await execution.SetVariableAsync("approvalResult", "approved", ct);
    }
}
```

### 6.2 注册方式 A：扫描程序集（推荐）

```csharp
// Program.cs：一行扫完，名字来自 [ServiceTaskName]，与 BPMN 里的 delegateExpression 对齐
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly);
```

名字来源三档（前者优先）：

1. `[ServiceTaskName("x")]` 显式名 —— **重名直接抛异常**（启动期就炸，不等到运行）；
2. 类型全名 `Namespace.ClassName`；
3. 类型短名 `ClassName` —— 仅当程序集内唯一时才登记，撞名时让位。

特性可叠加（一个类挂多个名字），所有别名共享**同一个单例实例**，不会重复构造。

### 6.3 注册方式 B：手写 keyed 注册

名字就是 BPMN 里的原文（解析时会剥掉 `${}`）：

```csharp
builder.Services.AddSingleton<LeaveApproveDelegate>();
builder.Services.AddKeyedSingleton<object>(
    "leave.ApproveComplete",
    (provider, _) => provider.GetRequiredService<LeaveApproveDelegate>());
```

需要每命令一个实例（如持有 DbSession）时用 Scoped：

```csharp
builder.Services.AddScoped<LeaveApproveDelegate>();
builder.Services.AddKeyedScoped<object>(
    "leave.ApproveComplete",
    (provider, _) => provider.GetRequiredService<LeaveApproveDelegate>());
```

解析器在命令内使用**命令作用域**，命令结束即释放。

### 6.4 `activiti:class` 的约束

- 类型已注册到 DI → 构造函数注入生效；
- 类型未注册 → 只能 `Activator.CreateInstance`，即**必须有无参构造**，否则抛
  `EngineException`（提示"请把它注册到 DI"）。

### 6.5 控制台宿主的内置占位委托

`NetBpm.Admin` 默认注册了 6 个占位名字（只写变量 + 打日志，不写业务表）：

| 名字 | 类型 |
|---|---|
| `leave.ApproveComplete` / `leave.RefuseComplete` | `IServiceTaskDelegate` |
| `order.ApproveComplete` / `order.RefuseComplete` | `IServiceTaskDelegate` |
| `WorkFlowProcessListener` | `IExecutionListener` |
| `WorkFlowTaskCompletedListener` | `IEngineEventListener` |

开关：`NetBpm:Admin:BuiltInDelegates:Enabled`（默认 `true`）。

业务宿主接入时：

```json
{ "NetBpm": { "Admin": { "BuiltInDelegates": { "Enabled": false } } } }
```

然后注册自己的实现（与内置**同名不要并存**：keyed 解析按注册顺序取第一个命中，重名排查成本高）。
BPMN 里引用了 `WorkFlowProcessListener` / `WorkFlowTaskCompletedListener` 的流程，
也必须由业务宿主提供实现，否则同样会在自检或运行时报"解析不到"。

真实业务回写的参考实现：`samples/NetBpm.Demo/Delegates/ApprovalCompletionDelegate.cs`
（读 `businessId` / `comment` → 业务回写 → `IWorkflowService.CompleteAsync` 落结论）。

---

## 7. 部署期自检：为什么"部署成功"不等于"能跑"

部署时会预解析所有 serviceTask 的 `class` / `delegateExpression`（`topic` 与 `expression` 跳过），
以及执行/任务/事件监听器；核心是 `src/NetBpm.Workflow/Diagnostics/ServiceTaskDelegateGuard.cs`。

| 触发点 | 行为 | 后果 |
|---|---|---|
| 宿主启动自动部署 | 解析失败 → **抛异常，阻断启动** | 环境根本起不来（配置错在部署期暴露） |
| 控制台发布 / `POST /api/designer/deploy` | 解析失败 → 转成 **Warning** | 定义已落库；实例跑到该节点才 500 |

判断方法：部署响应的 `warnings` 里 **`rule = "delegate-resolution"`** 就是这类问题。
实测（把两个 `delegateExpression` 故意写成不存在的名字）发布仍返回 201：

```json
{
  "processDefinitionId": "doc_check_demo:1:15054",
  "version": 1,
  "warnings": [
    {
      "rule": "delegate-resolution",
      "elementType": "delegateExpression",
      "severity": 0,
      "isError": false,
      "message": "流程定义 doc_check_demo:1:15054 里有委托无法解析：svc_approve_complete（activiti:delegateExpression=\"doc.NotRegistered\" 解析不到）；svc_refuse_complete（…）。请检查 BPMN 里写的名字，以及宿主是否已注册该委托。"
    }
  ],
  "skipped": false
}
```

两个实践建议：

1. **把发布响应里的 warnings 当失败处理**（CI 里尤其如此）。只在控制台看一眼弹窗很容易漏。
2. 启动部署偶尔需要临时放行时，用 `WorkflowProcessBootstrapper` 的 `SkipServiceTaskDelegateCheck`
   —— 只适合诊断，不要带进生产配置。

---

## 8. 委托里能做什么、不能做什么

`IDelegateExecution` 上常用的成员：

| 用途 | 成员 |
|---|---|
| 读一个变量 | `GetVariableAsync(name, ct)` |
| 读全部变量 | `GetVariablesAsync(ct)` |
| 写变量 | `SetVariableAsync(name, value, ct)` / `SetVariablesAsync(dict, ct)` |
| 判断 / 删除 | `HasVariableAsync` / `RemoveVariableAsync` |
| 实例与定义 | `ProcessInstanceId` / `ProcessDefinitionId` / `Id` |
| 当前节点 | `CurrentActivityId`、`CurrentFlowElement` |
| 发起人 | `StartUserId`（EL 里叫 `initiator`） |
| 事件名（监听器场景） | `EventName` |

限制与边界：

- **不要 await 外部系统 IO**（HTTP/RPC）。委托在流程命令的事务内执行，一次慢调用会拖长事务、
  放大锁竞争。需要外部调用时：写 outbox/本地表，由后台作业发；或把节点配成异步作业
  （`activiti:asyncBefore="true"` / `asyncAfter="true"`），让它在独立事务里跑。
- `IDelegateExecution` **不提供引擎/仓储入口**。要查库、要调业务服务，走**构造函数注入**。
- 委托要幂等：失败重试、人工重放、异步作业重试都可能让同一节点执行多次。

---

## 9. 委托失败时会发生什么

同步路径（默认）：

1. 委托抛异常 → 异常原样上抛，**不吞、不降级**；
2. 一个命令 = 一个事务 → **本次推进整体回滚**（变量、历史、令牌变更一起回滚）；
3. 令牌留在该 serviceTask 节点，调用方拿到 5xx（平台面是 RFC 9457 的 `internal_error` 或对应业务错误）；
4. **没有"实例进入失败状态"这回事**，同步路径**不会自动重试** —— 由调用方决定是否重试。

异步路径（节点配了 `asyncBefore` / `asyncAfter`）：

- 失败后作业行保留，按退避重试：默认最多 3 次，间隔 5s / 10s / 20s（上限 300s）；
- 重试用尽进死信，需要人工处理；
- 相关指标：`netbpm.async_job.failure`；同步命令的成败看 `netbpm.command.executed{result="..."}`。

> 注意：日志拦截器在非 Debug 级别下不打印命令失败详情，**别只靠日志判断**——
> 以接口返回、指标与 `ACT_RU_*` / 时间线为准。

---

## 10. 完整示范：请假流程的结束回写

配套文件：`web/designer/examples/leave-demo-process.bpmn20.xml`（key `leave_demo`）。

```
start → 组长审批(userTask) → gw_lead
        ├─ ${result == 1} → svc_approve_complete (leave.ApproveComplete) → end_pass
        ├─ ${result == 2} → svc_refuse_complete  (leave.RefuseComplete) → end_refuse
        └─ default 驳回修改 → 申请人修改(userTask) → 回组长审批
```

关键片段：

```xml
<bpmn:exclusiveGateway id="gw_lead" name="审批结果" default="flow_lead_modify">
  <bpmn:incoming>flow_lead_gw</bpmn:incoming>
  <bpmn:outgoing>flow_lead_pass</bpmn:outgoing>
  <bpmn:outgoing>flow_lead_refuse</bpmn:outgoing>
  <bpmn:outgoing>flow_lead_modify</bpmn:outgoing>
</bpmn:exclusiveGateway>

<bpmn:sequenceFlow id="flow_lead_pass" name="通过" sourceRef="gw_lead" targetRef="svc_approve_complete">
  <bpmn:conditionExpression xsi:type="bpmn:tFormalExpression"><![CDATA[${result == 1}]]></bpmn:conditionExpression>
</bpmn:sequenceFlow>

<bpmn:serviceTask id="svc_approve_complete" name="审批通过回写"
                  activiti:delegateExpression="leave.ApproveComplete">
  <bpmn:incoming>flow_lead_pass</bpmn:incoming>
  <bpmn:outgoing>flow_pass_end</bpmn:outgoing>
</bpmn:serviceTask>
```

用控制台跑一遍（最短路径）：

1. 模型库导入该 XML（或 `POST /api/designer/deploy` 直传）→ 发布；
2. `/workflow/v1/processes` 里能看到 `leave_demo`；
3. 发起：`{"processKey":"leave_demo","parameters":{"param.days":"2","param.reason":"演示"}}`；
4. 待办里提交 `optionId=1`（通过）；
5. 期望：实例 `COMPLETED`、结论 `APPROVE`；控制台日志出现
   `内置占位委托：activity=svc_approve_complete，instance=…，结论=approved`；
   引擎历史活动里能看到 `svc_approve_complete`（类型 `serviceTask`）与 `end_pass`。

想中途演示"跳转"（审批人不在）：在审批节点调
`POST /api/runtime/process-instances/{id}/jump`，body `{"targetActivityId":"task_apply_modify","reason":"…"}`，
时间线会记一条 `INSTANCE_JUMPED`，跳回审批用同样的接口。

自动化守门：`tests/NetBpm.IntegrationTests/LeaveJumpAndServiceTaskTests.cs`
（直接部署 examples 里那份 XML，跑完「发起 → 两次跳转 → 通过 → 委托执行」）。

---

## 11. 排错手册

| 症状 | 可能原因 | 处理 |
|---|---|---|
| 保存返回 422，`errors[].rule` 有值 | BPMN 规则不通过（如排他网关出流全带条件且无默认流） | 按 rule 修模型；排他网关必须留一条默认流，兜底方向选"退回"而不是"通过" |
| 保存返回 409 | `If-Match` 的 revision 过期（别人先保存了） | 用响应里的 `CurrentRevision` 刷新后重试 |
| 部署返回 201，但 `warnings` 里有 `delegate-resolution` | 委托名字解析不到 | 注册实现（第 6 节）；确认 `BuiltInDelegates` 开关与重名情况 |
| 部署 201，运行到 serviceTask 500 | 同上；或委托内部抛异常 | 看接口错误体与 `netbpm.command.executed{result="failure"}`；委托内的异常信息会带节点 id |
| 发起 422 `parameter.*` | 参数规范不满足（必填缺失 / 只读字段 / 类型不符） | 调 `GET /workflow/v1/processes/{key}/parameters/schema` 对齐参数名与类型 |
| 发起 500 `ElException` | BPMN 用了 `${applyUser}` 之类调用方给不了的变量 | 平台面只接受 `param.*`；改用 `candidateUsers`，或让宿主在发起前注入变量 |
| 待办里没有审批选项 | userTask 没写 `activiti:formProperty`（enum + value） | 补表单属性；选项 id 就是提交时的 `optionId` |
| 提交 422 `decision.*` | 没有 `optionId`，或值不在选项集 | 平台面提交 = 审批决策；无审批选项的填表节点要用引擎面 `complete` |
| 跳转后提交 404/403 | 跳转删除了原待办、在新节点重建了任务 | 重新查待办拿新 taskId，不要复用旧 id |
| 流程走完但业务表没变 | 用的是控制台内置占位委托 | 关掉 `BuiltInDelegates`，注册业务实现 |

---

## 附录 A：操作与 scope

| 操作 | 端点 | 需要 |
|---|---|---|
| 新建 / 导入 / 保存 / 发布模型 | `/api/admin/models/**` | `netbpm.admin.model.write` |
| 查模型 | `GET /api/admin/models/**` | `netbpm.admin.model.read` |
| 直传部署 | `POST /api/designer/deploy` | `workflow:admin:process` **且** `netbpm.admin.model.write` |
| 校验 XML（不落库） | `POST /api/designer/validate` | `workflow:process:read` |
| 发起 / 提交 / 终止 | `/workflow/v1/**` | `workflow:instance:start` / `workflow:task:submit` / `workflow:instance:terminate` |
| 跳转 / 迁移 / 变量直写 | `/api/runtime/**` | `workflow:admin:instance` / `workflow:admin:variables` |

`netbpm.admin` 覆盖全部 `netbpm.admin.*`；`workflow:*` 通配**不覆盖** `:admin` 与 `:onbehalf`。
注册给外部系统的客户端拿不到 `netbpm.admin.*`，只能用 `workflow:*` 那一套。

## 附录 B：关键源码位置

| 关注点 | 文件 |
|---|---|
| 模型 CRUD / 发布端点 | `src/NetBpm.Admin/Endpoints/AdminEndpoints.Models.cs` |
| 发布命令（部署 + 快照同事务） | `src/NetBpm.Admin/Storage/PublishModelCommand.cs` |
| 直传部署端点 | `src/NetBpm.Rest/Endpoints/DesignerEndpoints.cs` |
| 委托契约与解析器 | `src/NetBpm.Engine/Behavior/IServiceTaskDelegate.cs`、`src/NetBpm.Runtime/ServiceProviderActivityDelegateResolver.cs` |
| 委托执行 | `src/NetBpm.Engine/Behavior/ServiceTaskDelegateBehavior.cs` |
| 部署期自检 | `src/NetBpm.Workflow/Diagnostics/ServiceTaskDelegateGuard.cs` |
| 扫描注册 | `src/NetBpm.Runtime/ServiceTaskDelegateRegistrationExtensions.cs` |
| 控制台内置占位 | `src/NetBpm.Admin/Workflow/BuiltInProcessDelegates.cs` |
| 业务委托范例 | `samples/NetBpm.Demo/Delegates/ApprovalCompletionDelegate.cs` |
| 示例流程 | `web/designer/examples/leave-demo-process.bpmn20.xml` |
| 端到端用例 | `tests/NetBpm.IntegrationTests/LeaveJumpAndServiceTaskTests.cs` |
