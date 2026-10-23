/**
 * 与后端 DTO 一一对应的 TypeScript 契约（见 src/NetBpm.Admin/Contracts.cs 与
 * src/NetBpm.Rest/Contracts.cs）。字段命名保持后端 JSON 的驼峰形式。
 *
 * 🔍 契约参照：`npm run api:gen` 从运行中的宿主（`dotnet run --project src/NetBpm.Admin`）
 * 拉取 OpenAPI 文档生成 `./schema.d.ts`（全量 paths + schemas）。后端在 AddOpenApi 里用
 * schema transformer 修复了 STJ 的日期 / 数字弱化，因此字段类型（string / number / boolean）
 * 是准确的；但 STJ 默认不把非空属性标入 required，生成类型全字段可选——比手写契约更松。
 * 所以本文件保持手写强契约，schema.d.ts 用于比对后端 DTO 漂移（改了后端字段就 diff 一下）。
 */

// ─────────────────────────── 管理端 · 流程模型 ───────────────────────────

/** 发布状态：由同 key 的最新定义与版本历史推导（服务端只读）。 */
export interface BpmnModelPublishState {
  currentRevision?: number | null;
  publishedRevision?: number | null;
  latestPublishedVersion?: number | null;
  latestPublishedAt?: string | null;
  latestProcessDefinitionId?: string | null;
  /** 最后一次发布之后又保存过。 */
  hasUnpublishedChanges: boolean;
}

export interface BpmnModelSummary {
  id: string;
  name: string;
  processKey: string;
  description?: string | null;
  modifyTime: string;
  publish: BpmnModelPublishState;
}

export interface BpmnModelDetail {
  id: string;
  name: string;
  processKey: string;
  description?: string | null;
  bpmnXml: string;
  svg?: string | null;
  createTime: string;
  modifyTime?: string | null;
  publish: BpmnModelPublishState;
}

export interface SaveBpmnModelRequest {
  name: string;
  processKey: string;
  description?: string | null;
  bpmnXml: string;
  svg?: string | null;
}

/** 局部更新模型：只提交要改的字段（设计器保存用，避免 PUT 清空描述 / SVG）。 */
export interface PatchBpmnModelRequest {
  name?: string;
  processKey?: string;
  description?: string;
  bpmnXml?: string;
  svg?: string;
}

/** 校验问题（部署期 / 保存期同一口径）。 */
export interface ValidationIssue {
  rule: string;
  elementId?: string | null;
  elementType?: string | null;
  message: string;
}

/** 发布前预检结果（`POST /api/admin/models/{id}/validate`）。 */
export interface ModelValidationResponse {
  errors: ValidationIssue[];
  warnings: ValidationIssue[];
}

/** 保存模型结果（含新版本号与校验明细）。 */
export interface SaveBpmnModelResponse {
  id: string;
  revision: number;
  errors: ValidationIssue[];
  warnings: ValidationIssue[];
}

/** 一次模型发布记录。 */
export interface ModelPublication {
  revision: number;
  createdAt: string;
  operator?: string | null;
  processDefinitionId?: string | null;
  version?: number | null;
  /** 是否与线上定义唯一匹配（无法唯一匹配时 version 为 null）。 */
  linked: boolean;
}

/** 模型版本摘要（不含 XML 正文）。 */
export interface BpmnModelRevisionSummary {
  id: string;
  modelId: string;
  revision: number;
  name: string;
  processKey: string;
  description?: string | null;
  /** save / rollback / publish。 */
  action: string;
  operator?: string | null;
  createdAt: string;
}

/** 模型版本详情（含 BPMN XML / SVG 快照）。 */
export interface BpmnModelRevisionDetail extends BpmnModelRevisionSummary {
  bpmnXml: string;
  svg?: string | null;
}

/** 模型历史清理结果（`DELETE /api/admin/models/{id}/history`）。 */
export interface ModelHistoryPruneResponse {
  removed: number;
  modelId: string;
  keepLatest: number;
}

