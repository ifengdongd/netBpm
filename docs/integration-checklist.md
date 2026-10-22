# 新项目引入 NetBpm：注意事项与接入清单

> 结论先给：NetBpm 是「库 + 可选宿主」，接进已有系统真正的成本不在 API，而在
> **组合根顺序、事务边界、BPMN 与业务委托的对应关系**这三处。下面按接入顺序组织，
> 末尾附症状速查表与勾选清单。每一条都来自真实集成（OPMS）踩过的坑。
>
> 适用版本：0.1.2+（`AddNetBpmUserGroupManager`、默认委托自检、`NetBpm:Admin:PathBase` 为该版本新增）。

---

## 0. 一页速查

| 决策点 | 必须确认 | 选错的后果 |
|---|---|---|
| 执行树 | 生产 `UseInMemoryExecutionTree = false` + `ConnectionString` | 宿主重启，运行中实例全丢 |
| 库 | `ACT_*` 与业务表**同库** | 业务写成功、流程不动（两个事务） |
| 身份 | `AddNetBpmUserGroupManager<T>()` | 候选组展开 0 人，待办静默消失 |
| 委托 | 每个 `delegateExpression` 都有已注册实现 | 部署成功，跑到该节点才 500 |
| 建表 | 执行初始化脚本，或 `AutoMigrate = true` | 启动即报表不存在 / schema 版本不符 |
| 定时器 | `Timer:PollIntervalMs` 是**毫秒**，`Jobs:PollIntervalSeconds` 是**秒** | 超时不触发 / 高频空转 |
| 部署 | 独立根路径 或 子路径反代（二选一，见 §5） | 资源 404、白屏、Cookie/重定向丢前缀 |
| 安全 | 至少配 `Security:ApiKey` 或 `Security:Jwt:SigningKey` | 未配置 = 开放模式，任何人都能调 |

---

## 1. 组合根：注册什么、按什么顺序

最小可运行组合（六步）：

```csharp
builder.Services.AddNetBpm(builder.Configuration);                  // ① 引擎（方言/连接串/历史开关）
builder.Services.AddNetBpmUserGroupManager<MyUserGroupManager>();   // ② 身份适配器（候选组 → 人）
builder.Services.AddNetBpmWorkflow<MyWorkflowDataStore>();          // ③ 审批应用层 + 业务数据端口
builder.Services.AddServiceTaskDelegates(typeof(Program).Assembly); // ④ 服务任务委托（[ServiceTaskName]）
builder.Services.AddNetBpmTimerScheduler(builder.Configuration);    // ⑤ 边界定时器轮询
builder.Services.AddNetBpmAsyncJobExecutor(builder.Configuration);  // ⑥ async 作业执行器（用到才需要）

// 端点：②③④ 之外，通用流程 HTTP 面与业务定制点
app.MapWorkflowEndpoints("/api/workflow", new WorkflowEndpointOptions { ProcessKey = _ => "my_process" });

// 启动即部署（自定义守卫走 VerifyAsync）
await WorkflowProcessBootstrapper.DeployAsync(app.Services, new WorkflowDeployOptions
{
    ResourcePath = "processes/my-process.bpmn20.xml",
    DeploymentName = "我的流程",
}, app.Logger);
```

**顺序上的三个硬约束**（踩过才知道）：

1. `AddNetBpmUserGroupManager<T>()` **必须存在**。`AddNetBpm` 默认注册空实现 ——
   靠 `Replace` + 默认实现用 `TryAdd`，调用在 `AddNetBpm` 前后都安全，但**不能省**。
   省掉的症状是「发起成功、流程停在第一个 userTask、所有候选人都看不到待办、日志无异常」。
2. `AddServiceTaskDelegates` 必须在**部署之前**（它只是扫描注册，但部署期自检要能解析到）。
3. 身份适配器**只做读**。它在引擎命令执行过程中被调用（展开候选组 / 查上下级），
   在里面写业务数据会脱离引擎事务；读别的库（AD / 权限中台）没问题，但要自带超时与缓存，
   否则一个慢查询会拖住整个审批命令。

> 不想用 DI 的宿主（自建服务图）：所有组件都接受显式构造，参考 `samples/NetBpm.Demo` 的
> 装配方式；但 `WorkflowProcessBootstrapper` 仍要求 `IServiceProvider`。

---

## 2. 数据与事务

### 2.1 同库是硬要求

引擎的强一致依赖「业务表 + `ACT_*` 在同一个事务里」。业务写入应通过**流程内的服务任务委托**
（如 `order.save`）完成，而不是「先调引擎、再调业务」的补偿式写法。

拆库的合法做法只有一种：业务侧提供同库的事务参与者，或把业务写入整体挪到引擎库。

### 2.2 执行树与历史开关

