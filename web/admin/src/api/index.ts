import { http, list, newIdempotencyKey, toQueryString } from './client';
import type {
  AdminAuditItem,
  AdminAuditOptions,
  AdminAuditPurgeResponse,
  AdminMetricsResponse,
  BpmnModelDetail,
  BpmnModelSummary,
  BpmnModelRevisionDetail,
  BpmnModelRevisionSummary,
  BatchResultResponse,
  DeployResponse,
  DeploymentSummaryResponse,
  DeploymentResource,
  Diagram,
  FormMetadata,
  HistoricActivity,
  HistoricProcessInstance,
  HistoricTask,
  HistoricVariable,
  ModelHistoryPruneResponse,
  ModelPublication,
  ModelValidationResponse,
  PatchBpmnModelRequest,
  ProcessDefinitionResponse,
  ProcessInstanceResponse,
  ProcessType,
  SaveBpmnModelRequest,
  SaveBpmnModelResponse,
  SaveProcessTypeRequest,
  StartProcessInstanceRequest,
  StartProcessInstanceResponse,
  TaskDetail,
  TaskResponse,
  TaskStatsResponse,
  TimerJobStats,
  TimerJobSummary,
  UpdateTaskRequest,
  UserGroup,
  Variables,
} from './types';

// ─────────────────────────── 流程模型 ───────────────────────────

export const modelsApi = {
  /** 分页查模型列表（envelope 信封；`keyword` 模糊匹配名称 / 流程 Key / 描述）。 */
  list: (params?: { keyword?: string; firstResult?: number; maxResults?: number }, signal?: AbortSignal) =>
    list<BpmnModelSummary>('/api/admin/models', params, { signal }),
  get: (id: string) => http.get<BpmnModelDetail>(`/api/admin/models/${encodeURIComponent(id)}`),
  /** 新建模型：id 由后端生成，返回初始版本与校验问题。 */
  create: (request: SaveBpmnModelRequest) =>
    http.post<SaveBpmnModelResponse>('/api/admin/models', request),
  /** 导入 BPMN XML：后端从 XML 推导 process key / 名称。 */
  importXml: (xml: string, name?: string) =>
    http.postXml<SaveBpmnModelResponse>(
      `/api/admin/models/import${toQueryString({ name })}`,
      xml,
    ),
  /** 基于已部署定义反建模型：把线上版本拉回设计器。 */
  fromDefinition: (processDefinitionId: string, name?: string) =>
    http.post<SaveBpmnModelResponse>(
      `/api/admin/models/from-definition/${encodeURIComponent(processDefinitionId)}${toQueryString({ name })}`,
    ),
  /** 复制模型（副本从版本 1 开始）。 */
  duplicate: (id: string, name?: string) =>
    http.post<SaveBpmnModelResponse>(
      `/api/admin/models/${encodeURIComponent(id)}/duplicate`,
      { name },
    ),
  /** 全量保存（upsert）：未提交的字段会被清空，设计器保存请用 `patch`。 */
  save: (id: string, request: SaveBpmnModelRequest) =>
    http.put<SaveBpmnModelResponse>(`/api/admin/models/${encodeURIComponent(id)}`, request),
  /** 局部保存：只覆盖提供的字段，避免清空描述 / SVG。 */
  patch: (id: string, request: PatchBpmnModelRequest) =>
    http.patch<SaveBpmnModelResponse>(`/api/admin/models/${encodeURIComponent(id)}`, request),
  /** 发布前预检：结构化阻断 / 提示问题，不落库、不改版本。 */
  validate: (id: string) =>
    http.post<ModelValidationResponse>(`/api/admin/models/${encodeURIComponent(id)}/validate`),
  /** 发布历史（publish 快照 + 就近匹配的线上定义）。 */
  publications: (id: string) =>
    http.get<ModelPublication[]>(`/api/admin/models/${encodeURIComponent(id)}/publications`),
  remove: (id: string) => http.del<void>(`/api/admin/models/${encodeURIComponent(id)}`),

  /** 版本历史（分页；不含 XML 正文，按版本号倒序）。默认取最近 200 版。 */
  history: (id: string, params?: { firstResult?: number; maxResults?: number }) =>
    list<BpmnModelRevisionSummary>(
      `/api/admin/models/${encodeURIComponent(id)}/history`,
      { maxResults: 200, ...params },
    ),

  /** 版本详情（含 BPMN XML 快照）：版本历史里查看该版本的流程图用。 */
  revision: (id: string, revisionId: string) =>
    http.get<BpmnModelRevisionDetail>(
      `/api/admin/models/${encodeURIComponent(id)}/history/${encodeURIComponent(revisionId)}`,
    ),

  /** 回滚到指定版本：该版本内容作为新版本写入（历史只增不删）。 */
  rollback: (id: string, revisionId: string) =>
    http.post<BpmnModelDetail>(
      `/api/admin/models/${encodeURIComponent(id)}/rollback/${encodeURIComponent(revisionId)}`,
    ),

  /** 清理历史：只保留最近 `keepLatest` 个版本（最新版本永远保留）。 */
  pruneHistory: (id: string, keepLatest: number) =>
    http.del<ModelHistoryPruneResponse>(
      `/api/admin/models/${encodeURIComponent(id)}/history?keepLatest=${keepLatest}`,
    ),

  /**
   * 部署（发布）模型：引擎侧解析 + 校验 + 版本递增；阻断级问题 400（message 带校验明细）。
   * `svg` 为部署前渲染的流程图图片 —— 提供时后端把当前 XML 连同图片写入 `publish` 版本快照。
   */
  publish: (id: string, svg?: string, idempotencyKey?: string, skipIfUnchanged = true) =>
    http.post<DeployResponse>(
      `/api/admin/models/${encodeURIComponent(id)}/publish`,
      { svg, skipIfUnchanged },
      { headers: { 'Idempotency-Key': idempotencyKey ?? newIdempotencyKey() } },
    ),
};

