import { CopyOutlined, KeyOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Form,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Select,
  Space,
  Switch,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import {
  accessApi,
  CLIENT_STATUS_ACTIVE,
  CLIENT_STATUS_LABELS,
  CLIENT_STATUS_SUSPENDED,
  isPrivilegedScope,
  PLATFORM_SCOPES,
} from '../api/access';
import type {
  ClientDescriptor,
  IssuedClientSecret,
  RegisterClientRequest,
  RegisterTenantRequest,
  TenantView,
} from '../api/access';

const TENANTS_KEY = ['platform', 'tenants'] as const;
const CLIENTS_KEY = ['platform', 'clients'] as const;

/** 默认租户：初始化脚本自带，新客户端不填租户时落到这里。 */
const DEFAULT_TENANT = 'default';

/**
 * 明文密钥弹窗：**只出现这一次**，关掉就只能轮换。
 */
function SecretModal({
  issued,
  onClose,
}: {
  issued: IssuedClientSecret | null;
  onClose: () => void;
}) {
  const { message } = App.useApp();

  const copy = async () => {
    if (issued === null) {
      return;
    }

    try {
      await navigator.clipboard.writeText(issued.clientSecret);
      message.success('密钥已复制');
    } catch {
      message.warning('浏览器拒绝写入剪贴板，请手动选中复制');
    }
  };

  return (
    <Modal
      title="请立刻保存密钥"
      open={issued !== null}
      onCancel={onClose}
      onOk={() => void copy()}
      okText="复制密钥"
      okButtonProps={{ icon: <CopyOutlined /> }}
      cancelText="我已保存"
      width={620}
      maskClosable={false}
    >
      <Alert
        type="warning"
        showIcon
        message="明文密钥只出现这一次"
        description="关闭后服务端只保留摘要，无法再次查看；丢了只能轮换（旧密钥有宽限期，不会立刻打断对接）。"
        style={{ marginBottom: 16 }}
      />
      <Typography.Paragraph copyable={{ text: issued?.clientSecret }} code style={{ wordBreak: 'break-all' }}>
        {issued?.clientSecret}
      </Typography.Paragraph>
      <Typography.Text type="secondary">
        客户端：{issued?.clientId}
        {issued?.secretExpiresAt ? ` · 有效期至：${new Date(issued.secretExpiresAt).toLocaleString()}` : ''}
        {issued?.previousSecretValidUntil
          ? ` · 旧密钥宽限至：${new Date(issued.previousSecretValidUntil).toLocaleString()}`
          : ''}
      </Typography.Text>
    </Modal>
  );
}

export default function AccessPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();

  const [tenantForm] = Form.useForm<RegisterTenantRequest & { tenantId: string }>();
  const [clientForm] = Form.useForm<RegisterClientRequest>();

  const [tenantModalOpen, setTenantModalOpen] = useState(false);
  const [clientModalOpen, setClientModalOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [issued, setIssued] = useState<IssuedClientSecret | null>(null);
  const [selectedScopes, setSelectedScopes] = useState<string[]>([]);

  const tenantsQuery = useQuery({ queryKey: TENANTS_KEY, queryFn: ({ signal }) => accessApi.listTenants(signal) });
  const clientsQuery = useQuery({ queryKey: CLIENTS_KEY, queryFn: ({ signal }) => accessApi.listClients(undefined, signal) });

  const tenants = useMemo(() => tenantsQuery.data ?? [], [tenantsQuery.data]);
  const clients = useMemo(() => clientsQuery.data ?? [], [clientsQuery.data]);

  useEffect(() => {
    if (tenantsQuery.isError) {
      message.error(`加载租户失败：${(tenantsQuery.error as Error).message}`);
    }
  }, [tenantsQuery.isError, tenantsQuery.error, message]);

  useEffect(() => {
    if (clientsQuery.isError) {
      message.error(`加载接入系统失败：${(clientsQuery.error as Error).message}`);
    }
  }, [clientsQuery.isError, clientsQuery.error, message]);

  const refreshTenants = () => void queryClient.invalidateQueries({ queryKey: TENANTS_KEY });
  const refreshClients = () => void queryClient.invalidateQueries({ queryKey: CLIENTS_KEY });

  const handleCreateTenant = async () => {
    const values = await tenantForm.validateFields();

    if (!/^[a-zA-Z0-9_-]{1,64}$/.test(values.tenantId)) {
      message.error('租户 id 只能包含字母、数字、下划线与短横线（1–64 位）');

      return;
    }

    setSaving(true);

    try {
      await accessApi.upsertTenant(values.tenantId, { name: values.name, quotaPerMinute: values.quotaPerMinute });
      message.success('租户已登记（已存在的租户不会被覆盖）');
      setTenantModalOpen(false);
      tenantForm.resetFields();
      refreshTenants();
    } catch (error) {
      message.error(`登记失败：${(error as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  const handleTenantStatus = async (tenant: TenantView, active: boolean) => {
    try {
      await accessApi.setTenantStatus(tenant.tenantId, active);
      message.success(active ? `已启用租户「${tenant.name}」` : `已停用租户「${tenant.name}」（其下客户端立即鉴权失败）`);
      refreshTenants();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const handleCreateClient = async () => {
    const values = await clientForm.validateFields();
    const privileged = values.scopes.some(isPrivilegedScope);

    setSaving(true);

    try {
      const created = await accessApi.registerClient(
        {
          name: values.name,
          tenantId: values.tenantId || DEFAULT_TENANT,
          scopes: values.scopes,
          allowedProcessKeys: values.allowedProcessKeys,
          quotaPerMinute: values.quotaPerMinute,
          secretValidDays: values.secretValidDays,
        },
        privileged,
      );

      setClientModalOpen(false);
      clientForm.resetFields();
      setSelectedScopes([]);
      setIssued(created);
      refreshClients();
      refreshTenants();
    } catch (error) {
      message.error(`注册失败：${(error as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  const handleRotate = async (client: ClientDescriptor) => {
    try {
      const rotated = await accessApi.rotateSecret(client.clientId);
      setIssued(rotated);
      refreshClients();
    } catch (error) {
      message.error(`轮换失败：${(error as Error).message}`);
    }
  };

  const handleStatus = async (client: ClientDescriptor, active: boolean) => {
    try {
      await accessApi.updateClient(client.clientId, {
        status: active ? CLIENT_STATUS_ACTIVE : CLIENT_STATUS_SUSPENDED,
      });
      message.success(active ? `已启用「${client.name}」` : `已暂停「${client.name}」`);
      refreshClients();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const handleRevoke = async (client: ClientDescriptor) => {
    try {
      await accessApi.revokeClient(client.clientId);
      message.success(`已注销「${client.name}」`);
      refreshClients();
    } catch (error) {
      message.error(`注销失败：${(error as Error).message}`);
    }
  };

  const tenantColumns: ColumnsType<TenantView> = [
    { title: '租户 id', dataIndex: 'tenantId', key: 'tenantId', width: 180 },
    {
      title: '名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string) => <Typography.Text strong>{name}</Typography.Text>,
    },
    { title: '默认配额（次/分）', dataIndex: 'quotaPerMinute', key: 'quotaPerMinute', width: 160 },
    {
      title: '状态',
      dataIndex: 'active',
      key: 'active',
      width: 110,
      render: (active: boolean, record) => (
        <Tooltip title={active ? '停用会让其下全部客户端立即鉴权失败（数据不动）' : '启用后其下客户端恢复调用'}>
          <Switch
            size="small"
            checked={active}
            checkedChildren="启用"
            unCheckedChildren="停用"
            onChange={(checked) => void handleTenantStatus(record, checked)}
          />
        </Tooltip>
      ),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: (createdAt: string) => new Date(createdAt).toLocaleString(),
    },
  ];

  const clientColumns: ColumnsType<ClientDescriptor> = [
    {
      title: '接入系统',
      dataIndex: 'name',
      key: 'name',
      render: (name: string, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{name}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.clientId}
          </Typography.Text>
        </Space>
      ),
    },
    { title: '租户', dataIndex: 'tenantId', key: 'tenantId', width: 120 },
    {
      title: 'scope',
      dataIndex: 'scopes',
      key: 'scopes',
      render: (scopes: string[]) => (
        <Space size={[0, 4]} wrap>
          {scopes.map((scope) => (
            <Tag key={scope} color={isPrivilegedScope(scope) ? 'orange' : 'blue'}>
              {scope}
            </Tag>
          ))}
        </Space>
      ),
    },
    {
      title: '可用流程',
      dataIndex: 'allowedProcessKeys',
      key: 'allowedProcessKeys',
      width: 180,
      render: (keys: string[]) =>
        keys.length === 0 ? <Tag>全部授权</Tag> : (
          <Space size={[0, 4]} wrap>
            {keys.map((key) => (
              <Tag key={key}>{key}</Tag>
            ))}
          </Space>
        ),
    },
    { title: '配额（次/分）', dataIndex: 'quotaPerMinute', key: 'quotaPerMinute', width: 130 },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: number) => {
        const label = CLIENT_STATUS_LABELS[status] ?? `未知(${status})`;
        const color = status === CLIENT_STATUS_ACTIVE ? 'success' : status === CLIENT_STATUS_SUSPENDED ? 'warning' : 'default';

        return <Tag color={color}>{label}</Tag>;
      },
    },
    {
      title: '密钥有效期',
      dataIndex: 'secretExpiresAt',
      key: 'secretExpiresAt',
      width: 180,
      render: (expiresAt: string | null) => (expiresAt ? new Date(expiresAt).toLocaleString() : '长期'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 230,
      render: (_, record) => (
        <Space wrap>
          <Tooltip title="换一把密钥（旧密钥有宽限期）">
            <Button size="small" icon={<KeyOutlined />} onClick={() => void handleRotate(record)} />
          </Tooltip>
          <Popconfirm
            title={record.status === CLIENT_STATUS_ACTIVE ? '暂停该接入系统？' : '启用该接入系统？'}
            okText="确定"
            cancelText="取消"
            onConfirm={() => void handleStatus(record, record.status !== CLIENT_STATUS_ACTIVE)}
          >
            <Button size="small">{record.status === CLIENT_STATUS_ACTIVE ? '暂停' : '启用'}</Button>
          </Popconfirm>
          <Popconfirm
            title={`注销「${record.name}」？`}
            description="密钥立即吊销；记录保留用于审计回查。"
            okText="注销"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void handleRevoke(record)}
          >
            <Button size="small" danger>
              注销
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const hasPrivileged = selectedScopes.some(isPrivilegedScope);

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message="接入管理走平台面 /admin/v1"
        description="这里注册的每个接入系统都有独立 scope、流程白名单与配额；密钥只在创建 / 轮换时出现一次。旧路径 /api/admin/clients 仅保留兼容。"
      />

      <Tabs
        items={[
          {
            key: 'clients',
            label: `接入系统（${clients.length}）`,
            children: (
              <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                <Space style={{ width: '100%', justifyContent: 'space-between' }}>
                  <Typography.Text type="secondary">一系统一客户端：配额与审计按客户端记账。</Typography.Text>
                  <Space>
                    <Button icon={<ReloadOutlined />} onClick={refreshClients}>
                      刷新
                    </Button>
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setClientModalOpen(true)}>
                      注册接入系统
                    </Button>
                  </Space>
                </Space>
                <Table<ClientDescriptor>
                  rowKey="clientId"
                  columns={clientColumns}
                  dataSource={clients}
                  loading={clientsQuery.isFetching}
                  pagination={{ pageSize: 20, showSizeChanger: false }}
                  scroll={{ x: 1100 }}
                />
              </Space>
            ),
          },
          {
            key: 'tenants',
            label: `租户（${tenants.length}）`,
            children: (
              <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                <Space style={{ width: '100%', justifyContent: 'space-between' }}>
                  <Typography.Text type="secondary">
                    租户是配额与数据隔离的边界；停用是整租户止血开关（数据不动，可恢复）。
                  </Typography.Text>
                  <Space>
                    <Button icon={<ReloadOutlined />} onClick={refreshTenants}>
                      刷新
                    </Button>
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setTenantModalOpen(true)}>
                      登记租户
                    </Button>
                  </Space>
                </Space>
                <Table<TenantView>
                  rowKey="tenantId"
                  columns={tenantColumns}
                  dataSource={tenants}
                  loading={tenantsQuery.isFetching}
                  pagination={{ pageSize: 20, showSizeChanger: false }}
                />
              </Space>
            ),
          },
        ]}
      />

      <Modal
        title="登记租户"
        open={tenantModalOpen}
        confirmLoading={saving}
        onOk={() => void handleCreateTenant()}
        onCancel={() => setTenantModalOpen(false)}
        okText="登记"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={tenantForm} layout="vertical" initialValues={{ quotaPerMinute: 600 }}>
          <Form.Item
            name="tenantId"
            label="租户 id"
            rules={[{ required: true, message: '请输入租户 id' }]}
            extra="字母 / 数字 / 下划线 / 短横线，1–64 位；已存在的租户不会被覆盖。"
          >
            <Input placeholder="如 hr" />
          </Form.Item>
          <Form.Item name="name" label="名称" rules={[{ required: true, whitespace: true, message: '请输入名称' }]}>
            <Input placeholder="如 人力资源部" />
          </Form.Item>
          <Form.Item name="quotaPerMinute" label="默认配额（次/分）" extra="客户端未单独配置时取此值。">
            <InputNumber min={1} max={100000} style={{ width: '100%' }} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="注册接入系统"
        open={clientModalOpen}
        confirmLoading={saving}
        onOk={() => void handleCreateClient()}
        onCancel={() => {
          setClientModalOpen(false);
          setSelectedScopes([]);
        }}
        okText="注册并签发密钥"
        cancelText="取消"
        width={680}
        destroyOnClose
      >
        <Form
          form={clientForm}
          layout="vertical"
          initialValues={{ tenantId: DEFAULT_TENANT, quotaPerMinute: 600, secretValidDays: 90 }}
          onValuesChange={(changed: Partial<RegisterClientRequest>) => {
            if (changed.scopes !== undefined) {
              setSelectedScopes(changed.scopes);
            }
          }}
        >
          <Form.Item name="name" label="接入系统名称" rules={[{ required: true, whitespace: true, message: '请输入名称' }]}>
            <Input placeholder="如 OA 门户 / 招聘系统" />
          </Form.Item>
          <Form.Item
            name="tenantId"
            label="所属租户"
            rules={[{ required: true, message: '请输入租户' }]}
            extra="绑定租户后只能访问该租户的数据；跨租户运营视角属于未绑定租户的平台凭据。"
          >
            <Input placeholder={DEFAULT_TENANT} />
          </Form.Item>
          <Form.Item
            name="scopes"
            label="授权 scope"
            rules={[{ required: true, message: '至少选择一个 scope' }]}
            extra="橙色为高危项（需显式确认，服务端会二次校验）。"
          >
            <Select
              mode="multiple"
              allowClear
              optionFilterProp="label"
              placeholder="选择该接入系统需要的 scope"
              options={PLATFORM_SCOPES.map((scope) => ({
                value: scope.value,
                label: `${scope.value} — ${scope.label}`,
              }))}
            />
          </Form.Item>
          {hasPrivileged ? (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 16 }}
              message="包含高危 scope"
              description="管理类 scope 会让该接入系统能修改配置或读全量审计；确认确有必要再授予。"
            />
          ) : null}
          <Form.Item
            name="allowedProcessKeys"
            label="可用流程 key"
            extra="留空 = 授权全部流程；白名单是中台隔离的核心手段。"
          >
            <Select mode="tags" tokenSeparators={[',', ' ']} placeholder="如 leave_process" />
          </Form.Item>
          <Space size="large">
            <Form.Item name="quotaPerMinute" label="配额（次/分）">
              <InputNumber min={1} max={100000} />
            </Form.Item>
            <Form.Item name="secretValidDays" label="密钥有效期（天）">
              <InputNumber min={1} max={3650} />
            </Form.Item>
          </Space>
        </Form>
      </Modal>

      <SecretModal issued={issued} onClose={() => setIssued(null)} />
    </Space>
  );
}
