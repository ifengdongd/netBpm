import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  ADMIN_SCOPE,
  clearCredential,
  credentialHeaders,
  credentialPersistence,
  getCredential,
  isCredentialExpired,
  resolveIdentity,
  setCredential,
  verifyCredential,
} from './auth';

/** 构造一个 base64url 的 JWT 形状令牌（签名部分随意，前端不验签）。 */
function fakeJwt(payload: object): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  return `${encode({ alg: 'HS256' })}.${encode(payload)}.signature`;
}

describe('凭据存取', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
  });

  it('默认写入 localStorage：跨标签页共享，与设计器共用一个键', () => {
    setCredential({ mode: 'bearer', value: 't-1' });

    expect(localStorage.getItem('netbpm.admin.credential.v1')).toContain('t-1');
    expect(sessionStorage.getItem('netbpm.admin.credential.v1')).toBeNull();
    expect(credentialPersistence()).toBe('shared');
    expect(getCredential()).toEqual({ mode: 'bearer', value: 't-1' });
  });

  it('session 模式只写 sessionStorage：本标签页优先，关闭即失效', () => {
    localStorage.setItem('netbpm.admin.credential.v1', JSON.stringify({ mode: 'bearer', value: 'shared' }));
    setCredential({ mode: 'bearer', value: 'temporary' }, 'session');

    expect(credentialPersistence()).toBe('session');
    // 读取顺序：临时登录覆盖共享凭据，但共享凭据不被破坏（其它标签页仍可用）
    expect(getCredential()?.value).toBe('temporary');
    expect(localStorage.getItem('netbpm.admin.credential.v1')).toContain('shared');
  });

  it('退出登录：两种存储一起清（一处退出，处处退出）', () => {
    localStorage.setItem('netbpm.admin.credential.v1', JSON.stringify({ mode: 'bearer', value: 'shared' }));
    setCredential({ mode: 'bearer', value: 'temporary' }, 'session');

    clearCredential();

    expect(getCredential()).toBeNull();
    expect(credentialPersistence()).toBeNull();
  });

  it('令牌过期判定：过期视为失效，无过期时间的凭据长期有效', () => {
    const now = Date.parse('2026-10-21T10:00:00Z');

    expect(isCredentialExpired({ mode: 'bearer', value: 't', expiresAt: '2026-10-21T09:59:59Z' }, now)).toBe(true);
    expect(isCredentialExpired({ mode: 'bearer', value: 't', expiresAt: '2026-10-21T10:00:01Z' }, now)).toBe(false);
    expect(isCredentialExpired({ mode: 'bearer', value: 't' }, now)).toBe(false);
    expect(isCredentialExpired({ mode: 'apiKey', value: 'k' }, now)).toBe(false);
    expect(isCredentialExpired(null, now)).toBe(false);
  });

  it('保存 / 读取 / 清除凭据', () => {
    expect(getCredential()).toBeNull();

    setCredential({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 't1' });

    expect(getCredential()).toEqual({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 't1' });

    clearCredential();

    expect(getCredential()).toBeNull();
  });

  it('损坏或形状不对的存储值一律视为未登录', () => {
    sessionStorage.setItem('netbpm.admin.credential.v1', '{not-json');

    expect(getCredential()).toBeNull();

    sessionStorage.setItem('netbpm.admin.credential.v1', JSON.stringify({ mode: 'apiKey', value: '' }));

    expect(getCredential()).toBeNull();
  });

  it('credentialHeaders：API Key 模式带 X-Api-Key，可选声明用户与租户', () => {
    expect(
      credentialHeaders({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 't1' }),
    ).toEqual({ 'X-Api-Key': 'k-123', 'X-User-Id': 'ops', 'X-Tenant-Id': 't1' });

    expect(credentialHeaders({ mode: 'apiKey', value: 'k-123' })).toEqual({ 'X-Api-Key': 'k-123' });
  });

  it('credentialHeaders：Bearer 模式带 Authorization 头，不带租户头（租户在令牌里）', () => {
    const token = fakeJwt({ sub: 'admin' });

    expect(credentialHeaders({ mode: 'bearer', value: token })).toEqual({
      Authorization: `Bearer ${token}`,
    });
  });

  it('无凭据时请求头为空对象', () => {
    expect(credentialHeaders(null)).toEqual({});
  });
});

describe('resolveIdentity', () => {
  it('API Key 凭据：user / tenant 来自用户填写的声明', () => {
    const identity = resolveIdentity({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 'acme' });

    expect(identity).toEqual({ user: 'ops', tenant: 'acme', scopes: [] });
  });

  it('Bearer 凭据：从 JWT 载荷提取用户、租户与范围', () => {
    const identity = resolveIdentity({
      mode: 'bearer',
      value: fakeJwt({ sub: 'alice', tenant: 'acme', scope: `${ADMIN_SCOPE} netbpm.read` }),
    });

    expect(identity).toEqual({ user: 'alice', tenant: 'acme', scopes: [ADMIN_SCOPE, 'netbpm.read'] });
  });

  it('Bearer 凭据：scope 兼容逗号与 JSON 数组两种写法', () => {
    const comma = resolveIdentity({
      mode: 'bearer',
      value: fakeJwt({ scope: `${ADMIN_SCOPE}, netbpm.read` }),
    });

    expect(comma.scopes).toEqual([ADMIN_SCOPE, 'netbpm.read']);

    const array = resolveIdentity({
      mode: 'bearer',
      value: fakeJwt({ scp: [ADMIN_SCOPE, 'netbpm.read'] }),
    });

    expect(array.scopes).toEqual([ADMIN_SCOPE, 'netbpm.read']);
  });

  it('非法 JWT 或无凭据时只返回空范围', () => {
    expect(resolveIdentity({ mode: 'bearer', value: 'not-a-jwt' })).toEqual({ scopes: [] });
    expect(resolveIdentity(null)).toEqual({ scopes: [] });
  });
});

describe('verifyCredential', () => {
  const originalFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  /** 用固定状态码替换 fetch，返回读取「被请求路径」的取数函数。 */
  function stubFetch(status: number): () => string[] {
    const requested: string[] = [];

    globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
      requested.push(String(input));

      return { ok: status >= 200 && status < 300, status } as unknown as Response;
    }) as unknown as typeof fetch;

    return () => requested;
  }

  it('探针打 /api/auth/me：它受鉴权保护，401 才能代表“凭据无效”', async () => {
    const requested = stubFetch(200);

    await verifyCredential({ mode: 'bearer', value: 't-1' });

    // 反过来钉死：/api/admin/info 与就绪探针同口径、匿名可达恒为 200，
    // 拿它当探针会把错误凭据也判为有效。
    expect(requested()).toEqual(['/api/auth/me']);
  });

  it('200 判为有效、401 判为无效（开放模式下后端恒回 200，凭据直接可用）', async () => {
    stubFetch(200);
    expect(await verifyCredential({ mode: 'bearer', value: 'good' })).toBe(true);

    stubFetch(401);
    expect(await verifyCredential({ mode: 'bearer', value: 'bad' })).toBe(false);
  });

  it('后端不可达时判为无效，不向上抛异常', async () => {
    globalThis.fetch = vi.fn(async () => {
      throw new TypeError('network unreachable');
    }) as unknown as typeof fetch;

    expect(await verifyCredential({ mode: 'apiKey', value: 'k-1' })).toBe(false);
  });
});
