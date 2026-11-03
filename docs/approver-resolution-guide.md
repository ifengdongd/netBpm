# 审批人（办理人）解析与获取指南

> 适用版本：v1.29 之后（`src/` 当前主干）。本文所有结论都对着源码核过，末尾给出源码索引。
> 关联文档：`docs/leave-external-task-setup.md`（请假流程 + approveComplete 外部任务）、`docs/process-design-guide.md`（建模规则）。

## 0. 一句话结论

**引擎只在「任务创建那一刻」用流程变量求值 BPMN 里写的 `assignee` / `candidateUsers` / `candidateGroups`，它不会在流程推进过程中发起任何外部 HTTP 调用，BPMN 里也没有"审批人接口地址"这种配置项。**

所以"审批人来自外部系统"这件事，必然落在下面两个位置之一：

1. **信息先到、引擎后读** —— 外部系统在发起 / 提交时把审批人算好，塞进流程变量（**不改代码**）；
2. **流程里多一步** —— 用一个外部任务节点去调接口取人，把结果写回变量（**不改代码**）；
3. **改 NetBpm 宿主** —— 注册监听器 / 审批人钩子 / 身份适配器，在引擎进程内调接口（要发版）。

```
                  ┌──────────────── 调用方（3007 / OA / HR 门户）────────────────┐
                  │  ① 发起前：调 HR 接口算审批人 → parameters{approver}        │
                  │  ② 或：提交上一环节时顺带把下一环节审批人写进 parameters      │
                  └───────────────────────────┬─────────────────────────────────┘
                                              │ POST /workflow/v1/instances
                                              ▼
   BPMN: activiti:assignee="${approver}"  ──►  引擎求值（只有流程变量 / execution / initiator）
                                              │
                                              ├─ 写 ACT_RU_TASK.ASSIGNEE_（单人）
                                              └─ 写 ACT_RU_IDENTITYLINK（候选人 / 候选组）
                                              ▼
   外部系统读回：GET /api/runtime/tasks/{id}/identity-links  ← 唯一能拿到"求值后"候选人的接口
```

## 1. 三个落点与引擎的解析顺序

| BPMN 属性 | 落库位置 | 查询怎么命中 |
| --- | --- | --- |
| `activiti:assignee` | `ACT_RU_TASK.ASSIGNEE_` | 按 `assignee=`（引擎面）；平台面是 `scope=CLAIMED` |
| `activiti:candidateUsers` | `ACT_RU_IDENTITYLINK`，`USER_ID_` + `TYPE_='candidate'` | 按 `candidateUser=`（平台面是 `scope=MINE`） |
| `activiti:candidateGroups` | `ACT_RU_IDENTITYLINK`，`GROUP_ID_` + `TYPE_='candidate'` | 按 `candidateGroup=`；`candidateUser=` 会用"我在哪些组"去 `in` 匹配 |

解析发生在 `NetBpm.Engine/Behavior/TaskBehaviors.cs` 的 `HandleAssignmentsAsync`，顺序与 Activiti 一致：**assignee → owner → candidateGroups → candidateUsers**。

任务创建的完整时序（同一文件 `ExecuteAsync`）：

```
1. CreateTask + InsertTaskAsync          ← 必须先插入，因为办理关系要 taskId
2. taskListener event="create"           ← ⚠️ 此时 ASSIGNEE_ 还是空的
3. 流程级 TASK_CREATED 事件
4. skipExpression 判断（true 则删任务、直接离开节点）
5. HandleAssignmentsAsync                ← 这里才解析 assignee / candidates
6. taskListener event="assignment"       ← 仅当第 5 步真的指到了人才触发
7. 挂定时边界事件 → 停在节点等人
```

**第 2 步早于第 5 步**这一点很有用：`create` 监听器里 `SetVariable("approver", xxx)`，紧接着的 `${approver}` 就能取到值（见 §3.3）。

### 1.1 求值规则（会踩到的三条）

