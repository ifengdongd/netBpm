import { ReloadOutlined } from '@ant-design/icons';
import { Alert, App, Button, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useInfiniteQuery } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';

import { quotasApi } from '../api/quotas';
import type { ClientQuotaView, TenantQuotaView } from '../api/quotas';

const PAGE_SIZE = 50;

const STATUS_COLORS: Record<string, string> = {
  ACTIVE: 'success',
  SUSPENDED: 'warning',
  PENDING: 'processing',
  REVOKED: 'default',
};

/**
 * 配额总览。
 *
 * 两个层级分开展示：租户默认值是「配置对不对」，客户端实际值是「谁在吃配额」。
 * 两边各自游标首页 + 加载更多，避免把租户数 × 客户端数的乘积一次性拉回来。
 */
export default function QuotasPage() {
  const { message } = App.useApp();

  const tenantsQuery = useInfiniteQuery({
    queryKey: ['platform', 'quotas', 'tenants'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => quotasApi.get({ tenantCursor: pageParam, max: PAGE_SIZE }, signal),
    getNextPageParam: (lastPage) => lastPage.tenantsNextCursor ?? undefined,
  });

  const clientsQuery = useInfiniteQuery({
    queryKey: ['platform', 'quotas', 'clients'],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => quotasApi.get({ clientCursor: pageParam, max: PAGE_SIZE }, signal),
    getNextPageParam: (lastPage) => lastPage.clientsNextCursor ?? undefined,
  });

  const tenants = useMemo(
    () => (tenantsQuery.data?.pages ?? []).flatMap((page) => page.tenants),
    [tenantsQuery.data],
  );

  const clients = useMemo(
    () => (clientsQuery.data?.pages ?? []).flatMap((page) => page.clients),
    [clientsQuery.data],
  );

  const overview = tenantsQuery.data?.pages[0] ?? clientsQuery.data?.pages[0];

  useEffect(() => {
    if (tenantsQuery.isError) {
      message.error(`加载租户配额失败：${(tenantsQuery.error as Error).message}`);
    }
  }, [tenantsQuery.isError, tenantsQuery.error, message]);

  useEffect(() => {
    if (clientsQuery.isError) {
      message.error(`加载客户端配额失败：${(clientsQuery.error as Error).message}`);
    }
  }, [clientsQuery.isError, clientsQuery.error, message]);

  const refresh = () => {
    void tenantsQuery.refetch();
    void clientsQuery.refetch();
  };

  const tenantColumns: ColumnsType<TenantQuotaView> = [
    { title: '租户', dataIndex: 'tenantId', key: 'tenantId', width: 160 },
    {
      title: '名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string) => <Typography.Text strong>{name}</Typography.Text>,
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: string) => <Tag color={STATUS_COLORS[status] ?? 'default'}>{status}</Tag>,
    },
    { title: '默认配额（次/分）', dataIndex: 'quotaPerMinute', key: 'quotaPerMinute', width: 160 },
    {
      title: '接入系统数',
      dataIndex: 'clientCount',
      key: 'clientCount',
      width: 130,
      // 按全量统计（不是本页行数）：分页之后这一点必须由后端算
      render: (count: number) => <Typography.Text strong>{count}</Typography.Text>,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: (createdAt: string) => new Date(createdAt).toLocaleString(),
    },
  ];

  const clientColumns: ColumnsType<ClientQuotaView> = [
    {
      title: '接入系统',
      key: 'client',
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{record.name}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.clientId}
          </Typography.Text>
        </Space>
      ),
    },
    { title: '租户', dataIndex: 'tenantId', key: 'tenantId', width: 130 },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (status: string) => <Tag color={STATUS_COLORS[status] ?? 'default'}>{status}</Tag>,
    },
    {
      title: '配额（次/分）',
      dataIndex: 'quotaPerMinute',
      key: 'quotaPerMinute',
      width: 140,
      render: (quota: number, record) => {
        // 与所属租户默认值一致时灰显，便于一眼看出「哪些是单独设过配额的」
        const tenant = tenants.find((item) => item.tenantId === record.tenantId);
        const inherited = tenant !== undefined && tenant.quotaPerMinute === quota;

        return (
          <Typography.Text type={inherited ? 'secondary' : undefined}>
            {quota}
            {inherited ? '（继承租户）' : ''}
          </Typography.Text>
        );
      },
    },
    {
      title: '可用流程',
      dataIndex: 'allowedProcessKeys',
      key: 'allowedProcessKeys',
      width: 180,
      render: (keys: string[]) =>
        keys.length === 0 ? <Tag>全部授权</Tag> : (
          <Space size={[0, 4]} wrap>
            {keys.slice(0, 3).map((key) => (
              <Tag key={key}>{key}</Tag>
            ))}
            {keys.length > 3 ? <Tag>+{keys.length - 3}</Tag> : null}
          </Space>
        ),
    },
    {
      title: '密钥到期',
      dataIndex: 'secretExpiresAt',
      key: 'secretExpiresAt',
      width: 170,
      render: (expiresAt: string | null) => (expiresAt ? new Date(expiresAt).toLocaleString() : '长期'),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type={overview?.rateLimitEnabled === false ? 'warning' : 'info'}
        showIcon
        message={
          overview === undefined
            ? '配额配置加载中……'
            : overview.rateLimitEnabled
              ? `限流已启用：未单独配置的客户端按默认 ${overview.defaultPerMinute} 次/分`
              : '限流未启用（NetBpm:RateLimit:Enabled=false）：下面的配额值当前不生效'
        }
        description="配额按客户端记账（一系统一客户端）；租户配额是该租户下客户端的默认值。两个表各自分页，加载更多不会重复拉取。"
      />

      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">
          {tenants.length} 个租户 / {clients.length} 个接入系统（已加载）
        </Typography.Text>
        <Button
          icon={<ReloadOutlined />}
          loading={tenantsQuery.isFetching || clientsQuery.isFetching}
          onClick={refresh}
        >
          刷新
        </Button>
      </Space>

      <Space direction="vertical" size={4} style={{ width: '100%' }}>
        <Typography.Text strong>租户默认配额</Typography.Text>
        <Table<TenantQuotaView>
          rowKey="tenantId"
          columns={tenantColumns}
          dataSource={tenants}
          loading={tenantsQuery.isFetching}
          pagination={false}
          size="small"
        />
        <Button
          block
          type="dashed"
          disabled={!tenantsQuery.hasNextPage}
          loading={tenantsQuery.isFetchingNextPage}
          onClick={() => void tenantsQuery.fetchNextPage()}
        >
          {tenantsQuery.hasNextPage ? '加载更多租户' : '已全部加载'}
        </Button>
      </Space>

      <Space direction="vertical" size={4} style={{ width: '100%' }}>
        <Typography.Text strong>接入系统实际配额</Typography.Text>
        <Table<ClientQuotaView>
          rowKey="clientId"
          columns={clientColumns}
          dataSource={clients}
          loading={clientsQuery.isFetching}
          pagination={false}
          size="small"
          scroll={{ x: 900 }}
        />
        <Button
          block
          type="dashed"
          disabled={!clientsQuery.hasNextPage}
          loading={clientsQuery.isFetchingNextPage}
          onClick={() => void clientsQuery.fetchNextPage()}
        >
          {clientsQuery.hasNextPage ? '加载更多接入系统' : '已全部加载'}
        </Button>
      </Space>
    </Space>
  );
}
