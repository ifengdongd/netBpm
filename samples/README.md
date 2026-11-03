# samples

三个可以直接跑的样例宿主，以及两份接入操作指引。

| 目录 | 说明 | 运行 |
|---|---|---|
| [`NetBpm.Demo`](NetBpm.Demo/README.md) | 合并审批 Demo（订单 + 请假）：流程设计、发起、审批、业务回写、待办中心、任务转移、流程跳转；另带一个**外部任务闭环用例**（`POST /api/external-task/case/run`） | `dotnet run --project samples/NetBpm.Demo` → http://localhost:22613 |
| `NetBpm.ExternalWorker` | 外部任务 worker：拉取 `activiti:topic` 节点、回执完成/失败、超时回收与死信重投 | `dotnet run --project samples/NetBpm.ExternalWorker` |
| [`NetBpm.ServerTaskWorker`](NetBpm.ServerTaskWorker/README.md) | **外部系统一侧的完整形态**：3007 端口的业务接口 `POST /api/serverTask/{name}` + 幂等落账 + 后台 worker，请假流程开箱可跑 | `.\samples\NetBpm.ServerTaskWorker\run-worker.ps1` → http://localhost:3007/healthz |

## 从哪开始

- **要在平台上新建一条流程、发布它，并让结束分支执行一段业务代码（serviceTask）** →
  [在 NetBpm 平台新建流程并配置 serviceTask](process-authoring-guide.md)

这份指引覆盖：控制台建模入口、保存与发布两步的差别、三条发布途径、
serviceTask 的四种写法（委托三种 + 外部任务 `activiti:topic`）与注册方式、
部署期委托自检为什么"部署成功不等于能跑"、委托失败的语义与重试边界、排错手册。

- **不想先装 worker，只想确认"宿主里注册了 topic 之后流程能不能被推完"** →
  跑 Demo 自带的外部任务用例：`curl -X POST http://localhost:22613/api/external-task/case/run`
  （注册的 handler + 进程内 worker + 10 步断言，见 [Demo README](NetBpm.Demo/README.md#外部任务用例注册--调用自证闭环)）

- **要让引擎进程之外的系统（Go/Java/Python 都行）来执行某个 serviceTask 节点** →
  [配置外部任务 worker](external-task-worker-guide.md)

这份指引覆盖：`activiti:topic` 的正确写法（以及为什么别写 `activiti:type="external"`）、
平台侧存储与配置项、worker 的凭据/scope、fetch/complete/failure/unlock/retry 的实际契约、
锁与重试语义、排错手册与已知边界。

## 样例的分工

- `NetBpm.Demo` 演示**业务宿主**怎么接：自己的 `IBusinessProcess`、自己的 serviceTask 委托、
  自己的业务表，以及 `AddServiceTaskDelegates(typeof(Program).Assembly)` 的注册方式。
  流程定义不在 Demo 里维护，唯一来源是 `web/designer/examples/{order,leave}-process.bpmn20.xml`。
- `NetBpm.ExternalWorker` 演示**外部系统**怎么接的最小形态：不注册委托，只写 `activiti:topic`
  让外部进程领取任务，适合跨语言/跨团队的服务调用。
  注意**不要**写 `activiti:type="external"`：它不是本子集支持的属性，部署能过但会被忽略，
  只留一条 `unknown-activiti-attribute` 告警（见 [配置外部任务 worker](external-task-worker-guide.md)）。
- `NetBpm.ServerTaskWorker` 演示外部系统的**完整一侧**：同一个进程里既有业务接口
  `POST /api/serverTask/{name}`（外部系统按老办法调用），又有 worker 领取回执，
  两者共用一套处理器，不会出现「接口一套、worker 一套」的漂移。请假流程 `leave_process`
  开箱可跑，是 [`docs/leave-worker-3007-integration.md`](../docs/leave-worker-3007-integration.md) 的配套代码。
  用 `run-worker.ps1` 启动（而不是 `dotnet run`）：它先 publish 到 `artifacts/` 再从那里跑，
  这样 IDE 点「重新生成」时不会撞上「exe 正被另一进程使用」的文件锁。
- `NetBpm.Demo` 里另外藏了一个**外部任务闭环用例**（`ExternalTasks/` + `processes/external-task-case.bpmn20.xml`）：
  两个 handler 用 DI 注册、内置 worker 进程内领取回执，`POST /api/external-task/case/run` 一把跑完
  并逐步断言。它演示的是「注册 + 调用」的最小闭环，跨进程/跨语言仍以 `NetBpm.ExternalWorker` 为准。

选型建议：能在本进程内同步完成的业务回写用 serviceTask（委托）；
需要走外部系统、需要独立扩缩容或跨语言时用外部任务（external task）。
