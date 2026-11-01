import { CopyOutlined, DeleteOutlined, PlusOutlined, ReloadOutlined, SendOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Descriptions,
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
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import { eventingApi } from '../api/eventing';
import type {
  CreateEventSubscriptionRequest,
  EventDeliveryView,
  EventSubscriptionView,
  IssuedSubscriptionSecret,
} from '../api/eventing';

const SUBSCRIPTIONS_KEY = ['platform', 'subscriptions'] as const;
const DEAD_LETTERS_KEY = ['platform', 'deadletters'] as const;
const STATS_KEY = ['platform', 'event-stats'] as const;

const DELIVERY_COLORS: Record<string, string> = {
  PENDING: 'processing',
  DELIVERED: 'success',
  FAILED: 'warning',
  DEAD_LETTER: 'error',
};

/** 明文密钥弹窗：只出现这一次。 */
function SecretModal({ issued, onClose }: { issued: IssuedSubscriptionSecret | null; onClose: () => void }) {
  const { message } = App.useApp();

  const copy = async () => {
    if (issued === null) {
      return;
    }

    try {
      await navigator.clipboard.writeText(issued.secret);
      message.success('密钥已复制');
    } catch {
      message.warning('浏览器拒绝写入剪贴板，请手动选中复制');
    }
  };

  return (
    <Modal
      title="请立刻保存订阅密钥"
      open={issued !== null}
      onCancel={onClose}
      onOk={() => void copy()}
      okText="复制密钥"
      okButtonProps={{ icon: <CopyOutlined /> }}
      cancelText="我已保存"
      width={600}
      maskClosable={false}
    >
      <Alert
        type="warning"
        showIcon
        message="明文密钥只出现这一次"
        description="关闭后只保留摘要；丢了就调「轮换」重新签发（对端要同步改，旧密钥立即失效）。"
        style={{ marginBottom: 16 }}
      />
      <Typography.Paragraph copyable={{ text: issued?.secret }} code style={{ wordBreak: 'break-all' }}>
        {issued?.secret}
      </Typography.Paragraph>
      <Typography.Text type="secondary">订阅：{issued?.subscriptionId}</Typography.Text>
    </Modal>
  );
}

export default function EventSubscriptionsPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<CreateEventSubscriptionRequest>();

  const [createOpen, setCreateOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [issued, setIssued] = useState<IssuedSubscriptionSecret | null>(null);
  const [deliveryOf, setDeliveryOf] = useState<EventSubscriptionView | null>(null);
  const [deadLetterSubscription, setDeadLetterSubscription] = useState<string | undefined>(undefined);

  const subscriptionsQuery = useQuery({
    queryKey: SUBSCRIPTIONS_KEY,
    queryFn: ({ signal }) => eventingApi.listSubscriptions({ max: 500 }, signal),
  });

  const statsQuery = useQuery({
    queryKey: STATS_KEY,
    queryFn: ({ signal }) => eventingApi.stats(undefined, signal),
  });

  const deadLettersQuery = useQuery({
    queryKey: [...DEAD_LETTERS_KEY, deadLetterSubscription ?? ''],
    queryFn: ({ signal }) =>
      eventingApi.deadLetters({ subscriptionId: deadLetterSubscription, max: 100 }, signal),
  });

  const deliveriesQuery = useQuery({
    queryKey: ['platform', 'deliveries', deliveryOf?.id ?? ''],
    queryFn: ({ signal }) => eventingApi.deliveries(deliveryOf!.id, undefined, signal),
    enabled: deliveryOf !== null,
  });

  const subscriptions = useMemo(() => subscriptionsQuery.data ?? [], [subscriptionsQuery.data]);
  const deadLetters = deadLettersQuery.data?.items ?? [];
  const stats = statsQuery.data;

  useEffect(() => {
    if (subscriptionsQuery.isError) {
      message.error(`加载订阅失败：${(subscriptionsQuery.error as Error).message}`);
    }
  }, [subscriptionsQuery.isError, subscriptionsQuery.error, message]);

  const refreshAll = () => {
    void queryClient.invalidateQueries({ queryKey: SUBSCRIPTIONS_KEY });
    void queryClient.invalidateQueries({ queryKey: STATS_KEY });
    void queryClient.invalidateQueries({ queryKey: DEAD_LETTERS_KEY });
  };

  const handleCreate = async () => {
    const values = await form.validateFields();

    setSaving(true);

    try {
      const created = await eventingApi.create({
        ...values,
        tenantId: values.tenantId?.trim() ? values.tenantId.trim() : null,
        eventTypes: values.eventTypes?.length ? values.eventTypes : null,
      });

      setCreateOpen(false);
      form.resetFields();
      setIssued({ subscriptionId: created.subscription.id, secret: created.secret });
      refreshAll();
    } catch (error) {
      message.error(`创建失败：${(error as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  const handleToggle = async (record: EventSubscriptionView, enabled: boolean) => {
    try {
      await eventingApi.setEnabled(record.id, enabled);
      message.success(enabled ? `已启用「${record.name}」` : `已暂停「${record.name}」（不再投递新事件）`);
      refreshAll();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const handleRotate = async (record: EventSubscriptionView) => {
    try {
      const rotated = await eventingApi.rotate(record.id);
      setIssued(rotated);
      refreshAll();
    } catch (error) {
      message.error(`轮换失败：${(error as Error).message}`);
    }
  };

  const handleDelete = async (record: EventSubscriptionView) => {
    try {
      await eventingApi.remove(record.id);
      message.success(`已删除「${record.name}」`);
      refreshAll();
    } catch (error) {
      message.error(`删除失败：${(error as Error).message}`);
    }
  };

  const handleRetry = async (delivery: EventDeliveryView) => {
    try {
      await eventingApi.retryDelivery(delivery.eventId, delivery.subscriptionId);
      message.success('已重新投递：投递计数会继续累加，超过上限会再次进死信');
      refreshAll();
    } catch (error) {
      message.error(`重投失败：${(error as Error).message}`);
    }
  };

  const subscriptionColumns: ColumnsType<EventSubscriptionView> = [
    {
      title: '订阅',
      key: 'name',
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{record.name}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.id} · {record.tenantId}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '回调地址',
      dataIndex: 'endpointUrl',
      key: 'endpointUrl',
      ellipsis: true,
    },
    {
      title: '事件类型',
      dataIndex: 'eventTypes',
      key: 'eventTypes',
      width: 220,
      render: (eventTypes: string[]) =>
        eventTypes.length === 0 ? <Tag>全部</Tag> : (
          <Space size={[0, 4]} wrap>
            {eventTypes.map((type) => (
              <Tag key={type}>{type}</Tag>
            ))}
          </Space>
        ),
    },
    {
      title: '限速 / 重试',
      key: 'limits',
      width: 130,
      render: (_, record) => (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {record.maxPerMinute === 0 ? '不限速' : `${record.maxPerMinute}/分`} · {record.maxAttempts} 次
        </Typography.Text>
      ),
    },
    {
      title: '密钥',
      dataIndex: 'secretPreview',
      key: 'secretPreview',
      width: 150,
      render: (preview: string) => <Typography.Text code>{preview}</Typography.Text>,
    },
    {
      title: '状态',
      dataIndex: 'enabled',
      key: 'enabled',
      width: 100,
      render: (enabled: boolean, record) => (
        <Switch
          size="small"
          checked={enabled}
          checkedChildren="启用"
          unCheckedChildren="暂停"
          onChange={(checked) => void handleToggle(record, checked)}
        />
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 230,
      render: (_, record) => (
        <Space wrap>
          <Button size="small" onClick={() => setDeliveryOf(record)}>
            投递记录
          </Button>
          <Popconfirm
            title="轮换密钥？"
            description="旧密钥立即失效，对端要同步改；未消费的事件不受影响。"
            okText="轮换"
            cancelText="取消"
            onConfirm={() => void handleRotate(record)}
          >
            <Button size="small">轮换</Button>
          </Popconfirm>
          <Popconfirm
            title={`删除「${record.name}」？`}
            description="删除后不再产生新投递；已产生的投递记录保留。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void handleDelete(record)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const deliveryColumns: ColumnsType<EventDeliveryView> = [
    {
      title: '事件',
      dataIndex: 'eventId',
      key: 'eventId',
      ellipsis: true,
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 130,
      render: (status: string) => <Tag color={DELIVERY_COLORS[status] ?? 'default'}>{status}</Tag>,
    },
    { title: '次数', dataIndex: 'attempts', key: 'attempts', width: 70 },
    {
      title: '下次重试',
      dataIndex: 'nextAttemptAt',
      key: 'nextAttemptAt',
      width: 170,
      render: (value: string | null) => (value ? new Date(value).toLocaleString() : '—'),
    },
    {
      title: '最近错误',
      dataIndex: 'lastError',
      key: 'lastError',
      ellipsis: true,
      render: (value: string | null) => value ?? '—',
    },
    {
      title: '更新时间',
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 170,
      render: (value: string) => new Date(value).toLocaleString(),
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_, record) =>
        record.status === 'DEAD_LETTER' ? (
          <Popconfirm
            title="重新投递这条死信？"
            description="投递计数继续累加，超过上限会再次进死信。"
            okText="重投"
            cancelText="取消"
            onConfirm={() => void handleRetry(record)}
          >
            <Button size="small" type="primary" ghost icon={<SendOutlined />} />
          </Popconfirm>
        ) : null,
    },
  ];

  const deadLetterColumns: ColumnsType<EventDeliveryView> = [
    {
      title: '事件',
      dataIndex: 'eventId',
      key: 'eventId',
      ellipsis: true,
    },
    {
      title: '订阅',
      dataIndex: 'subscriptionId',
      key: 'subscriptionId',
      width: 200,
      render: (id: string) => {
        const subscription = subscriptions.find((item) => item.id === id);

        return subscription === undefined ? (
          <Space size={4}>
            <Typography.Text code style={{ fontSize: 12 }}>{id}</Typography.Text>
            <Tag color="orange">配置已删</Tag>
          </Space>
        ) : (
          subscription.name
        );
      },
    },
    { title: '次数', dataIndex: 'attempts', key: 'attempts', width: 70 },
    {
      title: '最近错误',
      dataIndex: 'lastError',
      key: 'lastError',
      ellipsis: true,
      render: (value: string | null) => value ?? '—',
    },
    {
      title: '入死信时间',
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 170,
      render: (value: string) => new Date(value).toLocaleString(),
    },
    {
      title: '操作',
      key: 'actions',
      width: 100,
      render: (_, record) => (
        <Popconfirm
          title="重新投递这条死信？"
          okText="重投"
          cancelText="取消"
          onConfirm={() => void handleRetry(record)}
        >
          <Button size="small" type="primary" ghost icon={<SendOutlined />}>
            重投
          </Button>
        </Popconfirm>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message="事件订阅：审批事件通过 outbox 投递到业务系统"
        description={
          stats === undefined
            ? 'outbox 积压与死信统计加载中……'
            : `待发布 ${stats.outbox.pending} 条 / 死信 ${stats.outbox.deadLetter} 条`
              + (stats.outbox.oldestPendingAt
                ? `；最老一条待发布事件产生于 ${new Date(stats.outbox.oldestPendingAt).toLocaleString()}（看年龄而不是条数）`
                : '；当前没有积压')
        }
      />

      <Tabs
        items={[
          {
            key: 'subscriptions',
            label: `订阅（${subscriptions.length}）`,
            children: (
              <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                <Space style={{ width: '100%', justifyContent: 'space-between' }}>
                  <Typography.Text type="secondary">
                    订阅是配置数据：暂停会停止投递新事件，但已产生的事件仍按重试策略投递。
                  </Typography.Text>
                  <Space>
                    <Button icon={<ReloadOutlined />} onClick={refreshAll}>
                      刷新
                    </Button>
                    <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
                      新建订阅
                    </Button>
                  </Space>
                </Space>
                <Table<EventSubscriptionView>
                  rowKey="id"
                  columns={subscriptionColumns}
                  dataSource={subscriptions}
                  loading={subscriptionsQuery.isFetching}
                  pagination={{ pageSize: 20, showSizeChanger: false }}
                  scroll={{ x: 1100 }}
                />
              </Space>
            ),
          },
          {
            key: 'deadletters',
            label: `死信（${deadLettersQuery.data?.total ?? 0}）`,
            children: (
              <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
                  <Space wrap>
                    <Select
                      allowClear
                      placeholder="按订阅过滤"
                      style={{ width: 260 }}
                      value={deadLetterSubscription}
                      onChange={(value) => setDeadLetterSubscription(value)}
                      options={subscriptions.map((item) => ({ value: item.id, label: item.name }))}
                    />
                    <Typography.Text type="secondary">
                      死信是一条也投不出去的事件：先修对端再重投，否则只是把失败次数再加一次。
                    </Typography.Text>
                  </Space>
                  <Button icon={<ReloadOutlined />} onClick={() => void queryClient.invalidateQueries({ queryKey: DEAD_LETTERS_KEY })}>
                    刷新
                  </Button>
                </Space>
                <Table<EventDeliveryView>
                  rowKey={(record) => `${record.eventId}#${record.subscriptionId}`}
                  columns={deadLetterColumns}
                  dataSource={deadLetters}
                  loading={deadLettersQuery.isFetching}
                  pagination={false}
                  locale={{ emptyText: '没有死信' }}
                />
              </Space>
            ),
          },
        ]}
      />

      <Modal
        title="新建事件订阅"
        open={createOpen}
        confirmLoading={saving}
        onOk={() => void handleCreate()}
        onCancel={() => setCreateOpen(false)}
        okText="创建并签发密钥"
        cancelText="取消"
        width={680}
        destroyOnClose
      >
        <Form form={form} layout="vertical" initialValues={{ enabled: true, maxAttempts: 5, maxPerMinute: 0 }}>
          <Form.Item name="name" label="订阅名" rules={[{ required: true, whitespace: true, message: '请输入订阅名' }]}>
            <Input placeholder="如 OA 门户事件回调" />
          </Form.Item>
          <Form.Item
            name="endpointUrl"
            label="回调地址"
            rules={[
              { required: true, whitespace: true, message: '请输入回调地址' },
              { pattern: /^https?:\/\//i, message: '必须是 http(s) 地址' },
            ]}
          >
            <Input placeholder="https://oa.example.com/netbpm/events" />
          </Form.Item>
          <Space size="large" style={{ display: 'flex' }}>
            <Form.Item name="tenantId" label="租户" extra="留空取默认租户。">
              <Input placeholder="default" />
            </Form.Item>
            <Form.Item name="maxAttempts" label="最大重试次数">
              <InputNumber min={1} max={20} />
            </Form.Item>
            <Form.Item name="maxPerMinute" label="每分钟上限" extra="0 = 不限速。">
              <InputNumber min={0} max={100000} />
            </Form.Item>
          </Space>
          <Form.Item name="eventTypes" label="事件类型白名单" extra="留空 = 订阅全部事件。">
            <Select mode="tags" tokenSeparators={[',', ' ']} placeholder="如 PROCESS_COMPLETED" />
          </Form.Item>
          <Form.Item name="processDefinitionId" label="只关心某个流程定义（可选）">
            <Input placeholder="processDefinitionId" />
          </Form.Item>
          <Form.Item name="enabled" label="创建后立即启用" valuePropName="checked">
            <Switch />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title={`投递记录：${deliveryOf?.name ?? ''}`}
        open={deliveryOf !== null}
        onCancel={() => setDeliveryOf(null)}
        footer={null}
        width={900}
      >
        <Descriptions size="small" column={2} style={{ marginBottom: 12 }}>
          <Descriptions.Item label="回调地址">{deliveryOf?.endpointUrl}</Descriptions.Item>
          <Descriptions.Item label="限速">
            {deliveryOf?.maxPerMinute === 0 ? '不限速' : `${deliveryOf?.maxPerMinute}/分`}
          </Descriptions.Item>
        </Descriptions>
        <Table<EventDeliveryView>
          rowKey={(record) => record.eventId}
          columns={deliveryColumns}
          dataSource={deliveriesQuery.data ?? []}
          loading={deliveriesQuery.isFetching}
          pagination={{ pageSize: 10, showSizeChanger: false }}
          size="small"
        />
      </Modal>

      <SecretModal issued={issued} onClose={() => setIssued(null)} />
    </Space>
  );
}
