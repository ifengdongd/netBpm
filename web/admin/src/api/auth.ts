import { http } from './client';

/**
 * 账号密码登录（<c>POST /api/auth/login</c>）。
 *
 * 后端校验 `admin_user` 里的账号后，用与鉴权中间件同一套密钥签发 JWT，
 * 所以这里拿到的令牌可以直接当 Bearer 用（与粘贴外部 JWT 的口径一致）。
 *
 * 失败语义（ApiError）：
 *   - 401 invalid_credentials：登录名或口令错误（后端统一文案，不透露账号是否存在）；
 *   - 403 account_disabled / tenant_mismatch；
 *   - 429 account_locked：连续失败次数达上限被临时锁定；
 *   - 503 jwt_not_configured：后端没配 NetBpm:Security:Jwt:SigningKey，签不出令牌。
 */
export interface LoginPayload {
  username: string;
  password: string;
  /** 期望登录到的租户：账号绑定了租户时必须一致。 */
  tenantId?: string;
}

export interface LoginResult {
  /** 访问令牌，放到 Authorization: Bearer <token>。 */
  token: string;
  /** 过期时间（ISO，UTC）。 */
  expiresAt: string;
  user: string;
  displayName?: string | null;
  tenant?: string | null;
  scopes: string[];
}

/** 账号密码登录，成功返回令牌与身份。 */
export function login(payload: LoginPayload): Promise<LoginResult> {
  return http.post<LoginResult>('/api/auth/login', payload);
}

/** `GET /api/auth/me` 回显的当前身份（登录页与顶栏自检用）。 */
export interface CurrentIdentity {
  user?: string | null;
  tenant?: string | null;
  authMode: string;
  scopes: string[];
  isAdmin: boolean;
}

/** 用当前凭据回显身份：凭据是否仍然有效，一问便知。 */
export function fetchCurrentIdentity(): Promise<CurrentIdentity> {
  return http.get<CurrentIdentity>('/api/auth/me');
}
