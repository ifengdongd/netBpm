import { useEffect, useState } from 'react';

import { credentialHeaders, getCredential } from '../auth';
import { ApiError } from './client';

/**
 * 两个宿主的职责边界（别指错，指错的表现只是某个管理页报 Not Found）：
 *   - NetBpm.Rest（默认 5080）：只有 designer / runtime / identity，外加 /api/admin/info；
 *   - NetBpm.Admin（默认 5081）：引擎 REST + 全部管理端点 /api/admin/**（模型 / 部署 / 定时作业 …）。
 */
export const WRONG_HOST_HINT =
  '当前后端不是 Admin 宿主（缺少 /api/admin 端点），请把控制台指向 NetBpm.Admin（默认 5081）';

/** 404 = 端点不存在。用于识别「控制台指错宿主」，而不是「确实没有数据」。 */
export function isNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

/** 宿主自检结果：`checking` 探测中，`ok` 宿主正确，`missingAdmin` 指到了 Rest 宿主。 */
export type AdminHostCheck = 'checking' | 'ok' | 'missingAdmin' | 'unknown';

/**
 * 宿主自检：确认控制台的 /api 指向 NetBpm.Admin 而不是 NetBpm.Rest。
 *
 * 探针取 `/api/admin/jobs/stats` —— 它是管理端点，只在 Admin 宿主注册（见
 * src/NetBpm.Admin/Endpoints/AdminEndpoints.cs），Rest 宿主打过去是 404。
 * 不能拿 /api/admin/info 当探针：两个宿主都有它，区分不出来。
 *
 * 判定：404 → 指错宿主；401/403 → 路由存在、宿主正确（只是凭据不够）；
 * 网络错误保持 unknown，不误报。探测只在布局挂载后跑一次，不轮询。
 */
export function useAdminHostCheck(): AdminHostCheck {
  const [state, setState] = useState<AdminHostCheck>('checking');

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const response = await fetch('/api/admin/jobs/stats', {
          headers: { Accept: 'application/json', ...credentialHeaders(getCredential()) },
        });

        if (!cancelled) {
          setState(response.status === 404 ? 'missingAdmin' : 'ok');
        }
      } catch {
        if (!cancelled) {
          setState('unknown');
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  return state;
}
