import { http, toQueryString } from './client';

/**
 * 外部任务（`/api/runtime/external-tasks`）：`activiti:topic` 节点的拉模式执行通道。
 *
 * 这里只用到「查询 / 死信重投 / 释放锁」三个端点 —— 领取（fetch）与回执（complete / failure）
 * 是 worker 的活，控制台不该代劳，否则会抢走 worker 的锁。
 */

/** 外部任务状态：`PENDING`（待领取）/ `LOCKED`（已锁定）/ `COMPLETED` / `DEAD_LETTER`。 */
export type ExternalTaskStatus = 'PENDING' | 'LOCKED' | 'COMPLETED' | 'DEAD_LETTER';

/** 查询端点单次返回上限（服务端 `maxResults` 硬上限，超过会被截断到 200）。 */
export const EXTERNAL_TASK_MAX_RESULTS = 200;

export interface ExternalTaskView {
  id: string;
  processInstanceId: string;
  executionId: string | null;
  processDefinitionId: string | null;
  processDefinitionKey: string | null;
  activityId: string | null;
  activityName: string | null;
  topic: string;
  tenantId: string | null;
  businessKey: string | null;
  /** 节点登记那一刻的变量快照（不是实时视图）。 */
  payload: Record<string, unknown>;
  priority: number;
  /** 剩余重试次数；失败回执扣到这个数为 0 时进死信。 */
  retries: number;
  status: ExternalTaskStatus | string;
  /** 锁持有者；`unlock` 要带同一个值。 */
  workerId: string | null;
  lockExpiresAt: string | null;
  /** 失败退避后的下次可领时间。 */
  nextDueAt: string | null;
  errorMessage: string | null;
  errorDetails: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface ExternalTaskQuery {
  topic?: string;
  status?: string;
  processInstanceId?: string;
  maxResults?: number;
}

export const externalTasksApi = {
  /** 按条件查询（响应是**裸数组**，不是信封；服务端没有 total 与分页游标）。 */
  list: (params?: ExternalTaskQuery, signal?: AbortSignal) =>
    http.get<ExternalTaskView[]>(
      `/api/runtime/external-tasks${toQueryString(params)}`,
      { signal },
    ),

  /**
   * 死信重投：204。只有 `DEAD_LETTER` 能重投，其余状态返回 409
   * `external_task_not_dead_letter`；`retries` 是「重投后恢复的剩余次数」，
   * 留空用服务端默认（`NetBpm:ExternalTasks:Retries`）。
   */
  retry: (id: string, retries?: number) =>
    http.post<void>(
      `/api/runtime/external-tasks/${encodeURIComponent(id)}/retry`,
      retries === undefined ? {} : { retries },
    ),

  /**
   * 释放锁：204，任务立刻回到 `PENDING`（**不扣重试次数**，所以可能马上又被领走）。
   * `workerId` 必须是当前持锁者；不匹配时服务端返回 **400**（不是 409）。
   */
  unlock: (id: string, workerId: string) =>
    http.post<void>(`/api/runtime/external-tasks/${encodeURIComponent(id)}/unlock`, { workerId }),
};