| `UseInMemoryExecutionTree` | `EnableHistory` | 结果 |
|---|---|---|
| `false`（默认推荐生产） | `true` | 正常：`ACT_RU_*` + `ACT_HI_*` 全量落库 |
| `false` | `false` | 正常：不留历史（`NullHistoryManager`） |
| `true`（默认值，POC/单测） | `false` | 正常：进程退出即清零 |
| `true` | `true` | **启动直接抛异常**（刻意设计：静默忽略历史开关只会掩盖配置错误） |

### 2.3 建表与版本校验

- 发行包**不带** SQL 脚本，因此 `AutoMigrate` 默认 `false`：先执行 `sql/71_netbpm_engine.sql`（幂等）。
- 保留 `ValidateSchemaOnStartup = true`：结构版本不符在**启动期**报，而不是运行期某个 SQL 报 Unknown column。
- 升级引擎版本时：先看 `DatabaseSchemaChecker.ExpectedSchemaVersion`，再决定是否重跑初始化脚本。

### 2.4 幂等与审计

- 变更类接口支持 `Idempotency-Key`：同键同请求回放首次响应，同键不同请求 409；
  数据库宿主用 `ACT_IDEMPOTENCY` 跨实例去重（内存宿主仅进程内）。
- 管理动作（转办 / 跳转 / 变量直写）写审计表，可通过 `/api/admin/audit` 回放。

---

## 3. BPMN 与业务委托

- 服务任务的 `activiti:class` / `activiti:delegateExpression` 指向**已注册**的委托；
  推荐 `[ServiceTaskName("order.ApproveComplete")]` + `AddServiceTaskDelegates(assembly)`。
- **0.1.2 起引导器默认做委托预解析**（`ServiceTaskDelegateGuard`）：解析不到直接让宿主起不来，
  异常里带节点 id 与写错的名字。只有「委托在运行时动态注册」的非常规宿主才设
  `SkipServiceTaskDelegateCheck = true`。
- 通过 REST 部署（`POST /api/designer/deploy`）走 `DeploymentDelegatePrecheck`，同样在响应里报出来，
  不会出现「部署成功、审批才炸」。
- **候选组空实现告警**（`CandidateGroupGuard`）：流程声明了 `candidateGroups` / `candidateUsers`
  但 `IUserGroupManager` 仍是空实现时，部署期打一条告警（只告警 —— 只写 `assignee` 的流程是合法的）。
- 业务强约束（审批选项与 C# 枚举一致、必填表单字段、节点命名规范）走
  `WorkflowDeployOptions.VerifyAsync`：`ApprovalFormGuard` 是现成范例。
  自检失败**故意**让宿主起不来 —— 审批是主链路，带病上线比启动失败更难查。

---

## 4. 运行期：定时器、异步作业、多宿主

| 配置 | 单位 | 默认 | 说明 |
|---|---|---|---|
| `NetBpm:Timer:PollIntervalMs` | **毫秒** | 5000 | 边界定时器（超时审批）轮询 |
| `NetBpm:Jobs:PollIntervalSeconds` | **秒** | 2 | `asyncBefore` / `asyncAfter` 作业轮询 |

- 单位填错的两种典型：把 `5`（秒）填进毫秒位 → 每 5ms 全表查询；把 `5000`（毫秒）填进秒位 →
  超时定时器 83 分钟才检查一次。0.1.2 起对 `< 200ms` 与 `>= 600s` 两种可疑取值在轮询启动时告警。
- **多宿主**：作业以条件删除抢占，不会重复执行；但 `Timer:Enabled` 建议只在**一个**宿主开启，
  其余宿主设 `false`，避免多份无效查询。
- **outbox**：`EnableOutbox = true` 且宿主注册了 `IOutboxEventHandler` 才会真正投递；
  否则事件只走进程内监听器（不落 `ACT_RU_EVENT` 之外的额外开销）。
- **探针**：`/health/live`（判断要不要重启）、`/health/ready`（数据库不可用返回 503）、
  `/api/admin/info`（版本 / 方言 / 历史开关 / 结构版本 / 运行时长）。

---

## 5. 部署形态

### 5.1 独立部署（控制台自己占一个域名/端口）

前端按默认 `base` 构建即可：管理端 `/`，设计器 `/designer/`。

### 5.2 子路径反代（挂到已有系统的 `/netbpm/` 下）

两种方案**二选一**，同时做会把前缀吃掉两遍：

| | 代理行为 | NetBpm 配置 | 前端构建 |
|---|---|---|---|
| A. 代理剥前缀 | `/netbpm/**` → `/**` 再转发 | 无需配置 | `npm run build:opms`（`base=/netbpm/`） |
| B. 宿主认领前缀 | `/netbpm/**` 原样转发 | `NetBpm:Admin:PathBase=/netbpm` | 同上 |

