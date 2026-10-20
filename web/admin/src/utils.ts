/** 后端时间统一为 UTC ISO（带 Z），这里转成本地可读格式；空值显示占位符。 */
export function formatTime(iso?: string | null): string {
  if (!iso) {
    return '—';
  }

  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('zh-CN', { hour12: false });
}

/**
 * 审计 / 指标页共用的动作配色：删除与清理类最醒目（红），变量类（紫），
 * 部署与定义类（蓝），实例生命周期（橙），其余（蓝）。
 */
export function auditActionColor(action: string): string {
  if (
    action === 'deployment.delete'
    || action === 'history.archive'
    || action === 'audit.purge'
    || action.startsWith('instance.delete')
    || action.startsWith('instance.batch_delete')
  ) {
    return 'red';
  }

  if (action.startsWith('variable') || action.startsWith('task_variable')) {
    return 'purple';
  }

  if (action.startsWith('deployment') || action.startsWith('definition')) {
    return 'geekblue';
  }

  if (action.startsWith('instance')) {
    return 'orange';
  }

  return 'blue';
}

/** 变量值的只读展示：null / undefined / 对象 / 标量各有可读形态。 */
export function formatValue(value: unknown): string {
  if (value === null) {
    return 'null';
  }

  if (value === undefined) {
    return '—';
  }

  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

/** 毫秒时长转可读文本（秒 / 分秒 / 时分）；空值显示占位符。 */
export function formatDuration(milliseconds?: number | null): string {
  if (milliseconds === undefined || milliseconds === null) {
    return '—';
  }

  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }

  const seconds = Math.floor(milliseconds / 1000);

  if (seconds < 60) {
    return `${seconds} 秒`;
  }

  const minutes = Math.floor(seconds / 60);

  if (minutes < 60) {
    return `${minutes} 分 ${seconds % 60} 秒`;
  }

  return `${Math.floor(minutes / 60)} 时 ${minutes % 60} 分`;
}