// ─────────────────────────── 流程类型 ───────────────────────────

export const processTypesApi = {
  list: () => http.get<ProcessType[]>('/api/admin/process-types'),
  get: (id: string) => http.get<ProcessType>(`/api/admin/process-types/${encodeURIComponent(id)}`),
  create: (request: SaveProcessTypeRequest) => http.post<ProcessType>('/api/admin/process-types', request),
  update: (id: string, request: SaveProcessTypeRequest) =>
    http.put<void>(`/api/admin/process-types/${encodeURIComponent(id)}`, request),
  remove: (id: string) => http.del<void>(`/api/admin/process-types/${encodeURIComponent(id)}`),
};

// ─────────────────────────── 部署与定义 ───────────────────────────

export const deploymentsApi = {
  list: (params?: { keyword?: string; firstResult?: number; maxResults?: number; tenantId?: string }) =>
    list<DeploymentSummaryResponse>('/api/designer/deployments', params),

  versions: (key: string, params?: { firstResult?: number; maxResults?: number }) =>
    list<ProcessDefinitionResponse>(`/api/designer/definitions/${encodeURIComponent(key)}/versions`, params),

  definitionXml: (processDefinitionId: string) =>
    http.get<string>(`/api/designer/definition/${encodeURIComponent(processDefinitionId)}/xml`),

  resources: (deploymentId: string) =>
    http.get<DeploymentResource[]>(`/api/designer/deployments/${encodeURIComponent(deploymentId)}/resources`),

  suspend: (processDefinitionId: string, suspendProcessInstances?: boolean) =>
    http.post<void>(
      `/api/designer/definitions/${encodeURIComponent(processDefinitionId)}/suspend${suspendProcessInstances ? '?suspendProcessInstances=true' : ''}`,
    ),

  activate: (processDefinitionId: string, activateProcessInstances?: boolean) =>
    http.post<void>(
      `/api/designer/definitions/${encodeURIComponent(processDefinitionId)}/activate${activateProcessInstances ? '?activateProcessInstances=true' : ''}`,
    ),

  removeDeployment: (deploymentId: string, cascade?: boolean) =>
    http.del<void>(`/api/designer/deployments/${encodeURIComponent(deploymentId)}${cascade ? '?cascade=true' : ''}`),
};

