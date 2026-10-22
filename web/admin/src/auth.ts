/**
 * 管理端鉴权凭据：存储 + 请求头生成 + 有效性探针 + 过期判定。
 *
 * 凭据来源有两条（见 src/NetBpm.Rest/Security/AuthenticationMiddleware.cs）：
 *   1. 账号密码登录：POST /api/auth/login 换回的 JWT（本文件负责存它）；
 *   2. 服务间凭据：直接粘贴 API Key 或外部签发的 JWT（运维 / 集成场景）。
 *
 * 存储权衡：
 *   - 默认写到 **localStorage**（键 netbpm.admin.credential.v1）——同标签页族的其它标签页
 *     与设计器（web/designer 读同一个键）免登录，这是控制台与流程设计器共用的凭据；
 *   - 取消「保持登录」时只写 **sessionStorage**，关闭标签页即失效，共享凭据也在本标签页被覆盖；
 *   - 两种写法 XSS 都能读到凭据，彻底防御仍需后端改 httpOnly Cookie 会话。
 * 读取顺序：sessionStorage 优先（本标签页的临时登录），其次 localStorage（跨标签页共享）。
 *
 * 后端两种鉴权（见 src/NetBpm.Rest/Security/AuthenticationMiddleware.cs）：
 *   - API Key：请求头 X-Api-Key（可再带 X-User-Id / X-Tenant-Id 声明身份与租户）；
 *   - JWT：请求头 Authorization: Bearer <token>（身份来自令牌的 sub / tenant 声明）。
 * 都未配置时后端放行（开放模式），此时前端不带凭据也能正常访问。
 */
import { apiPath } from './api/base';

export type AuthMode = 'apiKey' | 'bearer';

export interface Credential {
  mode: AuthMode;
  value: string;
  /** API Key 模式下的操作人声明（可选）。 */
  userId?: string;
  /** API Key 模式下的租户声明（可选）。 */
  tenantId?: string;
  /** Bearer 令牌的过期时间（ISO 字符串，UTC）；账号密码登录时由后端返回。 */
  expiresAt?: string;
  /** 登录名（账号密码登录时后端返回，仅用于顶栏展示）。 */
  user?: string;
  /** 显示名（账号密码登录时后端返回）。 */
  displayName?: string;
  /** 令牌携带的授权范围（账号密码登录时后端返回）。 */
  scopes?: string[];
}

/**
 * 与设计器（web/designer/src/auth.ts）共用的存储键：
 * 控制台写、设计器读，同一源下新开标签页不必重新登录。
 */
const STORAGE_KEY = 'netbpm.admin.credential.v1';

/**
 * 写入位置：remember=true → localStorage（跨标签页 / 跨窗口共享，浏览器重启后仍在）；
 * remember=false → sessionStorage（仅当前标签页，关闭即失效）。
 */
export type CredentialPersistence = 'session' | 'shared';

function parseCredential(raw: string | null): Credential | null {
  if (raw === null) {
    return null;
  }

  try {
    const parsed = JSON.parse(raw) as Partial<Credential>;

    if (typeof parsed.value !== 'string' || parsed.value.length === 0) {
      return null;
    }

    if (parsed.mode !== 'apiKey' && parsed.mode !== 'bearer') {
      return null;
    }

    return parsed as Credential;
  } catch {
    return null;
  }
}

/** 当前标签页的凭据：临时登录（sessionStorage）优先，其次共享凭据（localStorage）。 */
export function getCredential(): Credential | null {
  try {
    return parseCredential(sessionStorage.getItem(STORAGE_KEY))
      ?? parseCredential(localStorage.getItem(STORAGE_KEY));
  } catch {
    return null;
  }
}

/**
 * 保存凭据。
 * @param persistence 'shared' 写 localStorage（默认，跨标签页共享）；'session' 只写 sessionStorage。
 */
export function setCredential(credential: Credential, persistence: CredentialPersistence = 'shared'): void {
  try {
    if (persistence === 'shared') {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(credential));
      sessionStorage.removeItem(STORAGE_KEY);

      return;
    }

    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(credential));
  } catch {
    // 隐私模式下 setItem 会抛异常：登录态退化成「内存里有效、刷新即失效」，不阻塞主流程
  }
}

/** 退出登录：两种存储都清掉（共享凭据意味着一处退出、处处退出）。 */
export function clearCredential(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // 同上：清理失败也当作已退出
  }
}

/** 当前凭据落在哪种存储里（顶栏可提示「仅本标签页」）。 */
export function credentialPersistence(): CredentialPersistence | null {
  try {
    if (sessionStorage.getItem(STORAGE_KEY) !== null) {
      return 'session';
    }

    return localStorage.getItem(STORAGE_KEY) !== null ? 'shared' : null;
  } catch {
    return null;
  }
}

/** Bearer 令牌是否已过期（无过期时间的凭据视为长期有效）。 */
export function isCredentialExpired(credential: Credential | null, now: number = Date.now()): boolean {
  if (credential === null || credential.expiresAt === undefined) {
    return false;
  }

  const expiresAt = Date.parse(credential.expiresAt);

  return Number.isFinite(expiresAt) && expiresAt <= now;
}

