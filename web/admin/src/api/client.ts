import { clearCredential, credentialHeaders, getCredential } from '../auth';
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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  // 附加鉴权头（API Key / Bearer Token）；开放模式（无凭据）时为空
  const headers = new Headers(init?.headers);
  const authHeaders = credentialHeaders(getCredential());

  for (const [name, value] of Object.entries(authHeaders)) {
    headers.set(name, value);
  }

  const response = await fetch(path, { ...init, headers });

  // 401：凭据缺失 / 失效 → 清掉并跳登录页（登录页自身用 verifyCredential 直连，不经过这里）
  if (response.status === 401 && window.location.pathname !== '/login') {
    clearCredential();
    const redirect = `${window.location.pathname}${window.location.search}`;
    window.location.assign(`/login?redirect=${encodeURIComponent(redirect)}`);
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

function jsonInit(method: string, payload?: unknown): RequestInit {
  return {
    method,
    headers: payload === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: payload === undefined ? undefined : JSON.stringify(payload),
  };
}

export const http = {
  get: <T>(path: string) => request<T>(path, { method: 'GET' }),

  post: <T>(path: string, payload?: unknown) => request<T>(path, jsonInit('POST', payload)),

  put: <T>(path: string, payload?: unknown) => request<T>(path, jsonInit('PUT', payload)),

  patch: <T>(path: string, payload?: unknown) => request<T>(path, jsonInit('PATCH', payload)),

  del: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
};

/** 列表端点统一走 envelope=true，拿到 items + total，避免读 X-Total-Count 头。 */
export async function list<T>(path: string, params?: object): Promise<PagedResponse<T>> {
  const url = new URL(path, window.location.origin);

  if (params !== undefined) {
    for (const [key, value] of Object.entries(params)) {
      if (value !== undefined && value !== '') {
        url.searchParams.set(key, String(value));
      }
    }
  }

  url.searchParams.set('envelope', 'true');

  return http.get<PagedResponse<T>>(`${url.pathname}${url.search}`);
}