- 三个属性都支持 `${}` / `#{}`，走 `IExpressionManager`。
- 求值上下文**只有三样东西**：流程变量、隐式对象 `execution`、隐式对象 `initiator`（= 流程实例的 `startUserId`）。
- `candidateUsers` / `candidateGroups` 的求值结果**必须是单个 string**，多人用逗号分隔（`CandidateSeparator`）。返回 `List<string>` 会直接抛
  `EngineException: Expression did not resolve to a string or collection of strings` —— 报错文案里写着 collection，但实现只接受 string，别被骗。

```xml
<!-- ✅ 对：单个字符串，多人用逗号 -->
<bpmn:userTask id="task_approve" name="经理审批" activiti:candidateUsers="${approvers}" />
<!-- 变量 approvers = "zhangsan,lisi" -->

<!-- ❌ 错：${approverList} 求值成 List<string> → 部署能过，运行时抛 EngineException -->
```

### 1.2 求值为空时会发生什么

| 情况 | 结果 |
| --- | --- |
| `assignee` 求值为 null / 空串 | 任务无办理人，`ASSIGNEE_` 为空；谁能 `claim` 谁办 |
| `candidateUsers` 为空 | 任务没有任何候选关系 → 只有管理视角看得见（`scope=ALL`） |
| `candidateGroups` 为空 | 同上；若组存在但没人能展开组，也会表现为"待办消失"（见 §5.1） |

## 2. 为什么"流程走到节点再查接口"不能纯配置

EL 里**可以**写方法调用（`ElResolver` 的解析链是 标识符 → 属性 → 索引 → 方法调用），但方法只能打到**进程内的活对象**上。数据库宿主的流程变量是序列化进 `ACT_RU_VARIABLE` 的字符串 / 数字，不存在"把一个 HTTP 客户端存进变量再调用"的用法。

所以 `${hrApi.findManager(applyUser)}` 这种写法在 BPMN 里不成立。要实现"运行时查人"，只有三条路：外部任务节点（§3.2）、监听器 / 审批人钩子（§3.3）、身份适配器（§3.4）。

## 3. 四条接线方式

### 3.1 A：变量预置（推荐，零代码）

**谁算**：调用方（你们的 3007、OA、HR 门户）在**发起**或**上一环节提交**之前，调组织架构接口把审批人算好。

**发起时传**：

```bash
curl -s -X POST "http://localhost:5081/workflow/v1/instances" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{
        "processKey": "leave_days_approval",
        "businessKey": "LEAVE-2026-0001",
        "parameters": { "days": "5", "approver": "zhangsan" }
      }'
```

**提交上一环节时传**（`SubmitTaskRequest.parameters`，同样会写成流程变量）：

```bash
curl -s -X POST "http://localhost:5081/workflow/v1/tasks/$TASK_ID/submit" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"optionId":"1","comment":"同意","parameters":{"approver":"lisi"}}'
```

**BPMN 侧**：

```xml
<bpmn:userTask id="task_approve_manager" name="经理审批" activiti:assignee="${approver}" />
```

三条必须知道的口径：

- 参数名可带 `param.` 前缀（`param.days`），服务端会剥掉前缀镜像回裸名 —— 目的是让"表单字段 `param.days`"与"网关条件 `${days}`"共用同一个变量名。
- 保留名不能传，否则 400 `parameter.conflict_with_reserved`。清单就 8 个（`BaseModelVariables.All`）：`businessId`、`businessDeptId`、`result`、`comment`、`applyUser`、`applyTime`、`applyDeptId`、`workflowTypeId`。
- 流程**未声明**该参数（`/processes/{key}/parameters/schema` 里没有）时，参数仍会原样透传进变量 —— 官方注释明确举了 `${approver}` 这个例子。所以不必为了"能传审批人"先去登记参数；但登记了才能做必填 / 类型 / 可写性（`writableBy`）校验。

