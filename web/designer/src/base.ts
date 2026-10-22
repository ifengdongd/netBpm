/**
 * 部署前缀与接口地址：
 *   · 独立部署：设计器在 /designer/，后端接口在站点根 /api/**
 *   · 反代到 OPMS 的 /netbpm/：设计器在 /netbpm/designer/，接口在 /netbpm/api/**
 * 所以不能直接用 BASE_URL 拼接口 —— 要把末尾的 /designer 去掉才是站点前缀。
 */
export const APP_BASE = import.meta.env.BASE_URL.replace(/\/designer\/?$/, '');

/** 把后端接口路径（以 `/` 开头）挂到站点前缀下。 */
export function apiPath(path: string): string {
  return `${APP_BASE}${path}`;
}
