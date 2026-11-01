import { http, toQueryString } from './client';

/**
 * 配额总览（`GET /admin/v1/quotas`）。
 *
 * 两个层级一次取回：租户默认值（配得对不对）+ 客户端实际生效值（谁吃满了）。
 * 两个列表**各自**游标分页，互不影响。
 */

export interface TenantQuotaView {
  tenantId: string;
  name: string;
  status: string;
  quotaPerMinute: number;
  clientCount: number;
  createdAt: string;
}

export interface ClientQuotaView {
  tenantId: string;
  clientId: string;
  name: string;
  status: string;
  quotaPerMinute: number;
  scopes: string[];
  allowedProcessKeys: string[];
  secretExpiresAt: string | null;
}

export interface QuotaOverviewView {
  tenants: TenantQuotaView[];
  clients: ClientQuotaView[];
  rateLimitEnabled: boolean;
  defaultPerMinute: number;
  tenantsNextCursor: string | null;
  clientsNextCursor: string | null;
}

export const quotasApi = {
  get: (
    params?: { tenantCursor?: string; clientCursor?: string; max?: number },
    signal?: AbortSignal,
  ) => http.get<QuotaOverviewView>(`/admin/v1/quotas${toQueryString(params)}`, { signal }),
};
