import { LockOutlined, UserOutlined } from '@ant-design/icons';
import { Alert, Button, Card, Checkbox, Form, Input, Segmented, Space, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';

import { login } from '../api/auth';
import { ApiError } from '../api/client';
import { setCredential, verifyCredential, type AuthMode } from '../auth';

/** 登录方式：账号密码（走 /api/auth/login）或直接粘贴服务凭据。 */
type LoginMode = 'password' | 'apiKey' | 'bearer';

interface PasswordFormValues {
  username: string;
  password: string;
  tenantId?: string;
  remember?: boolean;
}

interface CredentialFormValues {
  value: string;
  userId?: string;
  tenantId?: string;
  remember?: boolean;
}

/** 后端错误码 → 登录页文案（ApiError.message 兜底）。 */
function describeLoginFailure(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.code) {
      case 'jwt_not_configured':
        return `${error.message}（服务端配置 NetBpm:Security:Jwt:SigningKey 后即可使用账号密码登录；未配置鉴权时为开放模式，直接用「服务凭据」登录即可。）`;
      case 'account_locked':
        return `${error.message}（连续失败次数达上限，锁定期间不再接受登录。）`;
      case 'account_disabled':
        return error.message;
      case 'tenant_mismatch':
        return error.message;
      default:
        break;
    }

    if (error.status === 429) {
      return '登录尝试过于频繁，请稍后再试（后端按 IP 限流）。';
    }

    return error.message;
  }

  return error instanceof Error ? error.message : '登录失败，请稍后重试。';
}

export default function LoginPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [mode, setMode] = useState<LoginMode>('password');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const rawRedirect = searchParams.get('redirect') ?? '/models';
  // 只允许同源站内路径，防开放重定向
  const redirect = rawRedirect.startsWith('/') && !rawRedirect.startsWith('//') ? rawRedirect : '/models';

  const handlePasswordSubmit = async (values: PasswordFormValues) => {
    setSubmitting(true);
    setError(null);

    try {
      const result = await login({
        username: values.username.trim(),
        password: values.password,
        tenantId: values.tenantId?.trim() || undefined,
      });

      // 令牌是后端签发的 JWT：直接当 Bearer 用，身份 / 租户 / 范围都能从令牌里解出
      setCredential(
        {
          mode: 'bearer',
          value: result.token,
          expiresAt: result.expiresAt,
          user: result.user,
          displayName: result.displayName ?? undefined,
          tenantId: result.tenant ?? undefined,
          scopes: result.scopes,
        },
        values.remember === false ? 'session' : 'shared',
      );

      navigate(redirect, { replace: true });
    } catch (err) {
      setError(describeLoginFailure(err));
    } finally {
      setSubmitting(false);
    }
  };

  const handleCredentialSubmit = async (values: CredentialFormValues) => {
    const credential = {
      mode: mode as AuthMode,
      value: values.value.trim(),
      userId: mode === 'apiKey' ? values.userId?.trim() || undefined : undefined,
      tenantId: mode === 'apiKey' ? values.tenantId?.trim() || undefined : undefined,
    };

    setSubmitting(true);
    setError(null);

    try {
      const ok = await verifyCredential(credential);

      if (!ok) {
        setError('凭据校验失败：后端返回 401（密钥无效，或未配置鉴权但后端不可达）。');
        return;
      }

      setCredential(credential, values.remember === false ? 'session' : 'shared');
      navigate(redirect, { replace: true });
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: '#f0f2f5',
      }}
    >
      <Card style={{ width: 420, boxShadow: '0 2px 12px rgba(0,0,0,0.08)' }}>
        <Space direction="vertical" size="large" style={{ width: '100%' }}>
          <Space direction="vertical" size={4} style={{ width: '100%', textAlign: 'center' }}>
            <Typography.Title level={3} style={{ margin: 0 }}>
              NetBpm 控制台
            </Typography.Title>
            <Typography.Text type="secondary">登录以访问流程管理</Typography.Text>
          </Space>

          {error !== null && <Alert type="error" message={error} showIcon />}

          <Segmented
            block
            options={[
              { label: '账号密码', value: 'password' },
              { label: 'API Key', value: 'apiKey' },
              { label: 'Bearer Token', value: 'bearer' },
            ]}
            value={mode}
            onChange={(value) => {
              setMode(value as LoginMode);
              setError(null);
            }}
          />

          {mode === 'password' ? (
            <Form<PasswordFormValues>
              layout="vertical"
              initialValues={{ remember: true }}
              onFinish={(values) => void handlePasswordSubmit(values)}
            >
              <Form.Item
                name="username"
                label="登录名"
                rules={[{ required: true, whitespace: true, message: '请输入登录名' }]}
              >
                <Input prefix={<UserOutlined />} placeholder="如 admin" autoComplete="username" autoFocus />
              </Form.Item>

              <Form.Item
                name="password"
                label="口令"
                rules={[{ required: true, message: '请输入口令' }]}
              >
                <Input.Password
                  prefix={<LockOutlined />}
                  placeholder="口令"
                  autoComplete="current-password"
                />
              </Form.Item>

              <Form.Item name="tenantId" label="租户（可选）">
                <Input placeholder="账号绑定了租户时必填" autoComplete="off" />
              </Form.Item>

              <Form.Item name="remember" valuePropName="checked" style={{ marginBottom: 12 }}>
                <Checkbox>保持登录（跨标签页共享，设计器也可直接读取）</Checkbox>
              </Form.Item>

              <Button type="primary" htmlType="submit" block loading={submitting}>
                登录
              </Button>
            </Form>
          ) : (
            <Form<CredentialFormValues>
              layout="vertical"
              initialValues={{ remember: true }}
              onFinish={(values) => void handleCredentialSubmit(values)}
            >
              <Form.Item
                name="value"
                label={mode === 'apiKey' ? 'API Key' : 'Bearer Token'}
                rules={[{ required: true, whitespace: true, message: '请输入凭据' }]}
              >
                <Input.Password
                  prefix={<LockOutlined />}
                  placeholder={mode === 'apiKey' ? '服务密钥' : 'JWT 令牌'}
                  autoComplete="off"
                />
              </Form.Item>

              {mode === 'apiKey' && (
                <>
                  <Form.Item name="userId" label="操作人（X-User-Id，可选）">
                    <Input placeholder="如 admin" autoComplete="off" />
                  </Form.Item>
                  <Form.Item name="tenantId" label="租户（X-Tenant-Id，可选）">
                    <Input placeholder="如 acme" autoComplete="off" />
                  </Form.Item>
                </>
              )}

              <Form.Item name="remember" valuePropName="checked" style={{ marginBottom: 12 }}>
                <Checkbox>保持登录（跨标签页共享，设计器也可直接读取）</Checkbox>
              </Form.Item>

              <Button type="primary" htmlType="submit" block loading={submitting}>
                登录
              </Button>
            </Form>
          )}

          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            账号由运维用 <code>dotnet run --project tools/NetBpm.DbMigrate -- users add</code> 创建；
            连续失败 5 次会临时锁定账号。
          </Typography.Text>
        </Space>
      </Card>
    </div>
  );
}
