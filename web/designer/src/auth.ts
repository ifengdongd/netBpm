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