// ─────────────────────────── 运行时 · 实例与任务 ───────────────────────────

export interface InstanceQuery {
  firstResult?: number;
  maxResults?: number;
  processDefinitionKey?: string;
  processDefinitionId?: string;
  businessKey?: string;
  startUserId?: string;
  tenantId?: string;
}

export interface TaskQuery {
  firstResult?: number;
  maxResults?: number;
  assignee?: string;
  candidateUser?: string;
  candidateGroup?: string;
  processInstanceId?: string;
  processDefinitionId?: string;
  taskDefinitionKey?: string;
  tenantId?: string;
}

export const runtimeApi = {
  // 运行实例
  /**
   * 启动流程实例：`processDefinitionId` 精确指定版本，只给 key 时启动最新版本。
   * 控制台「部署管理」的发起入口用 id，保证发起的就是当前看到的那一版。
   */
  startInstance: (request: StartProcessInstanceRequest, idempotencyKey?: string) =>
    http.post<StartProcessInstanceResponse>('/api/runtime/process-instances', request, {
      headers: { 'Idempotency-Key': idempotencyKey ?? newIdempotencyKey() },
    }),

  instances: (params?: InstanceQuery, signal?: AbortSignal) =>
    list<ProcessInstanceResponse>('/api/runtime/process-instances', params, { signal }),

  instance: (processInstanceId: string) =>
    http.get<ProcessInstanceResponse>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}`),

  suspendInstance: (processInstanceId: string) =>
    http.post<void>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/suspend`),

  activateInstance: (processInstanceId: string) =>
    http.post<void>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/activate`),

  jumpInstance: (processInstanceId: string, targetActivityId: string, reason?: string) =>
    http.post<void>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/jump`, {
      targetActivityId,
      reason,
    }),

  removeInstance: (processInstanceId: string, deleteReason?: string) =>
    http.del<void>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}${deleteReason ? `?deleteReason=${encodeURIComponent(deleteReason)}` : ''}`),

  instanceVariables: (processInstanceId: string) =>
    http.get<Variables>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/variables`),

  diagram: (processInstanceId: string) =>
    http.get<Diagram>(`/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/diagram`),

  batchDeleteInstances: (processInstanceIds: string[], deleteReason?: string) =>
    http.post<BatchResultResponse>('/api/runtime/process-instances/batch/delete', {
      processInstanceIds,
      deleteReason,
    }),

  /** 批量挂起 / 激活（逐项执行，返回逐项结果）。 */
  batchSuspendInstances: (processInstanceIds: string[]) =>
    http.post<BatchResultResponse>('/api/runtime/process-instances/batch/suspend', {
      processInstanceIds,
    }),

  batchActivateInstances: (processInstanceIds: string[]) =>
    http.post<BatchResultResponse>('/api/runtime/process-instances/batch/activate', {
      processInstanceIds,
    }),

  /** 批量迁移到同一目标定义版本（逐项执行；迁移不可逆，控制台暂未提供入口）。 */
  batchMigrateInstances: (
    processInstanceIds: string[],
    targetProcessDefinitionId: string,
    reason?: string,
  ) =>
    http.post<BatchResultResponse>('/api/runtime/process-instances/batch/migrate', {
      processInstanceIds,
      targetProcessDefinitionId,
      reason,
    }),

  /** 批量改任务到期时间（逐项执行）；`clearDueDate=true` 时清空到期时间。 */
  batchSetTaskDueDate: (taskIds: string[], dueDate?: string, clearDueDate = false) =>
    http.post<BatchResultResponse>('/api/runtime/tasks/batch/due-date', {
      taskIds,
      dueDate,
      clearDueDate,
    }),

  // 历史
  historicActivities: (processInstanceId: string) =>
    http.get<HistoricActivity[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/activities`),

  historicTasks: (processInstanceId: string) =>
    http.get<HistoricTask[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/tasks`),

  historicInstance: (processInstanceId: string) =>
    http.get<HistoricProcessInstance>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}`),

  historicVariables: (processInstanceId: string) =>
    http.get<HistoricVariable[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/variables`),

  historicInstances: (params?: InstanceQuery & { finished?: boolean; deleted?: boolean }, signal?: AbortSignal) =>
    list<HistoricProcessInstance>('/api/runtime/history/process-instances', params, { signal }),

  // 任务
  tasks: (params?: TaskQuery, signal?: AbortSignal) =>
    list<TaskResponse>('/api/runtime/tasks', params, { signal }),

  task: (taskId: string) => http.get<TaskDetail>(`/api/runtime/tasks/${encodeURIComponent(taskId)}`),

  taskVariables: (taskId: string) => http.get<Variables>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/variables`),

  claimTask: (taskId: string, userId?: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/claim`, userId ? { userId } : undefined),

  setTaskAssignee: (taskId: string, assignee: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/assignee`, { assignee }),

  completeTask: (taskId: string, variables?: Variables, idempotencyKey?: string) =>
    http.post<void>(
      `/api/runtime/tasks/${encodeURIComponent(taskId)}/complete`,
      { variables },
      { headers: { 'Idempotency-Key': idempotencyKey ?? newIdempotencyKey() } },
    ),

  /** 局部更新任务：名称 / 描述 / 优先级 / 到期时间 / 分类 / 属主。 */
  updateTask: (taskId: string, request: UpdateTaskRequest) =>
    http.patch<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}`, request),

  /** 取消认领（当前办理人或管理员；任务未签收时幂等）。 */
  unclaimTask: (taskId: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/unclaim`),

  /** 委派给他人（办理人变成被委派人，委派状态为 PENDING；解决后回到原办理人）。 */
  delegateTask: (taskId: string, userId: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/delegate`, { userId }),

  /** 解决委派：回到原办理人继续处理。 */
  resolveTask: (taskId: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/resolve`),

  /** 表单元数据（formKey + activiti:formProperty 声明，只读）。 */
  taskForm: (taskId: string) =>
    http.get<FormMetadata>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/form`),

  addCandidateUser: (taskId: string, userId: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/candidate-users`, { userId }),

  removeCandidateUser: (taskId: string, userId: string) =>
    http.del<void>(
      `/api/runtime/tasks/${encodeURIComponent(taskId)}/candidate-users/${encodeURIComponent(userId)}`,
    ),

  addCandidateGroup: (taskId: string, groupId: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/candidate-groups`, { groupId }),

  removeCandidateGroup: (taskId: string, groupId: string) =>
    http.del<void>(
      `/api/runtime/tasks/${encodeURIComponent(taskId)}/candidate-groups/${encodeURIComponent(groupId)}`,
    ),

  /** 批量转办（逐项执行，返回逐项结果）。 */
  batchSetTaskAssignee: (taskIds: string[], assignee?: string) =>
    http.post<BatchResultResponse>('/api/runtime/tasks/batch/assignee', { taskIds, assignee }),

  /** 实例变量直写（合并写入；值为 JSON 原生类型）。 */
  setInstanceVariables: (processInstanceId: string, variables: Variables) =>
    http.put<void>(
      `/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/variables`,
      variables,
    ),

  /** 删除单个实例变量。 */
  removeInstanceVariable: (processInstanceId: string, name: string) =>
    http.del<void>(
      `/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/variables/${encodeURIComponent(name)}`,
    ),

  /** 迁移到指定定义版本（迁移不可逆；支持活动映射）。 */
  migrateInstance: (
    processInstanceId: string,
    targetProcessDefinitionId: string,
    reason?: string,
    activityMappings?: Record<string, string>,
  ) =>
    http.post<unknown>(
      `/api/runtime/process-instances/${encodeURIComponent(processInstanceId)}/migrate`,
      { targetProcessDefinitionId, reason, activityMappings },
    ),

  taskStats: (params?: { timeZoneOffsetMinutes?: number }) =>
    http.get<TaskStatsResponse>(`/api/runtime/tasks/stats${params?.timeZoneOffsetMinutes !== undefined ? `?timeZoneOffsetMinutes=${params.timeZoneOffsetMinutes}` : ''}`),
};

