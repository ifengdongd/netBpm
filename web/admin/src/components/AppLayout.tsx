import {
  AppstoreOutlined,
  ApartmentOutlined,
  AuditOutlined,
  CloudServerOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  EyeOutlined,
  KeyOutlined,
  LogoutOutlined,
  NotificationOutlined,
  ReadOutlined,
  SafetyCertificateOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { Alert, App, Button, Layout, Menu, Space, Tag, Tooltip, Typography } from 'antd';
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
  // 接入管理：租户与客户端注册（/admin/v1），密钥签发只在这里做
  { key: '/access', icon: <KeyOutlined />, label: '接入管理' },
  // 审批语义字典：把流程私有 optionId 翻译成平台稳定语义（结论统计的口径在这里定）
  { key: '/semantics', icon: <ReadOutlined />, label: '审批字典' },
  // 事件订阅：outbox 投递配置、投递记录与死信重投
  { key: '/subscriptions', icon: <NotificationOutlined />, label: '事件订阅' },
  { key: '/quotas', icon: <ThunderboltOutlined />, label: '配额总览' },
  { key: '/monitoring', icon: <EyeOutlined />, label: '实例监控' },
  { key: '/audit', icon: <AuditOutlined />, label: '审计日志' },
  { key: '/metrics', icon: <DashboardOutlined />, label: '管理指标' },
  // 运维诊断：作业队列走 Redis 时这里能直接看到键与连接状态（内存队列则如实提示「未使用 Redis」）
  { key: '/redis', icon: <DatabaseOutlined />, label: 'Redis 诊断' },
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

  // 顶栏只留一个身份标签：优先显示名 → 登录名 → 鉴权方式（API Key / Bearer Token），
  // 用户名、租户、范围、有效期都收进悬浮提示，避免同一份信息摊成四五个 Tag。
  const isAdmin = identity.scopes.includes(ADMIN_SCOPE);
  const identityName = credential === null
    ? null
    : credential.displayName ?? identity.user ?? credentialLabel(credential);

  const identityHint = [
    `鉴权方式：${credential === null ? '-' : credentialLabel(credential)}`,
    `登录名：${identity.user ?? '-'}`,
    `租户：${identity.tenant ?? '不限'}`,
    `授权范围：${identity.scopes.length > 0 ? identity.scopes.join(' ') : '-'}`,
    credential?.expiresAt !== undefined
      ? `有效期至：${new Date(credential.expiresAt).toLocaleString()}`
      : null,
    persistence === 'session' ? '存储：仅本标签页（关闭即失效）' : '存储：本机共享（跨标签页）',
  ]
    .filter((line) => line !== null)
    .join('\n');

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
          <Space size={8}>
            {credential !== null ? (
              <>
                <Tooltip
                  title={<span style={{ whiteSpace: 'pre-line' }}>{identityHint}</span>}
                  placement="bottomRight"
                >
                  <Tag
                    icon={isAdmin ? <SafetyCertificateOutlined /> : <UserOutlined />}
                    color={isAdmin ? 'gold' : 'blue'}
                    style={{ marginInlineEnd: 0, cursor: 'default' }}
                  >
                    {identityName}
                    {identity.user !== undefined && identity.user !== identityName && (
                      <span style={{ opacity: 0.55, marginLeft: 6, fontSize: 12 }}>{identity.user}</span>
                    )}
                  </Tag>
                </Tooltip>
                {identity.tenant !== undefined && (
                  <Tag color="geekblue" style={{ marginInlineEnd: 0 }}>
                    {identity.tenant}
                  </Tag>
                )}
                {persistence === 'session' && (
                  <Tooltip title="未勾选「保持登录」：凭据只存在本标签页，关闭即失效">
                    <Tag color="orange" style={{ marginInlineEnd: 0 }}>
                      仅本标签页
                    </Tag>
                  </Tooltip>
                )}
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
