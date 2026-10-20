import { LockOutlined } from '@ant-design/icons';
import { Alert, Button, Card, Form, Input, Segmented, Space, Typography } from 'antd';
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';

import { setCredential, verifyCredential, type AuthMode } from '../auth';

interface LoginFormValues {
  value: string;
  userId?: string;
  tenantId?: string;
}

export default function LoginPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const [mode, setMode] = useState<AuthMode>('apiKey');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const rawRedirect = searchParams.get('redirect') ?? '/models';
  // 只允许同源站内路径，防开放重定向
  const redirect = rawRedirect.startsWith('/') && !rawRedirect.startsWith('//') ? rawRedirect : '/models';

  const handleSubmit = async (values: LoginFormValues) => {
    setSubmitting(true);
    setError(null);

    const ok = await verifyCredential({
      mode,
      value: values.value.trim(),
      userId: mode === 'apiKey' ? values.userId?.trim() || undefined : undefined,
      tenantId: mode === 'apiKey' ? values.tenantId?.trim() || undefined : undefined,
    });

    if (ok) {
      setCredential({
        mode,
        value: values.value.trim(),
        userId: mode === 'apiKey' ? values.userId?.trim() || undefined : undefined,
        tenantId: mode === 'apiKey' ? values.tenantId?.trim() || undefined : undefined,
      });
      navigate(redirect, { replace: true });
    } else {
      setError('凭据校验失败：后端返回 401（密钥无效，或未配置鉴权但后端不可达）。');
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
            <Typography.Text type="secondary">登录以访问流程管理（X-Api-Key / JWT）</Typography.Text>
          </Space>

          {error !== null && <Alert type="error" message={error} showIcon />}

          <Segmented
            block
            options={[
              { label: 'API Key（服务间）', value: 'apiKey' },
              { label: 'Bearer Token（JWT）', value: 'bearer' },
            ]}
            value={mode}
            onChange={(value) => setMode(value as AuthMode)}
          />

          <Form<LoginFormValues> layout="vertical" onFinish={(values) => void handleSubmit(values)}>
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

            <Button type="primary" htmlType="submit" block loading={submitting}>
              登录
            </Button>
          </Form>
        </Space>
      </Card>
    </div>
  );
}
