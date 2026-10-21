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
import { Alert, App, Button, Layout, Menu, Space, Tag, Typography } from 'antd';
import { useEffect, useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';

import { AUTH_EXPIRED_EVENT } from '../api/client';
import { STALE_ADMIN_HINT, useAdminHostCheck, WRONG_HOST_HINT } from '../api/hostCheck';
import {
  ADMIN_SCOPE,
  clearCredential,
  credentialLabel,
  credentialPersistence,
  getCredential,
  isCredentialExpired,
  resolveIdentity,
  subscribeCredential,
} from '../auth';

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
  // 初始读取时就把过期令牌当作未登录（避免顶栏闪一下「已登录」再跳走）
  const [credential, setCredentialState] = useState(() => {
    const current = getCredential();

    return isCredentialExpired(current) ? null : current;
  });
  const identity = resolveIdentity(credential);
  const persistence = credentialPersistence();

  // 宿主自检：指到 NetBpm.Rest（5080）时管理端点全是 404，各页面只会报 Not Found，
  // 这里统一置顶说明，不用再逐页猜。
  const hostCheck = useAdminHostCheck();

  // 取第一段作为高亮 key（/models、/process-types、/deployments、/monitoring）
  const selectedKey = `/${location.pathname.split('/')[1] ?? 'models'}`;

  // 跨标签页同步：别处登录 / 退出后，本页顶栏跟着变（新标签页也因此免登录）
  useEffect(() => subscribeCredential(() => setCredentialState(getCredential())), []);

  // 令牌过期：本地先判一次，不等服务端 401（刷新页面时能立刻回到登录页）
  useEffect(() => {
    const current = getCredential();

    if (current === null || !isCredentialExpired(current)) {
      return;
    }

    clearCredential();
    message.warning('登录已过期，请重新登录');

    const redirect = `${window.location.pathname}${window.location.search}`;
    navigate(`/login?redirect=${encodeURIComponent(redirect)}`, { replace: true });
  }, [message, navigate]);

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
                {credential.displayName !== undefined && <Tag>{credential.displayName}</Tag>}
                {identity.user !== undefined && <Tag>{identity.user}</Tag>}
                {persistence === 'session' && <Tag color="orange">仅本标签页</Tag>}
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
          {hostCheck === 'missingAdmin' && (
            <Alert
              banner
              type="error"
              showIcon
              style={{ marginBottom: 16 }}
              message={WRONG_HOST_HINT}
              description="管理端点（/api/admin/**）只在 NetBpm.Admin 提供；Rest 宿主（默认 5080）只有 designer / runtime / identity。开发期请用 NETBPM_API_TARGET=http://localhost:5081 npm run dev 并重启 Vite；生产部署请把控制台指向 Admin 宿主。"
            />
          )}
          {hostCheck === 'staleAdmin' && (
            <Alert
              banner
              type="warning"
              showIcon
              style={{ marginBottom: 16 }}
              message={STALE_ADMIN_HINT}
              description="常见于：前端已重新构建，但后端仍是旧进程（dotnet run --no-build / 发布目录未更新）。停掉当前 Admin 进程后重新启动（dotnet run --project src/NetBpm.Admin）即可。"
            />
          )}
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  );
}
