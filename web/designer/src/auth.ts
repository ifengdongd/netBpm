/**
 * 读取管理控制台共享的鉴权凭据（同一源下与 web/admin 共用一个 localStorage 键），
 * 生成要附加到每个请求的鉴权头。设计器自身不做登录，凭据由外层管理控制台的登录页写入；
 * 开放模式（后端未配置鉴权）下该键不存在，返回空头即可。
 */
const CREDENTIAL_KEY = 'netbpm.admin.credential.v1';

interface SharedCredential {
  mode?: 'apiKey' | 'bearer';
  value?: string;
  userId?: string;
  tenantId?: string;
}

export function credentialHeaders(): Record<string, string> {
  try {
    const raw = localStorage.getItem(CREDENTIAL_KEY);

    if (raw === null) {
      return {};
    }

    const credential = JSON.parse(raw) as SharedCredential;

    if (typeof credential.value !== 'string' || credential.value.length === 0) {
      return {};
    }

    if (credential.mode === 'bearer') {
      return { Authorization: `Bearer ${credential.value}` };
    }

    // 缺省按 API Key 处理
    const headers: Record<string, string> = { 'X-Api-Key': credential.value };

    if (credential.userId) {
      headers['X-User-Id'] = credential.userId;
    }

    if (credential.tenantId) {
      headers['X-Tenant-Id'] = credential.tenantId;
    }

    return headers;
  } catch {
    return {};
  }
}

/** 读设计器数据（定义 / 部署 / 回显 XML）所需的管理 scope。 */
export const MODEL_READ_SCOPE = 'netbpm.admin.model.read';

/** 保存模型、部署、定义挂起 / 删除部署所需的管理 scope。 */
export const MODEL_WRITE_SCOPE = 'netbpm.admin.model.write';

/** 403 的补充说明：后端只回 "Forbidden" 时，用户不知道缺哪个 scope。 */
export function forbiddenHint(requiredScope: string): string {
  return `缺少管理权限 scope「${requiredScope}」（或超级范围 netbpm.admin），请重新登录或联系管理员授权。`;
}

/**
 * 把 HTTP 失败整理成可展示文案；403 且后端未给出 scope 说明时追加所需 scope。
 *
 * 纯函数，单测直接调用（见 test/designer.test.ts）。
 */
export function describeHttpFailure(status: number, detail: string, requiredScope: string): string {
  const base = detail.trim().length > 0 ? detail : `HTTP ${status}`;

  if (status !== 403 || base.includes('scope')) {
    return base;
  }

  return `${base}（${forbiddenHint(requiredScope)}）`;
}