**边界**：审批人在"提交那一刻"就定死了。提交后组织架构调整，不会重算 —— 除非你再提交一次或干预改派。

### 3.2 B：外部任务节点取人（推荐"运行时动态"，不改宿主）

把"查审批人"变成流程里的**一个可观测步骤**：`serviceTask` + `activiti:topic`，由外部 worker 领任务、调接口、写回变量。

```xml
<bpmn:serviceTask id="task_resolve_approver" name="查询审批人"
                  activiti:topic="resolveApprover" />

<bpmn:userTask id="task_approve_manager" name="经理审批"
               activiti:assignee="${approver}" />
```

worker 回执时带变量：

```json
POST /api/runtime/external-tasks/{taskId}/complete
{ "workerId": "oa-worker-1", "variables": { "approver": "zhangsan" } }
```

**顺序有保证**：`CompleteExternalTaskCommand` 里先 `execution.SetVariablesAsync(_variables)`，再 `PlanTriggerExecutionOperation`。所以下一节点的 `${approver}` 一定取得到，不是"碰巧"。

| 优点 | 代价 |
| --- | --- |
| 查人失败可重试 / 进死信，**绝不跳过节点** | 每个实例多一条 `nb_external_task` 记录 |
| 不占引擎事务，HR 接口慢不会拖住 `ACT_RU_*` 的锁 | 多一次 worker 往返（默认秒级） |
| 时间线上能看到"查人"这一步，排障有据 | 需要 3007 侧多实现一个 topic handler |

### 3.3 C：监听器 / 审批人钩子（要改 NetBpm 宿主并重新发版）

**`ITaskListener`（BPMN 里配，实现类在引擎进程内）**

```xml
<!-- definitions 根上需要 xmlns:activiti="http://activiti.org/bpmn" -->
<bpmn:userTask id="task_approve_manager" name="经理审批" activiti:assignee="${approver}">
  <bpmn:extensionElements>
    <activiti:taskListener event="create" class="Opms.Bpmn.ResolveApproverListener, Opms.Bpmn" />
  </bpmn:extensionElements>
</bpmn:userTask>
```

```csharp
public sealed class ResolveApproverListener : ITaskListener
{
    public async ValueTask NotifyAsync(TaskListenerContext context, CancellationToken ct = default)
    {
        // create 事件发生在 HandleAssignmentsAsync 之前，这里写的变量会被随后的 ${approver} 读到
        var approver = await _hrClient.FindManagerAsync(context.Execution.StartUserId!, ct);
        await context.Execution.SetVariableAsync("approver", approver, ct);
    }
}
```

**`IApproverResolver`（引擎级钩子，只在 assignee 分支生效）**

```csharp
// 注册：必须在宿主里显式调用，默认不注册
builder.Services.AddNetBpmApproverResolver(new MyApproverResolver());
// 或有内置规则（发起人本人跳过 / 连续相同合并 / 空审批人转管理员）：
builder.Services.AddNetBpmApproverDeduplication(o => o.FallbackAdminUserId = "admin");
```

两个钩子的真实边界（`TaskBehaviors.PlanApprovalChainAsync`）：

- 只在 `assignee` 分支被调用，**`candidateUsers` / `candidateGroups` 完全不经过它**。
- 返回值只取 `plan[0]`，即**单值**；多审批人场景用不上。
- 同步阻塞在命令事务内 —— 在里面发 HTTP，等于把外部系统的延迟和故障注入到 `ACT_RU_*` 的写事务里，而且 `RetryInterceptor` 还会重放。真要用，必须自带超时 + 降级（失败就退回某个兜底审批人），不能抛。
- `NetBpm.Rest` / `NetBpm.Admin` 宿主当前**都没有**注册 `IApproverResolver`，所以控制台上"发起人自动跳过"这类规则是不生效的。

### 3.4 D：候选组 + `IUserGroupManager`（要改宿主）

BPMN 只写组 id，组 → 人由适配器展开：

