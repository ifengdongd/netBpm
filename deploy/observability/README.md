# 可观测性资产

看板与告警规则。指标由 NetBpm 用 OpenTelemetry 导出，**默认关闭**。

配套文档：`docs/operations-runbook.md`（值班动作、SLO、升级回滚）。

---

## 1. 打开导出

二选一，命中任一即启用（`OpenTelemetryExtensions.IsEnabled`）：

```bash
NetBpm__Otel__Enabled=true
# 或
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
```

只加 Meter 不加 Traces 也可以：`NetBpm:Otel:EnableTracing=false`（默认开）。
Meter 名：`NetBpm.Engine` / `NetBpm.Admin`。

---

## 2. 指标名规整

OTel 里的点号在 Prometheus 侧会被规整成下划线，Counter 还会加 `_total`：

| 代码里 | Prometheus 里 |
|---|---|
| `netbpm.command.executed` | `netbpm_command_executed_total` |
| `netbpm.command.duration` | `netbpm_command_duration_bucket` / `_sum` / `_count` |
| `netbpm.outbox.failure` | `netbpm_outbox_failure_total` |
| `netbpm.external_task.alert` | `netbpm_external_task_alert_total` |
| `netbpm.event.delivery` | `netbpm_event_delivery_total` |
| `netbpm.admin.authorization.denied` | `netbpm_admin_authorization_denied_total` |
| `netbpm.platform.surface.denied` | `netbpm_platform_surface_denied_total` |

标签（注意 `netbpm.` 前缀的标签名，OTel 会原样保留）：

| 指标 | 标签 |
|---|---|
| `command.executed` | `command` / `result` |
| `timer_job.failure` / `async_job.failure` / `outbox.failure` | `dead_letter` |
| `external_task.alert` | `code` / `tenant` |
| `event.delivery` | `subscription` / `result` |
| `admin.authorization.denied` | `netbpm.admin.scope` / `netbpm.auth.mode` |
| `admin.audit.recorded` | `netbpm.admin.action` / `netbpm.audit.success` |
| `platform.surface.denied` | `netbpm.admin.action` / `netbpm.auth.mode` |

三个告警码走**同一个计数器** `netbpm_external_task_alert_total`，用 `code` 区分：

- `external_task.no_worker` —— 登记后无人领取
- `external_task.dead_letter` —— 重试耗尽
- `event.delivery.dead_letter` —— 事件投递已停投

---

## 3. 文件

| 文件 | 用途 |
|---|---|
| `grafana-netbpm-overview.json` | 总览看板：引擎命令（速率 / p95 / 锁重试）、作业与 outbox 失败（区分死信）、外部任务告警、事件投递成功率、管理面拒绝 |
| `prometheus-alerts.yml` | 告警规则，按 `page` / `ticket` / `patrol` 三级严重度 |

导入看板：`Dashboards → New → Import`，选中 JSON；数据源变量 `${DS_PROMETHEUS}` 需要填你的 Prometheus UID。
告警规则挂到 `rule_files`，或转成 Grafana 的 `provisioning/alerting`。

严重度约定不是装饰：`page` 意味着**流程已经停了**，`ticket` 是**有量但没停**，`patrol` 是**接入方卫生问题**。
把 `patrol` 配成电话告警，两周后没人再看了。

---

## 4. 一个缺口，别假装没有

**outbox 积压没有指标**。`GET /admin/v1/events/stats` 才有：

```json
{ "outbox": { "pending": 12, "deadLetter": 0, "oldestPendingAt": "..." } }
```

它是端点数据，不是 Meter 数据 —— Prometheus 抓不到。要把它接进告警，两条路：

1. 用 blackbox exporter / 一个 5 行的 sidecar 定时打这个端点，把 `pending` 与
   `oldestPendingAt` 的年龄转成指标（推荐，改起来最快）；
2. 等 outbox 自己加 Gauge（那是代码改动，需要一个发版）。

**「积压」里最该盯的是 `oldestPendingAt` 的年龄**，不是 `pending` 的总数：
100 条积压里最老的一条只有 5 秒，那是正常吞吐；2 条积压但最老的一条卡了半小时，那是故障。