// ─────────────────────────── 管理治理 · 审计 ───────────────────────────

export interface AuditQuery {
  actor?: string;
  action?: string;
  targetType?: string;
  targetId?: string;
  processInstanceId?: string;
  tenantId?: string;
  success?: boolean;
  since?: string;
  until?: string;
  firstResult?: number;
  maxResults?: number;
}

export const auditApi = {
  /** 分页查管理动作审计（统一 envelope 分页信封 `{ items, total, firstResult, maxResults }`）。 */
  list: (params?: AuditQuery, signal?: AbortSignal) =>
    list<AdminAuditItem>('/api/admin/audit', params, { signal }),

  /** 筛选项：出现过的动作 / 目标类型 / 操作人（供下拉，不再硬编码）。 */
  options: () => http.get<AdminAuditOptions>('/api/admin/audit/options'),

  /** 保留策略清理：删除 `before` 之前的记录，返回删除条数。 */
  purge: (params: { before: string; tenantId?: string }) =>
    http.del<AdminAuditPurgeResponse>(`/api/admin/audit${toQueryString(params)}`),
};

// ─────────────────────────── 管理治理 · 定时作业 ───────────────────────────

export interface JobQuery {
  processInstanceId?: string;
  processDefinitionId?: string;
  dueBefore?: string;
  firstResult?: number;
  maxResults?: number;
}

