/**
 * 管理端鉴权凭据：sessionStorage 存储 + 请求头生成 + 有效性探针。
 *
 * 存储权衡（11b 方案）：凭据存 sessionStorage 而非 localStorage —— 关闭浏览器标签页即失效，
 * 缩短令牌暴露窗口；但 XSS 仍可在会话期内读取凭据，彻底防御需后端改 httpOnly Cookie 会话（11c）。
 * 副作用：新开标签页需重新登录，属于安全与便利的取舍。
 *
 * 后端两种鉴权（见 src/NetBpm.Rest/Security/AuthenticationMiddleware.cs）：
 *   - API Key：请求头 X-Api-Key（可再带 X-User-Id / X-Tenant-Id 声明身份与租户）；
 *   - JWT：请求头 Authorization: Bearer <token>（身份来自令牌的 sub / tenant 声明）。
 * 都未配置时后端放行（开放模式），此时前端不带凭据也能正常访问。
 */

export type AuthMode = 'apiKey' | 'bearer';

export interface Credential {
  mode: AuthMode;
  value: string;
  /** API Key 模式下的操作人声明（可选）。 */
  userId?: string;
  /** API Key 模式下的租户声明（可选）。 */
  tenantId?: string;
}

const STORAGE_KEY = 'netbpm.admin.credential.v1';

export function getCredential(): Credential | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);

    if (raw === null) {
      return null;
    }

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

export function setCredential(credential: Credential): void {
  sessionStorage.setItem(STORAGE_KEY, JSON.stringify(credential));
}

export function clearCredential(): void {
  sessionStorage.removeItem(STORAGE_KEY);
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
 * 校验凭据是否有效：用 /api/admin/info 做探针。
 * 配置了鉴权时，错误凭据 / 缺失凭据会返回 401；开放模式下恒为 200（凭据直接可用）。
 */
export async function verifyCredential(credential: Credential): Promise<boolean> {
  try {
    const response = await fetch('/api/admin/info', { headers: credentialHeaders(credential) });

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

  if (payload === null) {
    return { scopes: [] };
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
