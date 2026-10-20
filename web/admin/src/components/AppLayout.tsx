import {
  AppstoreOutlined,
  ApartmentOutlined,
  CloudServerOutlined,
  EyeOutlined,
  LogoutOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Button, Layout, Menu, Space, Tag, Typography } from 'antd';
import { useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';

import { ADMIN_SCOPE, clearCredential, credentialLabel, getCredential, resolveIdentity } from '../auth';

const { Sider, Header, Content } = Layout;

const menuItems = [
  { key: '/models', icon: <AppstoreOutlined />, label: '流程模型' },
  { key: '/process-types', icon: <ApartmentOutlined />, label: '流程类型' },
  { key: '/deployments', icon: <CloudServerOutlined />, label: '部署管理' },
  { key: '/monitoring', icon: <EyeOutlined />, label: '实例监控' },
];

export default function AppLayout() {
  const navigate = useNavigate();
  const location = useLocation();
  const [credential, setCredentialState] = useState(getCredential());
  const identity = resolveIdentity(credential);

  // 取第一段作为高亮 key（/models、/process-types、/deployments、/monitoring）
  const selectedKey = `/${location.pathname.split('/')[1] ?? 'models'}`;

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