/** 部署（发布）响应：引擎侧解析 + 校验 + 版本递增的结果。 */
export interface DeployResponse {
  processDefinitionId: string;
  processDefinitionKey: string;
  version: number;
  deploymentId: string;
  resourceName: string;
  deploymentName?: string | null;
  /** 提示级问题（部署成功，但建议检查）；null 表示没有任何提示。 */
  warnings?: { rule: string; message: string }[] | null;
  /** true 表示内容与最后一次发布一致，本次跳过部署（回放线上版本）。 */
  skipped?: boolean;
}

// ─────────────────────────── 管理端 · 流程类型 ───────────────────────────

export interface ProcessType {
  id: string;
  name: string;
  describe: string;
  viewUrl: string;
  approveUrl: string;
  sortOrder: number;
  enabled: boolean;
  createTime: string;
  modifyTime?: string | null;
}

export interface SaveProcessTypeRequest {
  name: string;
  describe: string;
  viewUrl: string;
  approveUrl: string;
  sortOrder: number;
  enabled: boolean;
}

// ─────────────────────────── 部署与定义 ───────────────────────────

export interface ProcessDefinitionResponse {
  processDefinitionId: string;
  key: string;
  name?: string | null;
  version: number;
  deploymentId: string;
  tenantId: string;
  deployTime?: string | null;
  isSuspended?: boolean;
}

export interface DeploymentDefinitionResponse {
  processDefinitionId: string;
  key: string;
  name?: string | null;
  version: number;
  resourceName?: string | null;
  isSuspended: boolean;
}

/** 部署资源项（资源名 → 流程定义）。 */
export interface DeploymentResource {
  name?: string | null;
  processDefinitionId: string;
}

export interface DeploymentSummaryResponse {
  deploymentId: string;
  name?: string | null;
  deployTime?: string | null;
  tenantId?: string | null;
  definitions: DeploymentDefinitionResponse[];
  /** 该部署包含的流程定义数量。 */
  definitionCount?: number;
  /** 该部署内最高的定义版本号。 */
  latestVersion?: number | null;
  /** 该部署内已挂起的定义数量。 */
  suspendedCount?: number;
}

// ─────────────────────────── 运行时 · 任务表单 / 身份 ───────────────────────────

export interface FormPropertyValue {
  id?: string | null;
  name?: string | null;
}

/** `activiti:formProperty` 的只读元数据（引擎只回传声明，不渲染表单）。 */
export interface FormProperty {
  id?: string | null;
  name?: string | null;
  type?: string | null;
  variable?: string | null;
  expression?: string | null;
  default?: string | null;
  datePattern?: string | null;
  required?: boolean | null;
  readable?: boolean | null;
  writable?: boolean | null;
  values: FormPropertyValue[];
}

export interface FormMetadata {
  formKey?: string | null;
  activityId?: string | null;
  activityName?: string | null;
  formProperties: FormProperty[];
}

export interface UserGroup {
  id: string;
  name?: string | null;
  type?: string | null;
}

// ─────────────────────────── 运行时 · 实例 / 任务 ───────────────────────────

export interface ProcessInstanceResponse {
  id: string;
  processInstanceId: string;
  processDefinitionId: string;
  processDefinitionKey?: string | null;
  businessKey?: string | null;
  name?: string | null;
  startUserId?: string | null;
  startTime?: string | null;
  isActive: boolean;
  isSuspended: boolean;
  tenantId?: string | null;
  children: string[];
}

export interface TaskResponse {
  id: string;
  name?: string | null;
  taskDefinitionKey?: string | null;
  assignee?: string | null;
  owner?: string | null;
  processInstanceId?: string | null;
  processDefinitionKey?: string | null;
  executionId?: string | null;
  formKey?: string | null;
  priority: number;
  revision: number;
  createTime?: string | null;
  dueDate?: string | null;
  claimTime?: string | null;
  tenantId?: string | null;
}

export interface TaskStatsResponse {
  total: number;
  overdue: number;
  dueToday: number;
  byDefinition: Record<string, number>;
}

export interface BatchItemResult {
  id: string;
  success: boolean;
  error?: string | null;
}

export interface BatchResultResponse {
  total: number;
  succeeded: number;
  failed: number;
  items: BatchItemResult[];
}

// ─────────────────────────── 运行时 · 历史 / 详情 ───────────────────────────

export interface HistoricActivity {
  activityId: string;
  activityName?: string | null;
  activityType?: string | null;
  taskId?: string | null;
  assignee?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  isActive: boolean;
}

