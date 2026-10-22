import { useEffect, useState } from 'react';

import { credentialHeaders, getCredential } from '../auth';
import { apiPath } from './base';
import { ApiError } from './client';

/**
 * 两个宿主的职责边界（别指错，指错的表现只是某个管理页报 Not Found）：
 *   - NetBpm.Rest（默认 5080）：只有 designer / runtime / identity；
 *   - NetBpm.Admin（默认 5081）：引擎 REST + 全部管理端点 /api/admin/**（模型 / 部署 / 定时作业 …）。
 */
export const WRONG_HOST_HINT =
  '当前后端不是 Admin 宿主（缺少 /api/admin 端点），请把控制台指向 NetBpm.Admin（默认 5081）';

/**
 * 「宿主对、但二进制过旧」——比指错宿主更常见：控制台前端 rebuild 了，后端
 * 还是昨天 `dotnet run --no-build` 起的进程，于是新端点全是 404。
 */
export const STALE_ADMIN_HINT =
  'Admin 宿主是旧构建：缺少 /api/admin/jobs 等新端点，请重新编译并重启 NetBpm.Admin';

/** 单个页面拿到 404 时用：两种成因都覆盖，不做二次探测。 */
export const ADMIN_404_HINT =
  '该管理接口返回 404：控制台可能指向了 Rest 宿主（默认 5080），也可能 Admin 宿主运行的是旧构建（需重新编译并重启）。';

/** 404 = 端点不存在。用于识别「宿主不对 / 版本过旧」，而不是「确实没有数据」。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** 宿主自检结果。 */
export type AdminHostCheck = 'checking' | 'ok' | 'missingAdmin' | 'staleAdmin' | 'unknown';

/**
 * 探测一个 GET 端点的状态码；网络异常返回 `null`（不误报）。
 * 直接用 fetch 而不是 http 封装：这里只关心 404 / 非 404，不需要解析响应体。
 */
async function probeStatus(path: string): Promise<number | null> {
  try {
    const response = await fetch(apiPath(path), {
      headers: { Accept: 'application/json', ...credentialHeaders(getCredential()) },
    });

    return response.status;
  } catch {
    return null;
  }
}

/**
 * 宿主自检：区分「指到了 Rest 宿主」与「Admin 宿主是旧构建」。
 *
 * 两级探针：
 *   1. `/api/admin/models` —— 早期版本的 Admin 宿主就有；404 ⇒ 整族管理端点都不在 ⇒ 指错宿主；
 *   2. `/api/admin/jobs/stats` —— 较新加入的端点；上一步通过但它 404 ⇒ 宿主对、二进制过旧。
 *
 * 判定：401/403 说明路由存在、宿主正确（只是凭据不够）；网络错误保持 unknown。
 * 探测只在布局挂载后跑一次，不轮询。
 */
export function useAdminHostCheck(): AdminHostCheck {
  const [state, setState] = useState<AdminHostCheck>('checking');

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      const legacy = await probeStatus('/api/admin/models?maxResults=1');

      if (cancelled) {
        return;
      }

      if (legacy === null) {
        setState('unknown');
        return;
      }

      if (legacy === 404) {
        setState('missingAdmin');
        return;
      }

      const fresh = await probeStatus('/api/admin/jobs/stats');

      if (cancelled) {
        return;
      }

      if (fresh === null) {
        setState('unknown');
        return;
      }

      setState(fresh === 404 ? 'staleAdmin' : 'ok');
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  return state;
}
