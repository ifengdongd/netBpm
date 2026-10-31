# NetBpm 独立部署（S4）

「独立部署」指的是：**NetBpm 是一个独立容器服务，业务系统的代码不进它的进程**。
`<serviceTask activiti:topic="...">` 是这套形态的支点 —— 引擎执行到这种节点会原地停下、
把变量快照登记到 `nb_external_task`，等客户侧的 worker 拉走处理。

## 1. 一条命令跑起来（演示档）

```bash
cd E:/DataCode/NetBpm
docker compose -f deploy/docker-compose.yml up --build
```

起来后：

| 服务 | 地址 | 说明 |
| --- | --- | --- |
| platform | http://localhost:8080 | 平台服务（`/health` 探活、OpenAPI 在 `/openapi/v1.json`） |
| worker | 无端口 | 外部任务工作者，只出网不出端口 |
| mysql | localhost:3306 | 流程库 |
| redis | 无对外端口 | 作业队列（多副本时才真正起作用） |

> ⚠️ 演示档关闭了凭据校验（`NetBpm__Security__RequireCredential=false`），
> 任何能访问 8080 的人都能读写流程数据。**只允许在本机验证。**

## 2. 端到端演示：一条含 serviceTask 的流程

顺序很重要 —— 这条演示要证明的是「实现类不在引擎进程里」：

### ① 部署流程定义

```bash
curl -X POST http://localhost:8080/api/designer/deploy \
  -H "Content-Type: text/xml" \
  --data-binary @samples/NetBpm.ExternalWorker/processes/external-task-demo.bpmn20.xml
```

流程是 `开始 → 通知(notify) → 开票(invoice) → 复核(userTask) → 结束`，
两个中间环节都是外部任务。

### ② 发起实例

```bash
curl -X POST http://localhost:8080/api/runtime/process-instances \
  -H "Content-Type: application/json" \
  -d '{"processDefinitionKey":"externalTaskDemo","businessKey":"DEMO-1","variables":{"amount":1000}}'
```

此刻引擎**走到 `svc_notify` 就停了**：

```bash
curl "http://localhost:8080/api/runtime/external-tasks?topic=notify"
```

### ③ worker 处理（看日志）

```bash
docker compose -f deploy/docker-compose.yml logs -f worker
```

会看到「领到任务 → 完成，写回变量」。发票号与含税金额是 worker 算的：
`INV-20261031-<任务号>`、`invoiceTotal=1060`（1000 × 1.06）。

### ④ 复核环节出现

```bash
curl "http://localhost:8080/api/runtime/tasks?processInstanceId=<实例 id>"
```

出现「复核」待办，说明流程确实被 worker 推过了两个服务节点。

### ⑤ 演示失败与死信

发起时让 worker 一定失败（`simulateFailure` 是 worker 侧读的变量）：

```bash
curl -X POST http://localhost:8080/api/runtime/process-instances \
  -H "Content-Type: application/json" \
  -d '{"processDefinitionKey":"externalTaskDemo","businessKey":"DEMO-2","variables":{"amount":1000,"simulateFailure":true}}'
```

观察三件事（这是外部任务最容易被写错的地方）：

1. 重试按退避进行，**流程节点始终停在开票**，不会「看起来办完了」；
2. 重试耗尽后进 `DEAD_LETTER`，任务行仍然在（不是消失）；
3. 告警 `external_task.dead_letter` 由巡检发出；修好后管理员可重投：
   ```bash
   curl -X POST http://localhost:8080/api/runtime/external-tasks/<taskId>/retry \
     -H "Content-Type: application/json" -d '{}'
   ```

## 3. 生产档要改什么

演示档为了「一条命令能跑通」放宽了三处，生产必须收紧：

| 项 | 演示档 | 生产档 |
| --- | --- | --- |
| 凭据 | `RequireCredential=false` | `NetBpm__Security__RequireCredential=true` + `Clients__Enabled=true` |
| worker 身份 | 匿名 | `NetBpm__ApiKey=nbc.{clientId}.{secret}` |
| 建表 | `AutoMigrate=true` | 先跑 `tools/NetBpm.DbMigrate migrate`，再置 `false` |

### 第一个客户端从哪来（鸡生蛋）

服务端开了凭据就没人能调注册接口，因此第一次要用**管理端点**：
在尚未收紧凭据的窗口注册，或直接在库里执行 `tools/NetBpm.DbMigrate` 后由管理员通过
`POST /api/admin/clients` 创建 —— **`clientSecret` 只在响应里出现这一次**，之后只能看到后 4 位。

```bash
curl -X POST "http://localhost:8080/api/admin/clients?confirmPrivilegedScopes=true" \
  -H "Content-Type: application/json" \
  -d '{"name":"order-worker","tenantId":"default","scopes":["workflow:external:task"]}'
# → {"clientId":"cl_...","clientSecret":"nbc.cl_......."}
```

把 `clientSecret` 填进 worker 的 `NetBpm__ApiKey`，再重启 worker。

## 4. 多副本部署

`platform` 横向扩容前必须确认队列是 Redis：

```yaml
NetBpm__Jobs__Queue__Provider: "Redis"
```

用内存队列跑两个副本，同一个定时/异步作业会被两个实例各执行一次 ——
表现不是报错，而是「偶发的重复推进」，很难追。

worker 副本数随便加：外部任务的并发安全由 `nb_external_task` 的抢占式 `update` 保证，
同一条任务只会被一个 worker 领走。

## 5. 文件说明

| 文件 | 作用 |
| --- | --- |
| `Dockerfile` | 多阶段构建，`--build-arg PROJECT=` 决定构建平台还是 worker |
| `docker-compose.yml` | MySQL + Redis + platform + worker 的完整编排 |
| `../.dockerignore` | 必须在仓库根：build context 是仓库根，否则一次构建要先传几百兆 |

最容易改坏的一条：`platform` 的 healthcheck 打 `/health` 而不是 `/health/ready` ——
后者会连库，数据库抖动会让编排反复重启一个健康的实例。