```xml
<bpmn:userTask id="task_approve" name="部门经理审批" activiti:candidateGroups="dept_manager" />
```

```csharp
public sealed class OpmsUserGroupManager : IUserGroupManager
{
    // 查"我的待办"用：我属于哪些组（引擎把它拼进 identitylink 的 in 条件）
    public ValueTask<IReadOnlyCollection<string>> FindGroupIdsByUserAsync(string userId, CancellationToken ct)
        => _org.GetGroupIdsAsync(userId, ct);

    // 反方向：这个组里有谁（业务侧要记"该谁办"时用；返回空集是合法结果）
    public ValueTask<IReadOnlyCollection<string>> FindUserIdsByGroupAsync(string groupId, CancellationToken ct)
        => _org.GetMemberIdsAsync(groupId, ct);
}

// 注册：Replace 语义，放 AddNetBpm 之前/之后都行
builder.Services.AddNetBpmUserGroupManager<OpmsUserGroupManager>();
```

⚠️ **这是控制台最常见的"审批卡住"根因**：宿主不替换时用的是 `EmptyUserGroupManager`，任何组都展开成 0 个用户，任务挂在那儿但谁的待办里都不出现。`CandidateGroupGuard` 在部署时会 LogWarning，措辞就是"候选组会展开成 0 个用户，待办将静默消失"。

### 3.5 四条路对比

| | 改动 | 审批人何时确定 | 支持多人 | 失败可见性 | 适用 |
| --- | --- | --- | --- | --- | --- |
| A 变量预置 | 无 | 发起 / 上一环节提交时 | ✅（逗号分隔） | 调用方自己负责 | **默认首选** |
| B 外部任务取人 | 无（写 worker） | 走到该节点时 | ✅ | ✅ 重试 + 死信 | 审批人要按当时数据算 |
| C 监听器 / 钩子 | 改宿主发版 | 任务创建时 | ⚠️ 钩子只支持单人 | 只能靠日志 | 宿主就在你们手里 |
| D 候选组适配器 | 改宿主发版 | 查询待办时展开 | ✅ | 空集静默 | 已有权限中台 / AD |

## 4. 外部系统怎么"获取"审批人（读接口）

| 想知道什么 | 调哪个 | 拿到的是什么 |
| --- | --- | --- |
| 这个任务**实际**挂给了谁（表达式求值后） | `GET /api/runtime/tasks/{taskId}/identity-links` | `ACT_RU_IDENTITYLINK` 的真实行：`assignee` / `candidate` / `owner` / `participant` |
| 办理人是谁 | `GET /workflow/v1/tasks/{taskId}` 的 `assignee`，或 `GET /workflow/v1/instances/{id}` 的 `tasks[].assignee` | 运行时真值 ✅ |
| BPMN 里声明的候选（**原文**） | 同上两个接口的 `candidateUsers` / `candidateGroups` | ⚠️ **BPMN 原文，未求值**：写的是 `${approver}` 就原样返回 `${approver}` |
| 我该办哪些 | `GET /workflow/v1/tasks?scope=MINE` | 未签收的候选（含候选组命中，靠 JWT 的 `groups` 声明或 `IUserGroupManager` 展开） |
| 我名下已签收的 | `scope=CLAIMED` | `ASSIGNEE_` = 我 |
| 某个组里有哪些人 | `GET /api/identity/groups/{groupId}/users` | 依赖 `IUserGroupManager`；默认空实现 → `404`（组不存在）或空数组 |
| 某人在哪些组 | `GET /api/identity/users/{userId}/groups` | 同上；只能查自己，查别人需管理员 |
| 管理员看全部 | `scope=ALL` | 需要超管或 `workflow:history:read:all`，否则 403（不静默降级） |

```bash
# 唯一能拿到"求值后"候选人的接口
curl -s "http://localhost:5081/api/runtime/tasks/$TASK_ID/identity-links" \
  -H "Authorization: Bearer $TOKEN"
# → [{"id":"...","type":"candidate","userId":"zhangsan","groupId":null,"taskId":"...", ...},
#    {"id":"...","type":"participant","userId":"zhangsan","processInstanceId":"..."}]
```

