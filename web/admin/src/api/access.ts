import { http, toQueryString } from './client';

/**
 * 平台接入管理（`/admin/v1/tenants`、`/admin/v1/clients`）。
 *
 * 与其它页面不同，这一组走**平台管理面**而不是 `/api/admin`：
 * 客户端注册只能在这里做（`/api/admin/clients` 是兼容旧路径，即将下线）。
 * 错误体是 RFC 9457（`code` / `title` / `detail`），`client.ts` 已统一取出可读消息。
 */

/** 租户（`GET /admin/v1/tenants`）。 */
export interface TenantView {
  tenantId: string;
  name: string;
  active: boolean;
  quotaPerMinute: number;
  createdAt: string;
}

/** 客户端状态：后端按枚举**数字**序列化（0..3），不能按字符串直接比较。 */
export const CLIENT_STATUS_LABELS: Record<number, string> = {
  0: '待启用',
  1: '使用中',
  2: '已暂停',
  3: '已注销',
};

export const CLIENT_STATUS_ACTIVE = 1;
export const CLIENT_STATUS_SUSPENDED = 2;

/** 接入系统（`GET /admin/v1/clients`，对应后端 `ClientDescriptor`）。 */
export interface ClientDescriptor {
  clientId: string;
  name: string;
  tenantId: string;
  status: number;
  scopes: string[];
  allowedProcessKeys: string[];
  quotaPerMinute: number;
  createdAt: string;
  secretExpiresAt: string | null;
}

/** 新签发（或轮换）的密钥：**明文只出现这一次**。 */
export interface IssuedClientSecret {
  clientId: string;
  clientSecret: string;
  secretExpiresAt: string | null;
  previousSecretValidUntil: string | null;
}

export interface RegisterTenantRequest {
  name: string;
  quotaPerMinute?: number;
}

export interface RegisterClientRequest {
  name: string;
  tenantId: string;
  scopes: string[];
  allowedProcessKeys?: string[];
  quotaPerMinute?: number;
  secretValidDays?: number;
}

export interface UpdateClientRequest {
  name?: string;
  status?: number;
  scopes?: string[];
  allowedProcessKeys?: string[];
  quotaPerMinute?: number;
}

/**
 * 平台 scope 全集（与后端 `PlatformScopes.All` 同序）。
 *
 * 前端留一份是为了在「注册客户端」时给可选项与提示；**真正校验仍在服务端**，
 * 这里多一个少一个只会影响体验，不会放宽权限。
 */
export const PLATFORM_SCOPES: ReadonlyArray<{ value: string; label: string; privileged?: boolean }> = [
  { value: 'workflow:process:read', label: '读流程目录 / 定义 / 版本 / 表单 schema' },
  { value: 'workflow:instance:read', label: '读实例视图 / 参数快照 / 实例流程图' },
  { value: 'workflow:instance:start', label: '发起流程' },
  { value: 'workflow:instance:terminate', label: '终止实例（高危）', privileged: true },
  { value: 'workflow:task:read', label: '读待办列表与详情' },
  { value: 'workflow:task:submit', label: '提交审批' },
  { value: 'workflow:task:submit:onbehalf', label: '代他人提交（需 token exchange）', privileged: true },
  { value: 'workflow:task:assign', label: '认领 / 取消认领 / 转办' },
  { value: 'workflow:history:read', label: '读审批记录 / 已办' },
  { value: 'workflow:history:read:all', label: '跨人查看全部审批历史', privileged: true },
  { value: 'workflow:admin:process', label: '维护流程名片 / 审批语义字典' },
  { value: 'workflow:admin:instance', label: '跳转节点 / 实例迁移', privileged: true },
  { value: 'workflow:admin:variables', label: '直写流程变量（默认不授予）', privileged: true },
  { value: 'workflow:admin:client', label: '租户 / 客户端 / 配额管理', privileged: true },
  { value: 'workflow:admin:audit:read', label: '读管理动作审计', privileged: true },
  { value: 'workflow:event:subscribe', label: '事件订阅与死信重投' },
  { value: 'workflow:external:task', label: '外部任务领取与回执' },
];

/** 需要显式确认的 scope（与后端判定一致：`:admin` 与 `:onbehalf`）。 */
export function isPrivilegedScope(scope: string): boolean {
  return scope.includes(':admin') || scope.endsWith(':onbehalf');
}

export const accessApi = {
  /** 租户列表（绑定租户的凭据只会看到自己）。 */
  listTenants: (signal?: AbortSignal) => http.get<TenantView[]>('/admin/v1/tenants', { signal }),

  /** 登记租户：幂等，已存在时**不覆盖**名称与配额。 */
  upsertTenant: (tenantId: string, request: RegisterTenantRequest) =>
    http.put<TenantView>(`/admin/v1/tenants/${encodeURIComponent(tenantId)}`, request),

  /** 启用 / 停用整租户（停用会让其下全部客户端鉴权失败，数据不动）。 */
  setTenantStatus: (tenantId: string, active: boolean) =>
    http.put<TenantView>(`/admin/v1/tenants/${encodeURIComponent(tenantId)}/status`, { active }),

  /** 接入系统列表。 */
  listClients: (params?: { tenantId?: string }, signal?: AbortSignal) =>
    http.get<ClientDescriptor[]>(`/admin/v1/clients${toQueryString(params)}`, { signal }),

  /**
   * 注册接入系统：响应里的<b>明文密钥只出现这一次</b>。
   *
   * 刻意**不带** `Idempotency-Key`：密钥签发不走幂等回放（服务端也会拒绝缓存），
   * 重复点击就是再发一把密钥，页面用「确认 + 提交中禁用」防止误触。
   *
   * 带 `:admin` / `:onbehalf` 的 scope 必须 `confirmPrivilegedScopes=true`（服务端会 400），
   * 这里由调用方显式勾选后传入 —— 不让页面「默认全勾」把高危权限顺手发出去。
   */
  registerClient: (request: RegisterClientRequest, confirmPrivilegedScopes: boolean) =>
    http.post<IssuedClientSecret>(
      `/admin/v1/clients${toQueryString({ confirmPrivilegedScopes })}`,
      request,
    ),

  /** 更新接入系统（PATCH 局部更新：未传字段表示不改）。 */
  updateClient: (clientId: string, request: UpdateClientRequest) =>
    http.patch<ClientDescriptor>(`/admin/v1/clients/${encodeURIComponent(clientId)}`, request),

  /** 轮换密钥：旧密钥在宽限期内仍有效，给调用方留切换时间。 */
  rotateSecret: (clientId: string) =>
    http.post<IssuedClientSecret>(`/admin/v1/clients/${encodeURIComponent(clientId)}/rotate`),

  /** 注销客户端（记录保留，便于审计回查）。 */
  revokeClient: (clientId: string) =>
    http.del<void>(`/admin/v1/clients/${encodeURIComponent(clientId)}`),
};