- 方案 B 由宿主 `UsePathBase`：路由、Cookie Path、重定向自动带前缀，并还原
  `X-Forwarded-Proto/For`（限流按真实客户端 IP 分区）。默认只信任本机（loopback）转发，
  反代在另一台机器/容器网络时需配置 `KnownProxies`。
- 设计器嵌在管理端里，前缀再深一层：`NETBPM_DESIGNER_BASE=/netbpm/designer/`
  （`build:opms` 已包含），否则模型编辑器弹窗空白。
- 控制台前端把令牌放在 `localStorage`（不是 Cookie），跨站 iframe 不会因 `SameSite` 丢登录态；
  但跨源仍需要正确的资源前缀与接口前缀（两者都由 `VITE base` 派生，别手写 `/api`）。

### 5.3 前端构建

```bash
cd web/admin    && npm run build        # 独立部署：base=/
cd web/admin    && npm run build:opms   # 子路径：base=/netbpm/
cd web/designer && npm run build        # base=/designer/
cd web/designer && npm run build:opms   # base=/netbpm/designer/
```

两个前端产物落在同一个 `src/NetBpm.Admin/wwwroot`（设计器在 `wwwroot/designer`）。
`emptyOutDir` 已关闭，管理端构建前由 `scripts/clean-dist.mjs` 清理 `wwwroot/assets` 与 `index.html`，
不碰 `wwwroot/designer`，所以先后顺序不敏感；习惯上先设计器、后管理端（管理端构建会清自己的旧资产）。

---

## 6. 安全

- **开放模式**：`NetBpm:Security:ApiKey` 与 `Security:Jwt:SigningKey` 都没配时，鉴权中间件放行
  （本地开发方便）。生产至少配一项。
- 管理端账号走 `admin_user` 表（`AddNetBpmAuth`）：PBKDF2 散列 + 失败锁定
  （`Security:Login:MaxFailedAttempts` / `LockoutMinutes`）。`Security:DevUsers` 只在**无库**模式生效，
  明文口令仅存于配置，启动时会告警。
- 令牌签名密钥 HS256 要求 ≥ 32 字节；登录接口与 `/api/auth/me` 探针各自独立限流分区。
- 内置占位委托（`NetBpm:Admin:BuiltInDelegates:Enabled`，默认 `true`）只为让控制台自带示例流程
  端到端可跑。业务宿主接入真实实现后设为 `false`，并确保委托**不同名** ——
  同名时按注册顺序命中其中一个，排查成本很高。

---

## 7. 症状速查表

| 症状 | 先查 |
|---|---|
| 发起成功，没人看到待办 | `IUserGroupManager` 是否仍是空实现（看部署期告警） |
| 部署成功，跑到某节点 500「无法解析委托」 | 委托是否 `AddServiceTaskDelegates` 注册、名字是否拼错 |
| 业务写成功，`ACT_RU_*` 不动 | 是否同库同事务；是否用了「先引擎后业务」的补偿写法 |
| 重启后实例全丢 | `UseInMemoryExecutionTree` 是否仍为 `true` |
| 启动报 `ACT_*` 表不存在 / schema 版本不符 | 是否执行初始化脚本；`ExpectedSchemaVersion` 是否匹配 |
| 超时审批永不触发 | `Timer:PollIntervalMs` 是否误填秒值（看启动告警） |
| 控制台白屏 / 资源 404 | 前端 base 与 `PathBase` / 代理剥前缀方案是否匹配（§5） |
| 反代后所有人共用一个限流桶 | 是否还原了 `X-Forwarded-For`（方案 B 已内建） |
| 未登录也能调接口 | 是否处于开放模式（`ApiKey` / `SigningKey` 都为空） |
| 一条定时器被触发多次 | 是否多个宿主都开了 `Timer:Enabled` |

---

## 8. 接入自检清单（发布前逐项打勾）

- [ ] `UseInMemoryExecutionTree = false` 且 `ConnectionString` 与业务库一致
- [ ] 初始化脚本已执行（或 `AutoMigrate = true`），`ValidateSchemaOnStartup = true`
- [ ] `AddNetBpmUserGroupManager<T>()` 已注册，且部署期无候选组空实现告警
- [ ] 所有 BPMN 委托可解析（启动自检通过 / REST 部署返回 200）
- [ ] `Timer` 只在一个宿主启用；`Timer` / `Jobs` 间隔单位核对过
- [ ] 前端按部署形态构建（`build` 或 `build:opms`），设计器前缀比管理端深一层
- [ ] `Security:ApiKey` 或 `Security:Jwt:SigningKey` 已配置，`DevUsers` 未在生产出现
- [ ] `BuiltInDelegates` 在业务宿主已关闭或确认不冲突
- [ ] `/health/ready` 返回 200，`/api/admin/info` 的 `schemaVersion` 与包版本匹配
- [ ] 发起 → 审批 → 结束的主链路在**测试库**跑通一遍（含一条边界定时器 / 一个 gateway 分支）
