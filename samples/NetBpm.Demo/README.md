# NetBpm.Demo · 合并审批 Demo（订单 + 请假）

一个宿主同时承载**两条审批流程**（订单审批 / 请假审批），演示流程设计、流程发起、
审批提交、通过/拒绝后的业务回写、待办中心、任务转移与流程跳转的完整后端处理。

- 流程定义唯一来源：`web/designer/examples/{order,leave}-process.bpmn20.xml`（设计器导出的 XML，csproj 链接进输出目录，启动即部署）；
- 页面不写死任何流程与审批选项：流程清单来自流程信息枚举，审批选项来自 BPMN 表单契约。

## 运行

```bash
dotnet run --project samples/NetBpm.Demo
# 打开 http://localhost:5188（首页即流程发起入口；OpenAPI 见 /openapi/v1.json）
```

**存储落真库（默认 SQL Server 10.1.245.36，Windows 身份认证，库 `netbpm_demo`；可切回 MySQL）**：
方言随 `NetBpm:DatabaseType`，引擎落 ACT_* 表（`UseInMemoryExecutionTree=false`），
业务数据落 `instances` / `instance_current_task` / `deal_history` / `workflow_parameters` + `order_bill`
（MySQL 无 schema 前缀；SQL Server 业务表在 `wf` schema）。启动时自动建库（若缺）并幂等执行
`db/<dialect>` 全部脚本，重复启动跳过。
⚠️ 引擎与业务数据是一体的 —— 内存执行树的流程实例 id 每次重启从 1 重新开始，会与真库累积行撞号。

前置脚本（按序执行，现由启动序列自动执行）：`db/<dialect>/0001 → 0002 → … → 0008`。

## 流程设计

### 订单审批流程（`order_process`，类型 id `02830358-46ef-40e5-b3b9-4d5c4eb23f9f`）

```
提交订单申请(start)
  → 经理审批(userTask, candidateGroups=managers, formKey=order-approve)
      表单：approvalResult(enum: 1通过/2拒绝/3驳回修改, variable=result, required) + comment
  → 排他网关 gw_manager（default = 驳回修改）
      ${result == 1} → 审批通过(serviceTask, delegateExpression=order.ApproveComplete) → 审批通过结束
      ${result == 2} → 拒绝结束(serviceTask, order.RefuseComplete)                      → 审批拒绝结束
      驳回修改(default) → 申请人修改(userTask, assignee=${applyUser}, 表单 amount/remark)
                          → 修改后重提 → 经理审批（订单保持审批中）
```

### 请假审批流程（`leave_process`，类型 id `3a7c2f14-8d52-4b6e-9a01-c5d84f6e21b7`）

```
提交请假申请(userTask, assignee=${applyUser}, 表单 days(long, required)/reason)
  → 组长审批(userTask, candidateGroups=leaders, 表单 approvalResult/comment)
      ${days > 3} → 经理审批(userTask, managers, approvalResult/comment)
      default     → 审批通过(serviceTask, leave.ApproveComplete)  /  拒绝走 leave.RefuseComplete
```

### 流程变量（BaseModel，扁平变量）

| 时点 | 变量 |
|---|---|
| 发起时（宿主写入） | `applyUser` / `applyTime` / `businessId` / `businessDeptId` / `applyDeptId` / **`workflowTypeId`** |
| 流程参数（paraDict） | 订单：`product_name` / `amount` / `remark`；请假：`days` / `reason` |
| 审批时（表单写入） | `result` / `comment` |

⚠️ 变量名是流程契约：BPMN 表单属性、网关条件、`BaseModelVariables` 三处必须一致。

## 流程信息枚举（instanceEnum）

`Common/ProcessInfo.cs`：`ProcessInfoEnum.All` 每项五个字段 ——
`Id`（流程类型 id / 设计器 modelId）、`Name`（流程 key）、`Describe`（描述）、
`ViewUrl`（审批结束后业务单查看地址，含 `{businessId}` 占位符）、`ApproveUrl`（审批页地址，含 `{taskId}` 占位符）。
端点 `GET /api/processes` 暴露给前端。

**数据源是 `workflow_type` 表（0008）**：系统启动时由
`WorkflowTypeLoader`（方言随 `NetBpm:DatabaseType`）加载进 `ProcessInfoEnum` ——
**新增流程 = 表里插一行**，不用改代码。
表不存在 / 加载失败（如 JSON 演示模式）回退内置默认清单；`name` 必须与 BPMN 流程 key 一致；
`describe` 在 MySQL 是保留字（反引号）、SQL Server 方括号引用，SQL 常量已按方言分开。

