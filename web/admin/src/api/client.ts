import { clearCredential, credentialHeaders, getCredential } from '../auth';
import { apiPath } from './base';
import type { ErrorResponse, PagedResponse } from './types';

/**
 * 后端统一错误体（ErrorResponse）。非 2xx 时抛出带 message 的 Error，
 * 调用方只需 catch (err) 并展示 err.message。
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code?: string | null;

  constructor(status: number, message: string, code?: string | null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

/**
 * 401（凭据缺失 / 失效）事件：client 层不直接操作路由，由 AppLayout 监听后用
 * router 软跳转到登录页（保留 SPA 状态，不整页刷新）。detail 携带回跳地址。
 */
export const AUTH_EXPIRED_EVENT = 'auth:expired';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  // 附加鉴权头（API Key / Bearer Token）；开放模式（无凭据）时为空
  const headers = new Headers(init?.headers);
  const authHeaders = credentialHeaders(getCredential());

  for (const [name, value] of Object.entries(authHeaders)) {
    headers.set(name, value);
  }

  // 统一挂部署前缀（独立部署为 ''，反代到 /netbpm/ 时为 '/netbpm'）
  const response = await fetch(apiPath(path), { ...init, headers });

  // 401：凭据缺失 / 失效 → 清掉并广播事件（登录页自身用 verifyCredential 直连，不经过这里）。
  // 抛出 401 终止后续处理：既避免继续解析响应体产生二次错误，也让调用方的 catch 有据可依。
  if (response.status === 401 && window.location.pathname !== apiPath('/login')) {
    clearCredential();
    window.dispatchEvent(
      new CustomEvent(AUTH_EXPIRED_EVENT, {
        detail: `${window.location.pathname}${window.location.search}`,
      }),
    );

    throw new ApiError(401, '登录已失效，请重新登录。');
  }

  // 204 / 无响应体：直接返回 undefined（调用方一般用 void 语义）
  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  let body: unknown = null;

  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }

  if (!response.ok) {
    const message = extractMessage(body, response.statusText);
    const code = typeof body === 'object' && body !== null
      ? (body as ErrorResponse).code
      : null;

    throw new ApiError(response.status, message, code);
  }

  return body as T;
}

function extractMessage(body: unknown, fallback: string): string {
  if (typeof body === 'string') {
    return body.length > 0 ? body : fallback;
  }

  if (typeof body === 'object' && body !== null) {
    const message = (body as ErrorResponse).message;

    if (message) {
      return message;
    }
  }

  return fallback;
}

/** 请求级扩展头（如写操作的 Idempotency-Key）。 */
export interface RequestOptions {
  headers?: Record<string, string>;
}

function jsonInit(method: string, payload?: unknown, options?: RequestOptions): RequestInit {
  const headers: Record<string, string> = { ...options?.headers };

  if (payload !== undefined) {
    headers['Content-Type'] = 'application/json';
  }

  return {
    method,
    headers: Object.keys(headers).length === 0 ? undefined : headers,
    body: payload === undefined ? undefined : JSON.stringify(payload),
  };
}

export const http = {
  /** GET；`signal` 供 TanStack Query 取消过期请求（快速翻页时旧请求不再占用服务端）。 */
  get: <T>(path: string, options?: { signal?: AbortSignal }) =>
    request<T>(path, { method: 'GET', signal: options?.signal }),

  post: <T>(path: string, payload?: unknown, options?: RequestOptions) =>
    request<T>(path, jsonInit('POST', payload, options)),

  /** POST 纯文本（BPMN XML 导入等），响应仍按 JSON 解析。 */
  postXml: <T>(path: string, xml: string, options?: RequestOptions) =>
    request<T>(path, {
      method: 'POST',
      headers: { 'Content-Type': 'text/xml; charset=utf-8', ...options?.headers },
      body: xml,
    }),

  put: <T>(path: string, payload?: unknown, options?: RequestOptions) =>
    request<T>(path, jsonInit('PUT', payload, options)),

  patch: <T>(path: string, payload?: unknown, options?: RequestOptions) =>
    request<T>(path, jsonInit('PATCH', payload, options)),

  del: <T>(path: string, options?: RequestOptions) =>
    request<T>(path, { method: 'DELETE', headers: options?.headers }),
};

/**
 * 生成幂等键：写操作（发布 / 发起实例 / 完成任务）带上 `Idempotency-Key`，
 * 网络重试或用户重复点击时服务端只执行一次（同键回放上次结果）。
 * 每次用户动作应生成一个新的键，并在该动作重试期间保持不变。
 */
export function newIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  return `netbpm-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * 把参数对象序列化为查询串（跳过 undefined / null / 空串）。
 * 返回空串或 `?key=value&...`，供 http.get 直接拼接。
 */
export function toQueryString(params?: object): string {
  const search = new URLSearchParams();

  if (params !== undefined) {
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== null && value !== '') {
        search.set(key, String(value));
      }
    }
  }

  const query = search.toString();
  return query.length > 0 ? `?${query}` : '';
}

/**
 * 下载非 JSON 响应（CSV 导出等）：带鉴权头，非 2xx 抛 {@link ApiError}。
 * 直接用 <a href> 不行 —— Bearer / API Key 在请求头里，浏览器导航不会带上。
 */
export async function fetchBlob(path: string): Promise<Blob> {
  const headers = new Headers();

  for (const [name, value] of Object.entries(credentialHeaders(getCredential()))) {
    headers.set(name, value);
  }

  const response = await fetch(apiPath(path), { headers });

  if (!response.ok) {
    throw new ApiError(
      response.status,
      extractMessage(await response.text(), response.statusText),
    );
  }

  return response.blob();
}

/** 列表端点统一走 envelope=true，拿到 items + total，避免读 X-Total-Count 头。 */
export async function list<T>(
  path: string,
  params?: object,
  options?: { signal?: AbortSignal },
): Promise<PagedResponse<T>> {
  return http.get<PagedResponse<T>>(`${path}${toQueryString({ ...params, envelope: 'true' })}`, options);
}