export const jobsApi = {
  /** 分页查边界定时器作业（到期时间升序）。 */
  list: (params?: JobQuery, signal?: AbortSignal) =>
    list<TimerJobSummary>('/api/admin/jobs', params, { signal }),

  /** 作业统计：总数 / 已到期 / 一小时内到期 / 最近到期时间。 */
  stats: (params?: { processInstanceId?: string; processDefinitionId?: string }) =>
    http.get<TimerJobStats>(`/api/admin/jobs/stats${toQueryString(params)}`),

  /** 改期：只改到期时间，触发仍由宿主轮询负责。 */
  reschedule: (jobId: string, dueDate: string) =>
    http.post<void>(`/api/admin/jobs/${encodeURIComponent(jobId)}/due-date`, { dueDate }),

  /**
   * 立即触发：不必等到期，走与轮询相同的抢占语义。
   * 作业已被触发 / 实例挂起 / 任务已完成时后端回 409。
   */
  execute: (jobId: string) =>
    http.post<void>(`/api/admin/jobs/${encodeURIComponent(jobId)}/execute`),
};

// ─────────────────────────── 身份（组 / 组成员）───────────────────────────

export const identityApi = {
  /** 某用户所属的全部组（用于展开「我的候选组待办」）。 */
  groupsOfUser: (userId: string) =>
    http.get<UserGroup[]>(`/api/identity/users/${encodeURIComponent(userId)}/groups`),

  group: (groupId: string) => http.get<UserGroup>(`/api/identity/groups/${encodeURIComponent(groupId)}`),

  /** 组内成员 userId 列表。 */
  usersInGroup: (groupId: string) =>
    http.get<string[]>(`/api/identity/groups/${encodeURIComponent(groupId)}/users`),
};

// ─────────────────────────── 管理治理 · 指标 ───────────────────────────

export const metricsApi = {
  /**
   * 管理指标。租户口径由令牌决定（受限身份传别的租户会被 403）；
   * `timeZoneOffsetMinutes` 用于「今日到期」的自然日换算。
   */
  get: (params?: { windowDays?: number; timeZoneOffsetMinutes?: number }) =>
    http.get<AdminMetricsResponse>(
      `/api/admin/metrics${toQueryString({
        windowDays: params?.windowDays,
        timeZoneOffsetMinutes: params?.timeZoneOffsetMinutes,
      })}`,
    ),
};
