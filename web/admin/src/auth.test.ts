import { beforeEach, describe, expect, it } from 'vitest';

import {
  ADMIN_SCOPE,
  clearCredential,
  credentialHeaders,
  getCredential,
  resolveIdentity,
  setCredential,
} from './auth';

/** 构造一个 base64url 的 JWT 形状令牌（签名部分随意，前端不验签）。 */
function fakeJwt(payload: object): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  return `${encode({ alg: 'HS256' })}.${encode(payload)}.signature`;
}

describe('凭据存取', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('保存 / 读取 / 清除凭据', () => {
    expect(getCredential()).toBeNull();

    setCredential({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 't1' });

    expect(getCredential()).toEqual({ mode: 'apiKey', value: 'k-123', userId: 'ops', tenantId: 't1' });

    clearCredential();

    expect(getCredential()).toBeNull();
  });

  it('损坏或形状不对的存储值一律视为未登录', () => {
    localStorage.setItem('netbpm.admin.credential.v1', '{not-json');

    expect(getCredential()).toBeNull();

    localStorage.setItem('netbpm.admin.credential.v1', JSON.stringify({ mode: 'apiKey', value: '' }));

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