`scope=MINE` 的组匹配有个前提：组声明要出现在调用方令牌里（`AuthenticationMiddleware` 读 `groups` / `group` claim）。**客户端凭据模式下没有组声明**，此时只能命中显式 `assignee` 或候选用户 —— 这正好解释了"服务间调用看不到候选组待办"。

## 5. 六个必踩的坑

### 5.1 默认身份适配器是空实现

`AddNetBpm` 里是 `TryAddSingleton<IUserGroupManager, EmptyUserGroupManager>()`。控制台（`NetBpm.Admin` / `NetBpm.Rest`）**没有替换**，所以：

- `candidateGroups` 的节点 → 展开 0 人 → 待办对谁都不可见；
- `/api/identity/**` 三个端点全部返回空 / 404；
- 部署时有一条 LogWarning 提醒（`CandidateGroupGuard`）。

如果候选关系本来就是你外部系统算好、直接写成 `candidateUsers` 的，可以忽略这条告警 —— 代码注释也是这么说的。

### 5.2 `TaskView.candidateUsers` 是定义原文，不是解析值

`TaskCatalog` 里这两个字段直接取自 BPMN 模型对象（`userTask.CandidateUsers`），**没有求值**。所以 `${approver}` 会原样出现在响应里。要"求值后的"，只能查 `identity-links`。

`TaskView.assignee` 则相反，来自 `ACT_RU_TASK.ASSIGNEE_`，是运行时真值。

### 5.3 `scope=MINE` **不包含**已指派 `assignee` 的任务

这条最反直觉，也是排查"我的待办里没这张单"的第一嫌疑：

- `MINE` → `TaskQuery(Assignee: null, CandidateUser: 我)` → 走 `FindTasksByCandidateUserAsync`
- 那条 SQL 带 `where T.ASSIGNEE_ is null` —— 语义是"**未签收的候选**"
- 而 `activiti:assignee` 直接把 `ASSIGNEE_` 写上了，且**不额外写任务级 candidate 行**（只写实例级 `participant`）

所以要查"指派给我的"：

```bash
GET /workflow/v1/tasks?scope=CLAIMED                 # 平台面：ASSIGNEE_ = 我
GET /api/runtime/tasks?assignee=zhangsan             # 引擎面：直查
GET /api/runtime/tasks?processInstanceId={id}        # 或按实例正查
```

**建模建议**：如果希望审批人能在"我的待办"里自然出现，用 `candidateUsers="${approver}"`（可认领），而不是 `assignee="${approver}"`（直接锁定）。前者还能配合 `claim` / `unclaim` / `transfer` 做认领与转办。

### 5.4 `candidateUsers` 表达式不能返回集合

见 §1.1。想要多人，就在变量里存 `"a,b"` 这样的字符串。

### 5.5 保留名不能当参数传

`result` / `comment` / `applyUser` / `businessId` … 见 §3.1。传了就是 400，且错误码是 `parameter.conflict_with_reserved`。

### 5.6 没有 `${starter}`，只有 `${initiator}`

设计器属性面板的提示文案写的是 `${starter}`，但 EL 的隐式对象只有 `execution` 和 `initiator`。写 `${starter}` 会抛 `ElException`（`无法解析标识符 'starter'`）——**不是「办理人为空」，是整个请求被拒**：事务回滚，任务/实例都留不下来。这类异常以前落到兜底 500「服务器内部错误」，2026-11-03 起映射为 **422 `engine.expression_error`** 并把异常消息原样透出（`表达式里用到了未知属性：${xxx}`）。

正确写法三选一：

