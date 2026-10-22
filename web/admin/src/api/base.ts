/**
 * 部署前缀（Vite base）：独立部署为 `/`，被 OPMS 反代到 `/netbpm/` 时为 `/netbpm/`。
 *
 * 为什么要这个模块：路由 basename、静态资源、接口地址三者必须同源同前缀，
 * 散在各处手写 `'/api'` 会在子路径部署时全部打到宿主的根路径上（表现为 404 / 白屏）。
 * 单独放一个没有依赖的文件，避免 client.ts ↔ auth.ts 循环引用。
 */
export const APP_BASE = import.meta.env.BASE_URL.replace(/\/+$/, '');

/** 把后端接口路径（以 `/` 开头）挂到部署前缀下：`/api/admin/x` → `/netbpm/api/admin/x`。 */
export function apiPath(path: string): string {
  return `${APP_BASE}${path}`;
}
