import { http, list } from './client';
import type {
  BpmnModelDetail,
  BpmnModelSummary,
  BatchResultResponse,
  DeploymentSummaryResponse,
  DeploymentResource,
  Diagram,
  HistoricActivity,
  HistoricProcessInstance,
  HistoricTask,
  HistoricVariable,
  ProcessDefinitionResponse,
  ProcessInstanceResponse,
  ProcessType,
  SaveBpmnModelRequest,
  SaveProcessTypeRequest,
  TaskDetail,
  TaskResponse,
  TaskStatsResponse,
  Variables,
} from './types';

// ─────────────────────────── 流程模型 ───────────────────────────

export const modelsApi = {
  list: () => http.get<BpmnModelSummary[]>('/api/admin/models'),
  get: (id: string) => http.get<BpmnModelDetail>(`/api/admin/models/${encodeURIComponent(id)}`),
  save: (id: string, request: SaveBpmnModelRequest) =>
    http.put<void>(`/api/admin/models/${encodeURIComponent(id)}`, request),
  remove: (id: string) => http.del<void>(`/api/admin/models/${encodeURIComponent(id)}`),
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
  list: (params?: { firstResult?: number; maxResults?: number; tenantId?: string }) =>
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
  instances: (params?: InstanceQuery) => list<ProcessInstanceResponse>('/api/runtime/process-instances', params),

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

  // 历史
  historicActivities: (processInstanceId: string) =>
    http.get<HistoricActivity[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/activities`),

  historicTasks: (processInstanceId: string) =>
    http.get<HistoricTask[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/tasks`),

  historicInstance: (processInstanceId: string) =>
    http.get<HistoricProcessInstance>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}`),

  historicVariables: (processInstanceId: string) =>
    http.get<HistoricVariable[]>(`/api/runtime/history/process-instances/${encodeURIComponent(processInstanceId)}/variables`),

  historicInstances: (params?: InstanceQuery & { finished?: boolean; deleted?: boolean }) =>
    list<HistoricProcessInstance>('/api/runtime/history/process-instances', params),

  // 任务
  tasks: (params?: TaskQuery) => list<TaskResponse>('/api/runtime/tasks', params),

  task: (taskId: string) => http.get<TaskDetail>(`/api/runtime/tasks/${encodeURIComponent(taskId)}`),

  taskVariables: (taskId: string) => http.get<Variables>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/variables`),

  claimTask: (taskId: string, userId?: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/claim`, userId ? { userId } : undefined),

  setTaskAssignee: (taskId: string, assignee: string) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/assignee`, { assignee }),

  completeTask: (taskId: string, variables?: Variables) =>
    http.post<void>(`/api/runtime/tasks/${encodeURIComponent(taskId)}/complete`, { variables }),

  taskStats: (params?: { timeZoneOffsetMinutes?: number }) =>
    http.get<TaskStatsResponse>(`/api/runtime/tasks/stats${params?.timeZoneOffsetMinutes !== undefined ? `?timeZoneOffsetMinutes=${params.timeZoneOffsetMinutes}` : ''}`),
};
