import { http, toQueryString } from './client';

/**
 * 事件订阅与死信（`/admin/v1/subscriptions`、`/admin/v1/events/*`）。
 *
 * 读用 `/admin/v1`（与管理面同一套租户口径与 RFC 9457 错误体），
 * 写也在 `/admin/v1`——`/api/admin/event-subscriptions` 是兼容旧脚本的路径。
 */

export interface EventSubscriptionView {
  id: string;
  tenantId: string;
  name: string;
  eventTypes: string[];
  processDefinitionId: string | null;
  endpointUrl: string;
  /** 密钥摘要（不可反推）；换密钥走 `/rotate`。 */
  secretPreview: string;
  headers: Record<string, string>;
  enabled: boolean;
  maxAttempts: number;
  maxPerMinute: number;
  createdAt: string;
  updatedAt: string;
}

export interface EventDeliveryView {
  eventId: string;
  subscriptionId: string;
  tenantId: string;
  /** `PENDING` / `DELIVERED` / `FAILED` / `DEAD_LETTER`。 */
  status: string;
  attempts: number;
  lastError: string | null;
  nextAttemptAt: string | null;
  createdAt: string;
  updatedAt: string;
  deliveredAt: string | null;
}

export interface AdminDeadLetterPageView {
  items: EventDeliveryView[];
  total: number;
  first: number;
  max: number;
}

export interface AdminOutboxBacklogView {
  pending: number;
  deadLetter: number;
  oldestPendingAt: string | null;
}

export interface AdminSubscriptionStatsView {
  subscriptionId: string;
  name: string | null;
  tenantId: string;
  maxPerMinute: number | null;
  enabled: boolean | null;
  total: number;
  pending: number;
  delivered: number;
  failed: number;
  deadLetter: number;
  lastDeliveredAt: string | null;
  lastFailureAt: string | null;
}

export interface AdminEventStatsView {
  since: string | null;
  outbox: AdminOutboxBacklogView;
  subscriptions: AdminSubscriptionStatsView[];
}

export interface CreateEventSubscriptionRequest {
  name: string;
  endpointUrl: string;
  tenantId?: string | null;
  eventTypes?: string[] | null;
  processDefinitionId?: string | null;
  secret?: string | null;
  headers?: Record<string, string> | null;
  enabled?: boolean | null;
  maxAttempts?: number | null;
  maxPerMinute?: number | null;
}

export interface UpdateEventSubscriptionRequest {
  name?: string;
  eventTypes?: string[];
  processDefinitionId?: string | null;
  endpointUrl?: string;
  headers?: Record<string, string>;
  enabled?: boolean;
  maxAttempts?: number;
  maxPerMinute?: number;
}

/** 新签发（或轮换）的订阅密钥：明文只出现这一次。 */
export interface IssuedSubscriptionSecret {
  subscriptionId: string;
  secret: string;
}

export const eventingApi = {
  listSubscriptions: (params?: { tenantId?: string; enabled?: boolean; max?: number }, signal?: AbortSignal) =>
    http.get<EventSubscriptionView[]>(`/admin/v1/subscriptions${toQueryString(params)}`, { signal }),

  create: (request: CreateEventSubscriptionRequest) =>
    http.post<{ subscription: EventSubscriptionView; secret: string }>(
      '/admin/v1/subscriptions',
      request,
    ),

  update: (id: string, request: UpdateEventSubscriptionRequest) =>
    http.patch<EventSubscriptionView>(`/admin/v1/subscriptions/${encodeURIComponent(id)}`, request),

  /** 启停（查询参数而不是请求体，与后端一致）。 */
  setEnabled: (id: string, enabled: boolean) =>
    http.post<EventSubscriptionView>(
      `/admin/v1/subscriptions/${encodeURIComponent(id)}/status${toQueryString({ enabled })}`,
    ),

  rotate: (id: string) =>
    http.post<IssuedSubscriptionSecret>(`/admin/v1/subscriptions/${encodeURIComponent(id)}/rotate`),

  remove: (id: string) =>
    http.del<void>(`/admin/v1/subscriptions/${encodeURIComponent(id)}`),

  deliveries: (id: string, params?: { eventId?: string }, signal?: AbortSignal) =>
    http.get<EventDeliveryView[]>(
      `/admin/v1/subscriptions/${encodeURIComponent(id)}/deliveries${toQueryString(params)}`,
      { signal },
    ),

  deadLetters: (
    params?: { tenantId?: string; subscriptionId?: string; first?: number; max?: number },
    signal?: AbortSignal,
  ) => http.get<AdminDeadLetterPageView>(`/admin/v1/events/deadletters${toQueryString(params)}`, { signal }),

  stats: (params?: { tenantId?: string; subscriptionId?: string; since?: string; max?: number }, signal?: AbortSignal) =>
    http.get<AdminEventStatsView>(`/admin/v1/events/stats${toQueryString(params)}`, { signal }),

  /** 死信重投（同一个投递记录再次投递，投递计数继续累加；204，无响应体）。 */
  retryDelivery: (eventId: string, subscriptionId: string) =>
    http.post<void>(
      `/admin/v1/events/${encodeURIComponent(eventId)}/deliveries/${encodeURIComponent(subscriptionId)}/retry`,
    ),
};