## 后端处理

### 组合根（Program.cs）

```
AddNetBpm(configuration)                       引擎（真库 ACT_* 表，方言随 DatabaseType）
AddDemoStores(configuration)                   业务数据三端口（按 DatabaseType 选 MySql/SqlServer 实现）
AddSingleton<IBusinessProcess, …>              业务流程接入（Order / Leave 各一个实现）
AddSingleton<InMemoryOrgDirectory>             IOrgDirectory + IUserGroupManager（同一份数据两个视角）
AddServiceTaskDelegates(typeof(Program).Assembly)   [ServiceTaskName] 扫描注册收口委托
MapBusinessProcess(process)                    统一业务入口：POST {ApiPrefix}/business/start
StartupInitializer.InitializeAsync(app)        启动序列：建库迁移 → 部署全部已注册流程 → 加载流程信息 → 发布流程图
```

### 新增业务流程（三步接入）

后续新建业务流程**不需要再写端点、部署或页面逻辑** —— 实现 `Workflow/IBusinessProcess`
接口即可获得全部流程能力（发起 / 实例 / 审批 / 历史 / 流程图 / 待办中心 / 部署列表自动纳入）：

1. **实现 `IBusinessProcess`**：四个标识属性（`ProcessKey` / `ResourceName` / `DeploymentName` /
   `ApiPrefix` / `WorkflowTypeId`）+ `PrepareStart`（业务单号发号、业务数据落库、流程参数组装）
   + `OnStartedAsync`（回填等收尾，可省略）；在 Program 注册 `AddSingleton<IBusinessProcess, …>`；
2. **两个审批收口委托**：派生 `Delegates/ApprovalCompletionDelegate`，实现
   `OnApprovalCompletedAsync`（把「通过 / 拒绝」翻译成业务变更），标 `[ServiceTaskName("xx.ApproveComplete")]`
   并与 BPMN 的 `delegateExpression` 对应；
3. **BPMN 定义 + workflow_type 表插一行**（id / name / describe / view_url / approve_url）。

参照实现：`Order/OrderBusinessProcess.cs`（有业务单表 + 状态回写）与
`Leave/LeaveBusinessProcess.cs`（无业务单表，参数落流程参数表）。

### 审批结束后的业务回写（拒绝/通过的数据处理）

- **订单**：`OrderCompletionDelegate` 基类 —— 先按流程变量 `businessId` 找到订单行、
  回写 `order_bill.status`（1=审批中，100=通过，-1=拒绝）与最终审批意见，再调
  `IWorkflowService.CompleteAsync` 落审批结论（顺序保证：业务事实先落，结论留痕可重试）；
- **请假**：`WorkflowCompletionDelegate` 派生两个委托（`leave.ApproveComplete` / `leave.RefuseComplete`），
  每个只剩「类名 + 结论」一行，落盘逻辑统一在 `CompleteAsync`。

### 发起时的流程类型 id（workflow_type_id）

`StartWorkflowRequest.WorkflowTypeId`（框架契约）→ `BaseModel.Start` → 实例登记。
Demo 在业务入口自动填 `ProcessInfoEnum` 的 GUID；
`instances.workflow_type_id`（MySQL `varchar(64)` / SQL Server `nvarchar(64)`）写入该值，
兜底用流程定义 key；同时落扁平变量 `workflowTypeId`。

### 业务表（netbpm_demo；MySQL 无前缀 / SQL Server 在 wf schema）

| 表 | 职责 |
|---|---|
| `instances` | 实例登记：申请人 / 业务单 / 状态 / 当前环节 / `workflow_type_id` |
| `instance_current_task` | 当前待办（每到达一个环节整体替换） |
| `deal_history` | 审批流水（每次提交追加；`deal_result`=BPMN 选项 id） |
| `workflow_parameters` | 流程参数（0007 新增：「实例 × 参数名」键值表，两条流程共用） |
| `workflow_type` | 流程类型表（0008：ProcessInfoEnum 的数据源，启动时加载） |
| `order_bill` | 订单业务行（`status` 随审批回写） |
| `ACT_*` | 引擎 13 张表（执行树 / 任务 / 变量 / 历史 / 部署） |

### HTTP 端点

- 业务入口：`POST /api/order/bills`（建单+发起+回填实例 id）、`POST /api/leave/instances`（自动发号）；
- 流程通用（`/api/{order|leave}`）：`instances` / `instances/{id}` / `history` / `tasks` / `tasks/{id}/submit` / `tasks/{id}/assignee`（转办） / `instances/{id}/jump`（跳转） / `process-nodes` / `process-definition`；
- 跨流程合并（`/api`）：`GET processes` / `GET tasks?candidateUser=` / `GET tasks/{taskId}` / `GET instances?applicant=` / `GET org/*`。

