/** 后端时间统一为 UTC ISO（带 Z），这里转成本地可读格式；空值显示占位符。 */
export function formatTime(iso?: string | null): string {
  if (!iso) {
    return '—';
  }

  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString('zh-CN', { hour12: false });
}