```xml
<!-- 1) 隐式对象：直接用发起人 -->
<bpmn:userTask id="t1" activiti:assignee="${initiator}" />

<!-- 2) 开始事件声明变量名：发起时把 startUserId 写进 applyUser -->
<bpmn:startEvent id="start" activiti:initiator="applyUser" />
<bpmn:userTask id="t2" activiti:assignee="${applyUser}" />

<!-- 3) 调用方显式传 -->
<bpmn:userTask id="t3" activiti:assignee="${nextApprover}" />
```

> ⚠️ 第 2 种里的 `activiti:initiator` 不是可选项。平台的**引擎面**发起（`POST /api/runtime/process-instances`，控制台「部署管理 → 发起」走的就是它）只把 `startUserId` 落到执行实体上，**不会**替你生成 `applyUser` 变量；缺了它，`${applyUser}` 在第一个节点求值就失败——症状是「点发起就报错，一条实例都没建出来」。<br>
> 而 `/workflow/v1` 业务层发起相反：`Applicant` 会被写成 `applyUser`。同一份 BPMN 在两个入口表现不同，原因就在这里。

## 6. 选哪条：决策树

```
审批人能不能在「发起 / 上一环节提交」时算出来？
├─ 能 ──────────────────────────────► A 变量预置（零改动）
└─ 不能，必须走到节点时按当时数据算
   ├─ 能接受多一个流程步骤 + 秒级延迟 ──► B 外部任务取人（零改动，可观测）
   └─ 必须同步、立刻拿到
      ├─ 单人 ───────────────────────► C 监听器 / 审批人钩子（改宿主）
      └─ 多人 / 按组派单 ────────────► D 候选组 + IUserGroupManager（改宿主）
```

## 7. 源码索引

| 关注点 | 位置 |
| --- | --- |
| 三个属性的模型定义 | `src/NetBpm.Bpmn.Model/Activity.cs`（`UserTask.Assignee / CandidateUsers / CandidateGroups`） |
| 解析与写库 | `src/NetBpm.Engine/Behavior/TaskBehaviors.cs` → `ExecuteAsync` / `HandleAssignmentsAsync` |
| 审批链钩子 | `src/NetBpm.Engine/Tasks/ApprovalChain.cs`（`IApproverResolver` / `ApproverDeduplicationResolver`） |
| 钩子注册入口 | `src/NetBpm.Runtime/NetBpmServiceCollectionExtensions.cs`（`AddNetBpmApproverResolver` / `AddNetBpmApproverDeduplication`） |
| 身份适配器 | `src/NetBpm.Identity/IdentityContracts.cs`、`EmptyUserGroupManager.cs`、`src/NetBpm.Runtime/UserGroupManagerServiceCollectionExtensions.cs` |
| 候选组空实现告警 | `src/NetBpm.Workflow/Diagnostics/CandidateGroupGuard.cs` |
| 任务级身份关系 SQL | `src/NetBpm.Persistence/Tasks/DapperTaskManager.cs`（`FindTasksByCandidateUserAsync` / `FindTasksByCandidateGroupAsync`） |
| 候选查询组合 | `src/NetBpm.Runtime/ProcessEngine.cs` → `CollectTasksAsync` |
| 平台面视图与可见性 | `src/NetBpm.Rest/Platform/TaskCatalog.cs`（`BuildView` / `CanSubmit` / `ResolveScope`） |
| 发起参数 → 变量 | `src/NetBpm.Rest/Platform/InstanceStartService.cs` → `BuildVariables` |
| 提交参数 → 变量 | `src/NetBpm.Rest/Platform/TaskSubmitService.cs` → `BuildVariablesAsync` |
| 外部任务写变量 | `src/NetBpm.Engine/Command/CompleteExternalTaskCommand.cs` |
| EL 解析链与隐式对象 | `src/NetBpm.El/ElResolver.cs` |
| 身份查询端点 | `src/NetBpm.Rest/Endpoints/IdentityEndpoints.cs` |
| 引擎面身份关系端点 | `src/NetBpm.Rest/Endpoints/RuntimeEndpoints.cs`（`/tasks/{taskId}/identity-links`） |
