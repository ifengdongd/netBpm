/**
 * 部署前缀与接口地址：
 *   · 独立部署：设计器在 /designer/，后端接口在站点根 /api/**
 *   · 反代到 OPMS 的 /netbpm/：设计器在 /netbpm/designer/，接口在 /netbpm/api/**
 * 所以不能直接用 BASE_URL 拼接口 —— 要把末尾的 /designer 去掉才是站点前缀。
 */
// ImportMeta.env 是 Vite 注入的：单测用 esbuild 打包后直接跑源码，那里没有 env 对象，
// 读它会抛 TypeError 导致整个测试文件在导入阶段失败。缺省回退到 '/'，与 Vite 的默认
// BASE_URL 一致，生产构建不受影响。
const VITE_BASE_URL = (import.meta as { env?: { BASE_URL?: string } }).env?.BASE_URL ?? '/';

export const APP_BASE = VITE_BASE_URL.replace(/\/designer\/?$/, '');

/** 把后端接口路径（以 `/` 开头）挂到站点前缀下。 */
export function apiPath(path: string): string {
  return `${APP_BASE}${path}`;
}
