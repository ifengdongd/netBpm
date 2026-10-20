import {
  AppstoreOutlined,
  ApartmentOutlined,
  AuditOutlined,
  CloudServerOutlined,
  DashboardOutlined,
  EyeOutlined,
  LogoutOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { App, Button, Layout, Menu, Space, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';

import { AUTH_EXPIRED_EVENT } from '../api/client';
import { ADMIN_SCOPE, clearCredential, credentialLabel, getCredential, resolveIdentity } from '../auth';

const { Sider, Header, Content } = Layout;

const menuItems = [
  { key: '/models', icon: <AppstoreOutlined />, label: '流程模型' },
  { key: '/process-types', icon: <ApartmentOutlined />, label: '流程类型' },
  { key: '/deployments', icon: <CloudServerOutlined />, label: '部署管理' },
  { key: '/monitoring', icon: <EyeOutlined />, label: '实例监控' },
  { key: '/audit', icon: <AuditOutlined />, label: '审计日志' },
  { key: '/metrics', icon: <DashboardOutlined />, label: '管理指标' },
];

export default function AppLayout() {
  const { message } = App.useApp();
  const navigate = useNavigate();
  const location = useLocation();
  const [credential, setCredentialState] = useState(getCredential());
  const identity = resolveIdentity(credential);

  // 取第一段作为高亮 key（/models、/process-types、/deployments、/monitoring）
  const selectedKey = `/${location.pathname.split('/')[1] ?? 'models'}`;

  // 401（凭据失效）软跳转：router 跳登录页而不是整页刷新，保留 SPA 状态
  useEffect(() => {
    const onExpired = () => {
      setCredentialState(null);
      message.warning('登录已失效，请重新登录');

      const redirect = `${window.location.pathname}${window.location.search}`;
      navigate(`/login?redirect=${encodeURIComponent(redirect)}`, { replace: true });
    };

    window.addEventListener(AUTH_EXPIRED_EVENT, onExpired);
    return () => window.removeEventListener(AUTH_EXPIRED_EVENT, onExpired);
  }, [message, navigate]);

  const handleLogout = () => {
    clearCredential();
    setCredentialState(null);
    navigate('/login', { replace: true });
  };

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider theme="dark" width={200}>
        <div style={{ height: 56, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <Typography.Text strong style={{ color: '#fff', fontSize: 16 }}>
            NetBpm 控制台
          </Typography.Text>
        </div>
        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[selectedKey]}
          items={menuItems}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            background: '#fff',
            paddingInline: 24,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
          }}
        >
          <Typography.Title level={4} style={{ margin: 0, lineHeight: '56px' }}>
            {menuItems.find((item) => item.key === selectedKey)?.label ?? '管理控制台'}
          </Typography.Title>
          <Space>
            {credential !== null ? (
              <>
                <Tag icon={<UserOutlined />} color="blue">
                  {credentialLabel(credential)}
                </Tag>
                {identity.user !== undefined && <Tag>{identity.user}</Tag>}
                {identity.tenant !== undefined && <Tag color="geekblue">租户：{identity.tenant}</Tag>}
                {identity.scopes.includes(ADMIN_SCOPE) && <Tag color="gold">admin</Tag>}
                <Button size="small" icon={<LogoutOutlined />} onClick={handleLogout}>
                  退出
                </Button>
              </>
            ) : (
              <Button size="small" icon={<UserOutlined />} onClick={() => navigate('/login')}>
                登录
              </Button>
            )}
          </Space>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  );
}
