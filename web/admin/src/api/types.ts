/**
 * 与后端 DTO 一一对应的 TypeScript 契约（见 src/NetBpm.Admin/Contracts.cs 与
 * src/NetBpm.Rest/Contracts.cs）。字段命名保持后端 JSON 的驼峰形式。
 */

// ─────────────────────────── 管理端 · 流程模型 ───────────────────────────

export interface BpmnModelSummary {
  id: string;
  name: string;
  processKey: string;
  description?: string | null;
  modifyTime: string;
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
}

export interface SaveBpmnModelRequest {
  name: string;
  processKey: string;
  description?: string | null;
  bpmnXml: string;
  svg?: string | null;
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

/** 运行态变量：键值对（值由服务端按变量类型解码）。 */
export type Variables = Record<string, unknown>;

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
