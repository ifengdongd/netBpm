# samples

两个可以直接跑的样例宿主，以及一份建模/接入操作指引。

| 目录 | 说明 | 运行 |
|---|---|---|
| [`NetBpm.Demo`](NetBpm.Demo/README.md) | 合并审批 Demo（订单 + 请假）：流程设计、发起、审批、业务回写、待办中心、任务转移、流程跳转 | `dotnet run --project samples/NetBpm.Demo` → http://localhost:5188 |
| `NetBpm.ExternalWorker` | 外部任务 worker：拉取 `externalTask` 节点、回执完成/失败、超时重投 | `dotnet run --project samples/NetBpm.ExternalWorker` |

## 从哪开始

- **要在平台上新建一条流程、发布它，并让结束分支执行一段业务代码（serviceTask）** →
  [在 NetBpm 平台新建流程并配置 serviceTask](process-authoring-guide.md)

这份指引覆盖：控制台建模入口、保存与发布两步的差别、三条发布途径、serviceTask 的三种写法与注册方式、
部署期委托自检为什么"部署成功不等于能跑"、委托失败的语义与重试边界、排错手册。

## 两个样例的分工

- `NetBpm.Demo` 演示**业务宿主**怎么接：自己的 `IBusinessProcess`、自己的 serviceTask 委托、
  自己的业务表，以及 `AddServiceTaskDelegates(typeof(Program).Assembly)` 的注册方式。
  流程定义不在 Demo 里维护，唯一来源是 `web/designer/examples/{order,leave}-process.bpmn20.xml`。
- `NetBpm.ExternalWorker` 演示**外部系统**怎么接：不注册委托，用 `activiti:type="external"` +
  `topic` 让外部进程领取任务，适合跨语言/跨团队的服务调用。

选型建议：能在本进程内同步完成的业务回写用 serviceTask（委托）；
需要走外部系统、需要独立扩缩容或跨语言时用外部任务（external task）。
