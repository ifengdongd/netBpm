import {
  ApiOutlined,
  FileTextOutlined,
  RedoOutlined,
  ReloadOutlined,
  UnlockOutlined,
} from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Empty,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';

import { EXTERNAL_TASK_MAX_RESULTS, externalTasksApi } from '../api/externalTasks';
import type { ExternalTaskView } from '../api/externalTasks';
import { formatTime } from '../utils';
import {
  canRetry,
  canUnlock,
  externalTaskStatusColor,
  externalTaskStatusLabel,
  externalTaskStatusOptions,
  isLockExpired,
  isTruncated,
  summarizeStatuses,
} from './externalTasks';

export default function ExternalTasksPage() {
  const { message } = App.useApp();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  // 从实例监控跳过来时带 ?instanceId=…：直接按该实例过滤，省掉一次手输
  const [instanceIdInput, setInstanceIdInput] = useState(() => searchParams.get('instanceId') ?? '');
  const [instanceId, setInstanceId] = useState(() => searchParams.get('instanceId') ?? '');
  const [topicInput, setTopicInput] = useState('');
  const [topic, setTopic] = useState('');
  const [status, setStatus] = useState<string | undefined>(undefined);
  const [payloadOf, setPayloadOf] = useState<ExternalTaskView | null>(null);

  const tasksQuery = useQuery({
    queryKey: ['runtime', 'external-tasks', topic, status ?? '', instanceId],
    queryFn: ({ signal }) =>
      externalTasksApi.list(
        {
          topic: topic || undefined,
          status,
          processInstanceId: instanceId || undefined,
          maxResults: EXTERNAL_TASK_MAX_RESULTS,
        },
        signal,
      ),
    // 外部任务是被 worker 异步领取的：新鲜度给短一点，打开页面时尽量看到真实状态
    staleTime: 5_000,
  });

  const tasks = useMemo(() => tasksQuery.data ?? [], [tasksQuery.data]);
  const summary = useMemo(() => summarizeStatuses(tasks), [tasks]);
  const truncated = isTruncated(tasks, EXTERNAL_TASK_MAX_RESULTS);

  useEffect(() => {
    if (tasksQuery.isError) {
      message.error(`加载外部任务失败：${(tasksQuery.error as Error).message}`);
    }
  }, [tasksQuery.isError, tasksQuery.error, message]);

  const refresh = () => {
    void tasksQuery.refetch();
  };

  const handleRetry = async (record: ExternalTaskView) => {
    try {
      await externalTasksApi.retry(record.id);
      message.success('已重投：任务回到待领取，等 worker 重新领走并回执');
      refresh();
    } catch (error) {
      message.error(`重投失败：${(error as Error).message}`);
    }
  };

  const handleUnlock = async (record: ExternalTaskView) => {
    try {
      await externalTasksApi.unlock(record.id, record.workerId ?? '');
      message.success('已释放锁：任务回到待领取（重试次数没动）');
      refresh();
    } catch (error) {
      message.error(`解锁失败：${(error as Error).message}`);
    }
  };

  const columns: ColumnsType<ExternalTaskView> = [
    {
      title: '主题 / 任务',
      key: 'topic',
      width: 210,
      render: (_, record) => (
        <Space direction="vertical" size={2} style={{ lineHeight: 1.5 }}>
          <Tag color="blue" style={{ marginInlineEnd: 0 }}>
            {record.topic}
          </Tag>
          <Typography.Text
            type="secondary"
            style={{ fontSize: 12 }}
            copyable={{ text: record.id, tooltips: ['复制任务 ID', '已复制'] }}
          >
            #{record.id}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '停留节点',
      key: 'activity',
      width: 180,
      ellipsis: true,
      render: (_, record) => (
        <Space direction="vertical" size={0} style={{ lineHeight: 1.5 }}>
          <Typography.Text ellipsis={{ tooltip: record.activityName ?? record.activityId ?? '' }}>
            {record.activityName ?? '（未命名节点）'}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.activityId ?? '—'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '流程 / 实例',
      key: 'instance',
      width: 230,
      render: (_, record) => (
        <Space direction="vertical" size={0} style={{ lineHeight: 1.5 }}>
          <Typography.Text code style={{ fontSize: 12 }}>
            {record.processDefinitionKey ?? '—'}
          </Typography.Text>
          <Typography.Link
            style={{ fontSize: 12 }}
            onClick={() => navigate(`/monitoring?instanceId=${encodeURIComponent(record.processInstanceId)}`)}
          >
            {record.processInstanceId}
          </Typography.Link>
        </Space>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 110,
      render: (value: string, record) => (
        <Space direction="vertical" size={2}>
          <Tag color={externalTaskStatusColor(value)} style={{ marginInlineEnd: 0 }}>
            {externalTaskStatusLabel(value)}
          </Tag>
          {record.nextDueAt !== null && (
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              下次 {formatTime(record.nextDueAt)}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    {
      title: '重试',
      dataIndex: 'retries',
      key: 'retries',
      width: 80,
      render: (retries: number) => (
        <Tag color={retries <= 0 ? 'error' : retries === 1 ? 'warning' : 'default'} style={{ marginInlineEnd: 0 }}>
          {retries}
        </Tag>
      ),
    },
    {
      title: '锁',
      key: 'lock',
      width: 170,
      render: (_, record) => {
        if (record.status !== 'LOCKED' || (record.workerId ?? '') === '') {
          return <Typography.Text type="secondary">—</Typography.Text>;
        }

        const expired = isLockExpired(record);

        return (
          <Space direction="vertical" size={1} style={{ lineHeight: 1.5 }}>
            <Typography.Text code style={{ fontSize: 12 }}>
              {record.workerId}
            </Typography.Text>
            <Tooltip title={`锁到期：${formatTime(record.lockExpiresAt)}`}>
              <Typography.Text type={expired ? 'danger' : 'secondary'} style={{ fontSize: 11 }}>
                {expired ? '锁已过期（可被重领）' : `至 ${formatTime(record.lockExpiresAt)}`}
              </Typography.Text>
            </Tooltip>
          </Space>
        );
      },
    },
    {
      title: '最近失败',
      dataIndex: 'errorMessage',
      key: 'errorMessage',
      ellipsis: true,
      render: (value: string | null, record) =>
        value === null ? (
          <Typography.Text type="secondary">—</Typography.Text>
        ) : (
          <Tooltip title={record.errorDetails ?? value}>
            <Typography.Text type="danger" ellipsis style={{ fontSize: 12 }}>
              {value}
            </Typography.Text>
          </Tooltip>
        ),
    },
    {
      title: '登记时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 165,
      render: (value: string, record) => (
        <Space direction="vertical" size={0} style={{ lineHeight: 1.5 }}>
          <Typography.Text style={{ fontSize: 12 }}>{formatTime(value)}</Typography.Text>
          {record.completedAt !== null && (
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              回执 {formatTime(record.completedAt)}
            </Typography.Text>
          )}
        </Space>
      ),
    },
    {
      title: '操作',
      key: 'actions',
      width: 168,
      align: 'right',
      render: (_, record) => (
        <Space size={4}>
          <Tooltip title="查看领取时的变量快照">
            <Button size="small" icon={<FileTextOutlined />} onClick={() => setPayloadOf(record)} />
          </Tooltip>
          {canRetry(record.status) ? (
            <Popconfirm
              title="重投这条死信？"
              description="节点仍停在原地；重投只是让它重新可被领取，剩余次数不填则用服务端默认值。"
              okText="确认重投"
              cancelText="取消"
              onConfirm={() => void handleRetry(record)}
            >
              <Button size="small" type="primary" ghost icon={<RedoOutlined />}>
                重投
              </Button>
            </Popconfirm>
          ) : null}
          {canUnlock(record) ? (
            <Popconfirm
              title={`释放 worker「${record.workerId}」的锁？`}
              description="任务立刻回到待领取，不扣重试次数；worker 若还在处理，会出现重复执行（业务需幂等）。"
              okText="确认释放"
              cancelText="取消"
              onConfirm={() => void handleUnlock(record)}
            >
              <Button size="small" icon={<UnlockOutlined />}>
                解锁
              </Button>
            </Popconfirm>
          ) : null}
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        icon={<ApiOutlined />}
        message="外部任务：activiti:topic 节点由 worker 拉取执行，引擎只登记任务、原地等回执"
        description={
          tasksQuery.isPending
            ? '正在加载外部任务……'
            : `待领取 ${summary.PENDING} 条 / 已锁定 ${summary.LOCKED} 条 / 死信 ${summary.DEAD_LETTER} 条`
              + `（本屏共 ${tasks.length} 条${truncated ? `，已达上限 ${EXTERNAL_TASK_MAX_RESULTS}` : ''}）`
              + '；失败回执不会推进流程，死信需要人工处理。'
        }
      />

      <Space wrap style={{ width: '100%' }}>
        <Input.Search
          allowClear
          placeholder="按 topic 过滤（与 BPMN 的 activiti:topic 逐字一致）"
          style={{ width: 320 }}
          value={topicInput}
          onChange={(event) => {
            setTopicInput(event.target.value);

            if (event.target.value === '' && topic !== '') {
              setTopic('');
            }
          }}
          onSearch={(value) => setTopic(value.trim())}
        />
        <Select
          allowClear
          placeholder="全部状态"
          style={{ width: 150 }}
          value={status}
          onChange={(value?: string) => setStatus(value)}
          options={externalTaskStatusOptions()}
        />
        <Input.Search
          allowClear
          placeholder="按流程实例 ID 过滤"
          style={{ width: 320 }}
          value={instanceIdInput}
          onChange={(event) => {
            setInstanceIdInput(event.target.value);

            if (event.target.value === '' && instanceId !== '') {
              setInstanceId('');
            }
          }}
          onSearch={(value) => setInstanceId(value.trim())}
        />
        <div style={{ flex: 1 }} />
        <Button icon={<ReloadOutlined />} loading={tasksQuery.isFetching} onClick={refresh}>
          刷新
        </Button>
      </Space>

      {truncated && (
        <Alert
          type="warning"
          showIcon
          message={`只显示了最近 ${EXTERNAL_TASK_MAX_RESULTS} 条（服务端上限）`}
          description="用 topic / 状态 / 实例 ID 收窄范围；还没领走的任务越积越多，通常意味着某个 topic 没有 worker 在跑。"
        />
      )}

      <Table<ExternalTaskView>
        rowKey="id"
        tableLayout="fixed"
        scroll={{ x: 1500 }}
        columns={columns}
        dataSource={tasks}
        loading={tasksQuery.isFetching}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={
                topic || status !== undefined || instanceId
                  ? '没有符合条件的外部任务'
                  : '还没有外部任务：流程走到 activiti:topic 节点才会登记一条'
              }
            />
          ),
        }}
        pagination={{ pageSize: 20, hideOnSinglePage: true, showSizeChanger: false }}
      />

      <Modal
        title={payloadOf ? `${payloadOf.topic} · 变量快照` : ''}
        open={payloadOf !== null}
        footer={null}
        width={720}
        onCancel={() => setPayloadOf(null)}
      >
        <Typography.Paragraph type="secondary" style={{ fontSize: 12 }}>
          节点登记那一刻的变量快照（不是实时视图）。worker 回执的变量才会写回流程实例。
        </Typography.Paragraph>
        <pre
          style={{
            maxHeight: '55vh',
            overflow: 'auto',
            background: '#fafafa',
            padding: 12,
            borderRadius: 6,
            fontSize: 12,
          }}
        >
          {JSON.stringify(payloadOf?.payload ?? {}, null, 2)}
        </pre>
      </Modal>
    </Space>
  );
}