export interface HistoricTask {
  id: string;
  taskDefinitionKey?: string | null;
  processInstanceId?: string | null;
  processDefinitionId?: string | null;
  name?: string | null;
  assignee?: string | null;
  owner?: string | null;
  priority?: number | null;
  formKey?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  durationInMillis?: number | null;
  deleteReason?: string | null;
  tenantId?: string | null;
}

/** 流程图数据：BPMN XML + 活动高亮序列（只读实例视图一次取全）。 */
export interface Diagram {
  processInstanceId: string;
  processDefinitionId: string;
  bpmnXml?: string | null;
  isRunning: boolean;
  activities: HistoricActivity[];
}

/** 历史变量（审计：实例上出现过哪些变量、最后是什么值）。 */
export interface HistoricVariable {
  name: string;
  type?: string | null;
  value: unknown;
  executionId?: string | null;
  taskId?: string | null;
  createTime?: string | null;
  lastUpdatedTime?: string | null;
}

export interface HistoricProcessInstance {
  processInstanceId: string;
  businessKey?: string | null;
  processDefinitionId: string;
  name?: string | null;
  startUserId?: string | null;
  startActivityId?: string | null;
  endActivityId?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  durationInMillis?: number | null;
  deleteReason?: string | null;
  isRunning: boolean;
  tenantId?: string | null;
}

export interface IdentityLink {
  type: string;
  userId?: string | null;
  groupId?: string | null;
  taskId?: string | null;
  processInstanceId?: string | null;
}

export interface TaskDetail {
  id: string;
  name?: string | null;
  description?: string | null;
  taskDefinitionKey?: string | null;
  assignee?: string | null;
  owner?: string | null;
  processInstanceId?: string | null;
  processDefinitionId?: string | null;
  processDefinitionKey?: string | null;
  executionId?: string | null;
  businessKey?: string | null;
  formKey?: string | null;
  category?: string | null;
  priority: number;
  revision: number;
  createTime?: string | null;
  dueDate?: string | null;
  claimTime?: string | null;
  tenantId?: string | null;
  isSuspended: boolean;
  delegationState?: string | null;
  identityLinks: IdentityLink[];
}

/** 局部更新任务（`PATCH /api/runtime/tasks/{taskId}`）：只提交要改的字段。 */
export interface UpdateTaskRequest {
  name?: string;
  description?: string;
  priority?: number;
  /** ISO 8601 时间；清空到期时间用 `clearDueDate`。 */
  dueDate?: string;
  category?: string;
  owner?: string;
  clearDueDate?: boolean;
  clearDescription?: boolean;
  /** 乐观并发：与任务当前 revision 不一致时 409。 */
  expectedRevision?: number;
}

/** 运行态变量：键值对（值由服务端按变量类型解码）。 */
export type Variables = Record<string, unknown>;

/** 启动流程实例请求（`POST /api/runtime/process-instances`）。 */
export interface StartProcessInstanceRequest {
  /** 精确指定版本；与 `processDefinitionKey` 都给时以 id 为准。 */
  processDefinitionId?: string;
  /** 按 key 启动最新版本。 */
  processDefinitionKey?: string;
  businessKey?: string;
  /** 发起人；JWT 模式下必须与令牌 sub 一致。 */
  startUserId?: string;
  tenantId?: string;
  variables?: Variables;
}

/** 启动流程实例响应。 */
export interface StartProcessInstanceResponse {
  processInstanceId: string;
}

// ─────────────────────────── 通用 ───────────────────────────

export interface ErrorResponse {
  message: string;
  errors?: unknown[] | null;
  traceId?: string | null;
  code?: string | null;
}

export interface PagedResponse<T> {
  items: T[];
  total: number;
  firstResult: number;
  maxResults: number;
}

// ─────────────────────────── 管理治理 · 审计 ───────────────────────────

/** 一条管理动作审计（`GET /api/admin/audit`）。 */
/** 定时作业（边界定时器）。 */
export interface TimerJobSummary {
  id: string;
  processInstanceId: string;
  processDefinitionId?: string | null;
  activityId: string;
  boundaryEventId: string;
  dueDate: string;
  tenantId?: string | null;
  createTime: string;
}

