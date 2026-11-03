import type { ExternalTaskStatus, ExternalTaskView } from '../api/externalTasks';

/**
 * 外部任务页的纯逻辑（状态文案 / 配色 / 动作可用性 / 统计）。
 *
 * 单独抽出来是为了能被单测钉住：这里的「能不能重投 / 能不能解锁」直接对应后端的状态机，
 * 判错一次就是让人对着 409 / 400 猜原因。
 */

export const EXTERNAL_TASK_STATUSES: readonly ExternalTaskStatus[] = [
  'PENDING',
  'LOCKED',
  'COMPLETED',
  'DEAD_LETTER',
] as const;

const STATUS_LABELS: Record<ExternalTaskStatus, string> = {
  PENDING: '待领取',
  LOCKED: '已锁定',
  COMPLETED: '已完成',
  DEAD_LETTER: '死信',
};

const STATUS_COLORS: Record<ExternalTaskStatus, string> = {
  PENDING: 'processing',
  LOCKED: 'geekblue',
  COMPLETED: 'success',
  DEAD_LETTER: 'error',
};

/** 状态文案；遇到未知状态原样返回（后端加了新状态时不至于显示空白）。 */
export function externalTaskStatusLabel(status: string): string {
  return STATUS_LABELS[status as ExternalTaskStatus] ?? status;
}

/** 状态配色；未知状态用 antd 的 default 灰。 */
export function externalTaskStatusColor(status: string): string {
  return STATUS_COLORS[status as ExternalTaskStatus] ?? 'default';
}

/** 筛选下拉的选项。 */
export function externalTaskStatusOptions(): { value: ExternalTaskStatus; label: string }[] {
  return EXTERNAL_TASK_STATUSES.map((status) => ({ value: status, label: STATUS_LABELS[status] }));
}

/**
 * 只有死信能重投 —— 与后端 `external_task_not_dead_letter`（409）同口径。
 * 别给「待领取 / 已锁定」也开按钮：那两种情况要么正常在途，要么该等 worker 回执。
 */
export function canRetry(status: string): boolean {
  return status === 'DEAD_LETTER';
}

/**
 * 只有「已锁定且知道持锁者」才谈得上解锁。
 *
 * 注意锁可能已过期但还没被别的 worker 重领（此时解锁仍会成功），所以这里**不按时间**禁用；
 * 真正被重领过的情况，服务端会以 400「锁不属于」拒绝。
 */
export function canUnlock(task: Pick<ExternalTaskView, 'status' | 'workerId'>): boolean {
  return task.status === 'LOCKED' && (task.workerId ?? '').trim() !== '';
}

/** 锁是否已过期（仅用于展示提示；锁过期不等于任务可解锁/不可解锁）。 */
export function isLockExpired(task: Pick<ExternalTaskView, 'lockExpiresAt'>, now = Date.now()): boolean {
  if (task.lockExpiresAt === null) {
    return false;
  }

  const expiresAt = new Date(task.lockExpiresAt).getTime();

  return !Number.isNaN(expiresAt) && expiresAt <= now;
}

/** 各状态条数：服务端查询没有 total，统计只能基于这一屏数据。 */
export function summarizeStatuses(tasks: readonly ExternalTaskView[]): Record<ExternalTaskStatus, number> {
  const summary: Record<ExternalTaskStatus, number> = {
    PENDING: 0,
    LOCKED: 0,
    COMPLETED: 0,
    DEAD_LETTER: 0,
  };

  for (const task of tasks) {
    const status = task.status as ExternalTaskStatus;

    if (status in summary) {
      summary[status] += 1;
    }
  }

  return summary;
}

/** 列表被服务端上限截断了吗（截断了就要提示收窄筛选，否则会以为「只有这些」）。 */
export function isTruncated(tasks: readonly ExternalTaskView[], limit: number): boolean {
  return tasks.length >= limit;
}