### 流程图与流转状态

`BpmnDiagramSvgRenderer`（`NetBpm.Workflow/Diagram`）把**已发布**流程定义的 BPMN XML 渲染成
SVG 流程图图片 —— 图形信息取文件里的 `bpmndi:BPMNDiagram`（设计器导出自带），
节点（事件=圆 / 网关=菱形 / 任务=圆角矩形）与连线按 DI 定位绘制，零第三方依赖。

三个入口（每条流程自动获得）：

| 端点 | 说明 |
|---|---|
| `GET /api/{prefix}/process-diagram.svg` | 发布定义的流程图图片；`?definitionId=` 可看任意**历史版本**（部署列表的链接指向这里，校验定义属于本流程防串图） |
| `GET /api/{prefix}/instances/{id}/diagram.svg` | **流转状态图**：走过的节点=红框、走过的线=红线（两端都已流转的顺序流），当前正停的环节=加粗红框+浅红填充；数据来自审批历史（`ActivityId` / `IsActive`），办结后全图标红到结束事件 |
| `GET /api/{prefix}/process-deployments` | 流程部署列表：每个部署一行（含历史版本，按部署时间倒序），每项带 `DiagramUrl` 指向该版本的流程图 |

发布即生成：部署完成后 Demo 把当前定义的流程图落到 `wwwroot/diagrams/{key}.svg`，静态可直接访问。
页面入口：审批页 / 业务单页的「流转状态图」链接；`deployments.html` 的部署列表逐行带「查看流程图」链接（含历史版本）。

### 页面（wwwroot，占位符由流程信息枚举的 viewUrl/approveUrl 渲染）

| 页面 | 用途 |
|---|---|
| `index.html` | 流程信息枚举展示、流程发起（订单/请假动态表单）、我的申请 |
| `tasks.html` | 待办任务列表（跨流程合并） |
| `approve.html` | 流程审批页（表单契约渲染 + 提交 + 历史 + 流转状态图） |
| `bill.html` | 业务单查看页（订单行 / 请假参数 + 审批历史 + 流转状态图） |
| `deployments.html` | 流程部署列表（含历史版本，逐行带查看流程图链接） |
| `transfer.html` | 待办任务转移（转办 / 退回未分配） |
| `jump.html` | 流程跳转（目标节点来自 `process-nodes`） |

## 目录结构

```
NetBpm.Demo/
├── Program.cs            薄组合根：注册框架与存储、映射端点、执行启动序列
├── Common/               ConstField 统一常量；流程信息枚举（workflow_type 加载）；
│                         流程端点 / 跨流程合并端点
├── Workflow/             两条流程共用的公共层：
│                         StartupInitializer（启动序列：部署→加载流程信息→发布流程图）、
│                         ProcessBootstrapper（参数化启动即部署 + 部署期自检）、
│                         BusinessKeys（业务单号发号器）、ProcessListeners（流程监听器）、
│                         ErrorResponse（统一错误响应）
├── Delegates/            服务任务委托（审批结束的业务回写）：leave.* 收口 / order.* 状态回写
├── Order/                订单契约 / JSON 业务存储 / 端点
├── Leave/                请假契约 / 端点
├── Storage/              双方言业务存储（逻辑下沉基类，方言收敛 SQL 常量层）：
│                         WorkflowDataStoreBase（四张流程表全部端口逻辑）+
│                         WorkflowStoreDialect（IWorkflowStoreDialect + MySQL/SqlServer 两个实现）+
│                         WorkflowStoreRows（共用行映射）；
│                         DataAccess/（公共数据操作接口：IDataAccessor 五件套 —— 按条件查列表 /
│                         按 id 查明细 / 新增返回 id / 修改返回影响行数 / 伪删除改状态列；
│                         DataAccessorBase 公共拼装 + MySql/SqlServerDataAccessor 两个方言实现；
│                         EntityTableSpec 列白名单 + IEntityMapper 实体映射 + OrderBillMapper 示范）；
│                         OrderBillStoreBase（订单 CRUD 委托 IDataAccessor）；
│                         四个薄子类：MySql/SqlServerWorkflowDataStore、MySql/SqlServerOrderBillStore；
│                         DemoSchemaInitializer（建库+迁移）；AddDemoStores 按 DatabaseType 注册
└── wwwroot/              七个独立页面 + diagrams/（发布生成的流程图）+ 共享 css/js
```