/** 定时作业统计。 */
export interface TimerJobStats {
  total: number;
  overdue: number;
  dueWithinHour: number;
  nextDueAt?: string | null;
}

/** 审计筛选项（出现过的动作 / 目标类型 / 操作人）。 */
export interface AdminAuditOptions {
  actions: string[];
  targetTypes: string[];
  actors: string[];
}

export interface AdminAuditItem {
  id: string;
  tenantId?: string | null;
  actor?: string | null;
  authMode: string;
  action: string;
  targetType: string;
  targetId: string;
  processInstanceId?: string | null;
  /** 动作明细（JSON 字符串，不含变量值）。 */
  detail?: string | null;
  success: boolean;
  error?: string | null;
  createdAt: string;
}

/** 保留策略清理结果（`DELETE /api/admin/audit`）。 */
export interface AdminAuditPurgeResponse {
  removed: number;
  before: string;
  tenantId?: string | null;
}

/**
 * 管理指标（`GET /api/admin/metrics`）。
 *
 * `null` 表示「当前宿主无法统计」（例如内存宿主没有 `ACT_RU_TASK` 可聚合、归档未启用），
 * 而不是统计结果为 0 —— 界面应显示为「不可用」。
 */
export interface AdminMetricsResponse {
  generatedAt: string;
  tenantId?: string | null;
  auditWindowDays: number;
  runningProcessInstances: number;
  processDefinitions: number;
  historicProcessInstances: number;
  finishedProcessInstances: number;
  deletedProcessInstances: number;
  pendingTasks?: number | null;
  overdueTasks?: number | null;
  dueTodayTasks?: number | null;
  pendingTasksByProcessKey?: Record<string, number> | null;
  archivedInstances?: number | null;
  auditActions: AdminAuditActionCount[];
}

/** 窗口内按动作聚合的审计计数。 */
export interface AdminAuditActionCount {
  action: string;
  count: number;
}

// ─────────────────────────── 运维 · Redis 诊断 ───────────────────────────

/**
 * 一个队列键的当前状态（`GET /api/admin/redis`）。
 *
 * 键不存在时后端返回 `length: 0` —— 队列只存「作业到期提示」，作业执行完即删除，
 * 空键由 Redis 自动回收，所以「三个键都是 0」是正常状态，不是故障。
 */
export interface RedisQueueKeyInfo {
  /** 语义角色：delay（延迟段）/ ready（就绪段）/ inflight（在途段）。 */
  role: string;
  /** 完整键名（含 keyPrefix）。 */
  key: string;
  /** Redis 数据类型：zset / list。 */
  type: string;
  length: number;
}

/** Redis 服务端 INFO 摘要（只取诊断需要的几项）。 */
export interface RedisServerInfo {
  version?: string | null;
  mode?: string | null;
  uptimeSeconds?: number | null;
  connectedClients?: number | null;
  usedMemoryBytes?: number | null;
  totalCommandsProcessed?: number | null;
  keyspaceHits?: number | null;
  keyspaceMisses?: number | null;
  /** 各逻辑库的键数量，键名形如 `db1`。 */
  keyspace: Record<string, number>;
}

/**
 * Redis 作业队列诊断（`GET /api/admin/redis`）。
 *
 * 口令永远不会回传：连接串只以 `usesConnectionString` 布尔标记体现。
 * `isRedis=false` 表示当前宿主用的是内存队列，Redis 里不会有任何 netbpm 键。
 */
export interface AdminRedisInfoResponse {
  /** 实际生效的队列实现名：redis / memory。 */
  provider: string;
  isRedis: boolean;
  /** 目标地址（host:port，不含口令）；内存队列为 null。 */
  target?: string | null;
  /** 实际命中的逻辑库；连不上时为 null（连接串模式下只有连上才知道库号）。 */
  database?: number | null;
  keyPrefix?: string | null;
  ssl?: boolean | null;
  usesConnectionString: boolean;
  checkedAt: string;
  reachable: boolean;
  /** PING 往返时延（毫秒）。 */
  latencyMs?: number | null;
  error?: string | null;
  keys: RedisQueueKeyInfo[];
  server?: RedisServerInfo | null;
}