/**
 * 订阅其它标签页的凭据变化（storage 事件只在**别的**标签页触发）。
 * 用途：新标签页登录 / 别处退出后，当前页的顶栏状态同步更新。
 */
export function subscribeCredential(listener: () => void): () => void {
  const onStorage = (event: StorageEvent) => {
    if (event.key === null || event.key === STORAGE_KEY) {
      listener();
    }
  };

  window.addEventListener('storage', onStorage);

  return () => window.removeEventListener('storage', onStorage);
}

/** 由凭据生成要附加到每个请求的鉴权头。 */
export function credentialHeaders(credential: Credential | null): Record<string, string> {
  if (credential === null) {
    return {};
  }

  if (credential.mode === 'apiKey') {
    const headers: Record<string, string> = { 'X-Api-Key': credential.value };

    if (credential.userId) {
      headers['X-User-Id'] = credential.userId;
    }

    if (credential.tenantId) {
      headers['X-Tenant-Id'] = credential.tenantId;
    }

    return headers;
  }

  return { Authorization: `Bearer ${credential.value}` };
}

/**
 * 校验凭据是否有效：用 /api/auth/me 做探针（它就是「我是谁」端点）。
 * 启用了鉴权时，凭据无效 / 缺失会返回 401；未启用鉴权（开放模式）返回 200 且 authMode=none，
 * 凭据直接可用。
 *
 * 不用 /api/admin/info：它与就绪探针同口径、匿名可达，恒为 200，区分不出凭据好坏。
 */
export async function verifyCredential(credential: Credential): Promise<boolean> {
  try {
    const response = await fetch(apiPath('/api/auth/me'), { headers: credentialHeaders(credential) });

    return response.ok;
  } catch {
    return false;
  }
}

/** 顶栏展示用的凭据标签。 */
export function credentialLabel(credential: Credential | null): string {
  if (credential === null) {
    return '未登录';
  }

  return credential.mode === 'apiKey' ? 'API Key' : 'Bearer Token';
}

// ─────────────────────────── 身份解析（顶栏展示）───────────────────────────

/** 从凭据里解析出的操作人 / 租户 / 授权范围。 */
export interface Identity {
  user?: string;
  tenant?: string;
  scopes: string[];
}

/**
 * 管理范围 scope。
 * ⚠️ 唯一来源是后端 `src/NetBpm.Rest/Security/RequestContext.cs` 的
 * `TaskScopes.Admin`（'netbpm.admin'），改值需两侧同步。
 */
export const ADMIN_SCOPE = 'netbpm.admin';

/**
 * 解析当前登录身份：
 *   - API Key：user / tenant 来自用户填写的 X-User-Id / X-Tenant-Id 声明；
 *   - Bearer：解码 JWT payload，取 sub / nameid → user，tenant / tenant_id → tenant，
 *     scope / scp → scopes（与后端 ReadClaimSet 同口径：兼容空格、逗号、JSON 数组三种写法）。
 */
export function resolveIdentity(credential: Credential | null): Identity {
  if (credential === null) {
    return { scopes: [] };
  }

  if (credential.mode === 'apiKey') {
    return { user: credential.userId, tenant: credential.tenantId, scopes: [] };
  }

  const payload = decodeJwtPayload(credential.value);

  // 不透明令牌（非 JWT）：退回登录响应里记下的身份信息
  if (payload === null) {
    return {
      user: credential.user,
      tenant: credential.tenantId,
      scopes: credential.scopes ?? [],
    };
  }

  return {
    user: firstString(payload, 'sub', 'nameid'),
    tenant: firstString(payload, 'tenant', 'tenant_id'),
    scopes: readClaimSet(payload, 'scope', 'scp'),
  };
}

/** 从 JWT 里解出 payload（base64url → UTF-8 JSON）。返回 null 表示非合法 JWT。 */
function decodeJwtPayload(token: string): Record<string, unknown> | null {
  try {
    const parts = token.split('.');

    if (parts.length < 2) {
      return null;
    }

    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(Math.ceil(base64.length / 4) * 4, '=');
    const bytes = Uint8Array.from(atob(padded), (char) => char.charCodeAt(0));
    const parsed = JSON.parse(new TextDecoder().decode(bytes)) as unknown;

    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function firstString(payload: Record<string, unknown>, ...keys: string[]): string | undefined {
  for (const key of keys) {
    const value = payload[key];

    if (typeof value === 'string' && value.length > 0) {
      return value;
    }
  }

  return undefined;
}

function readClaimSet(payload: Record<string, unknown>, ...keys: string[]): string[] {
  const values = new Set<string>();

  for (const key of keys) {
    const value = payload[key];

    // JSON 数组写法（scp: ["a","b"]）——之前注释声称支持但实现漏掉了
    if (Array.isArray(value)) {
      for (const item of value) {
        if (typeof item === 'string' && item.trim().length > 0) {
          values.add(item.trim());
        }
      }

      continue;
    }

    if (typeof value !== 'string') {
      continue;
    }

    const raw = value.trim().replace(/^\[|\]$/g, '');

    for (const part of raw.split(/[ ,]+/)) {
      const item = part.trim().replace(/^"|"$/g, '');

      if (item.length > 0) {
        values.add(item);
      }
    }
  }

  return [...values];
}
