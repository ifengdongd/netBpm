import { DeleteOutlined, PauseCircleOutlined, PlayCircleOutlined, SwapOutlined } from '@ant-design/icons';
import {
  App,
  Button,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Spin,
  Table,
  Tabs,
  Tag,
  Timeline,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { lazy, Suspense, useEffect, useState } from 'react';

import { runtimeApi } from '../api';
import { ApiError } from '../api/client';
import type {
  HistoricProcessInstance,
  HistoricTask,
  HistoricVariable,
  ProcessInstanceResponse,
  Variables,
} from '../api/types';
import { formatDuration, formatTime, formatValue } from '../utils';

// bpmn-js 体积较大，只在打开「流程图」Tab 时按需加载
const BpmnDiagram = lazy(() => import('./BpmnDiagram'));

interface Props {
  /** 运行实例 id 或历史实例 id（两者共用展示，内部自动判断模式）。 */
  instanceId: string | null;
  open: boolean;
  onClose: () => void;
  /** 运行实例状态变化（挂起 / 激活 / 跳转 / 删除）后通知列表刷新。 */
  onChanged: () => void;
}

export default function InstanceDetailDrawer({ instanceId, open, onClose, onChanged }: Props) {
  const { message } = App.useApp();

  // 详情查询：6 个数据源一次并行拉取；键含实例 id，
  // 挂起 / 跳转等操作后由 onChanged → invalidateQueries(['runtime']) 联动刷新本抽屉与列表。
  // 各数据源独立兜底：仅 404（实例 / 历史不存在）落空值；
  // 403（无权查看）与 5xx 上抛走查询错误通路展示，避免「无权」被误读为「没有」。
  const detailQuery = useQuery({
    queryKey: ['runtime', 'instance-detail', instanceId],
    queryFn: async () => {
      const id = instanceId as string;

      const [running, hist, vars, histVars, histTasks, diagramResult] = await Promise.all([
        runtimeApi.instance(id).catch(ignoreNotFound(null)),
        runtimeApi.historicInstance(id).catch(ignoreNotFound(null)),
        runtimeApi.instanceVariables(id).catch(ignoreNotFound({} as Variables)),
        runtimeApi.historicVariables(id).catch(ignoreNotFound([] as HistoricVariable[])),
        runtimeApi.historicTasks(id).catch(ignoreNotFound([] as HistoricTask[])),
        runtimeApi.diagram(id).catch(ignoreNotFound(null)),
      ]);

      return { running, hist, vars, histVars, histTasks, diagramResult };
    },
    enabled: open && instanceId !== null,
  });

  const instance = detailQuery.data?.running ?? null;
  const historic = detailQuery.data?.hist ?? null;
  const variables = detailQuery.data?.vars ?? {};
  const historicVariables = detailQuery.data?.histVars ?? [];
  const activities = detailQuery.data?.diagramResult?.activities ?? [];
  const tasks = detailQuery.data?.histTasks ?? [];
  const diagram = detailQuery.data?.diagramResult ?? null;

  const [jumpOpen, setJumpOpen] = useState(false);
  const [jumping, setJumping] = useState(false);
  const [jumpForm] = Form.useForm<{ targetActivityId: string; reason?: string }>();

  const loading = open && detailQuery.isPending;
  const loaded = detailQuery.isSuccess || detailQuery.isError;

  useEffect(() => {
    if (detailQuery.isError) {
      message.error(`加载实例详情失败：${(detailQuery.error as Error).message}`);
    }
  }, [detailQuery.isError, detailQuery.error, message]);

  const toggleSuspend = async () => {
    if (instance === null) {
      return;
    }

    try {
      if (instance.isSuspended) {
        await runtimeApi.activateInstance(instance.id);
        message.success('已激活实例');
      } else {
        await runtimeApi.suspendInstance(instance.id);
        message.success('已挂起实例');
      }

      // 失效 runtime 查询族：列表与本抽屉的详情会自动重新拉取
      onChanged();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const handleJump = async () => {
    const values = await jumpForm.validateFields();

    if (instance === null) {
      return;
    }

    setJumping(true);

    try {
      await runtimeApi.jumpInstance(instance.id, values.targetActivityId.trim(), values.reason?.trim() || undefined);
      message.success('已跳转');
      setJumpOpen(false);
      jumpForm.resetFields();
      onChanged();
    } catch (error) {
      message.error(`跳转失败：${(error as Error).message}`);
    } finally {
      setJumping(false);
    }
  };

  const handleDelete = async () => {
    if (instance === null) {
      return;
    }

    try {
      await runtimeApi.removeInstance(instance.id, '管理端删除');
      message.success('已删除实例');
      onChanged();
      onClose();
    } catch (error) {
      message.error(`删除失败：${(error as Error).message}`);
    }
  };

  const variableColumns: ColumnsType<{ name: string; value: unknown }> = [
    { title: '变量名', dataIndex: 'name', key: 'name', width: 220 },
    {
      title: '值',
      dataIndex: 'value',
      key: 'value',
      render: (value: unknown) => <Typography.Text code>{formatValue(value)}</Typography.Text>,
    },
  ];

  const historicVariableColumns: ColumnsType<HistoricVariable> = [
    { title: '变量名', dataIndex: 'name', key: 'name', width: 180 },
    {
      title: '类型',
      dataIndex: 'type',
      key: 'type',
      width: 100,
      render: (value?: string | null) => (value ? <Tag>{value}</Tag> : '—'),
    },
    {
      title: '值',
      dataIndex: 'value',
      key: 'value',
      render: (value: unknown) => <Typography.Text code>{formatValue(value)}</Typography.Text>,
    },
    {
      title: '最后更新',
      dataIndex: 'lastUpdatedTime',
      key: 'lastUpdatedTime',
      width: 170,
      render: (value?: string | null) => formatTime(value),
    },
  ];

  const taskColumns: ColumnsType<HistoricTask> = [
    { title: '任务名', dataIndex: 'name', key: 'name', ellipsis: true, render: (v?: string | null) => v ?? '—' },
    { title: '办理人', dataIndex: 'assignee', key: 'assignee', width: 120, render: (v?: string | null) => v ?? '—' },
    { title: '开始', dataIndex: 'startTime', key: 'startTime', width: 170, render: (v?: string | null) => formatTime(v) },
    { title: '结束', dataIndex: 'endTime', key: 'endTime', width: 170, render: (v?: string | null) => formatTime(v) },
    {
      title: '删除原因',
      dataIndex: 'deleteReason',
      key: 'deleteReason',
      ellipsis: true,
      render: (v?: string | null) => v ?? '—',
    },
  ];

  const variableTab = instance !== null
    ? {
        key: 'variables',
        label: `变量（${Object.keys(variables).length}）`,
        children: (
          <Table<{ name: string; value: unknown }>
            rowKey="name"
            size="small"
            loading={loading}
            pagination={false}
            columns={variableColumns}
            dataSource={Object.entries(variables).map(([name, value]) => ({ name, value }))}
          />
        ),
      }
    : {
        key: 'variables',
        label: `历史变量（${historicVariables.length}）`,
        children: (
          <Table<HistoricVariable>
            rowKey={(record) => `${record.name}-${record.executionId ?? ''}-${record.taskId ?? ''}`}
            size="small"
            loading={loading}
            pagination={false}
            columns={historicVariableColumns}
            dataSource={historicVariables}
          />
        ),
      };

  const tabItems = [
    {
      key: 'diagram',
      label: '流程图',
      children: (
        <Suspense fallback={<Spin style={{ display: 'block', margin: '80px auto' }} />}>
          <BpmnDiagram xml={diagram?.bpmnXml ?? ''} activities={diagram?.activities ?? []} height={460} />
        </Suspense>
      ),
    },
    variableTab,
    {
      key: 'activities',
      label: `活动时间线（${activities.length}）`,
      children: (
        <Timeline
          items={activities.map((activity) => ({
            color: activity.isActive ? 'blue' : 'gray',
            children: (
              <Space direction="vertical" size={0}>
                <Space>
                  <Typography.Text strong>{activity.activityName ?? activity.activityId}</Typography.Text>
                  <Tag>{activity.activityType ?? '—'}</Tag>
                  {activity.isActive && <Tag color="processing">当前</Tag>}
                </Space>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {formatTime(activity.startTime)} → {activity.endTime ? formatTime(activity.endTime) : '进行中'}
                  {activity.assignee ? ` · ${activity.assignee}` : ''}
                </Typography.Text>
              </Space>
            ),
          }))}
        />
      ),
    },
    {
      key: 'tasks',
      label: `历史任务（${tasks.length}）`,
      children: (
        <Table<HistoricTask>
          rowKey="id"
          size="small"
          loading={loading}
          pagination={false}
          columns={taskColumns}
          dataSource={tasks}
        />
      ),
    },
  ];

  const isRunning = instance !== null;
  const isHistoric = !isRunning && historic !== null;
  const notFound = loaded && !isRunning && !isHistoric;

  const title = isRunning
    ? `实例详情 · ${instance.processDefinitionKey ?? instance.processDefinitionId}`
    : isHistoric
      ? `历史实例 · ${historic.processDefinitionId.split(':')[0] || historic.processDefinitionId}`
      : '实例详情';

  return (
    <Drawer title={title} open={open} width={760} onClose={onClose} destroyOnClose>
      {!loaded && <Spin style={{ display: 'block', margin: '120px auto' }} />}

      {notFound && <Empty description="实例不存在（可能已删除且没有历史记录）" style={{ padding: 60 }} />}

      {(isRunning || isHistoric) && (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {isRunning && <RunningInstanceDescriptions instance={instance} />}

          {isHistoric && (
            <>
              <Space>
                <Tag color="default">历史实例（只读）</Tag>
                {historic.isRunning ? (
                  <Tag color="processing">运行中</Tag>
                ) : historic.deleteReason ? (
                  <Tag color="error">已删除</Tag>
                ) : (
                  <Tag>已结束</Tag>
                )}
              </Space>
              <HistoricInstanceDescriptions historic={historic} />
            </>
          )}

          {isRunning && (
            <Space wrap>
              <Button
                icon={instance.isSuspended ? <PlayCircleOutlined /> : <PauseCircleOutlined />}
                onClick={() => void toggleSuspend()}
              >
                {instance.isSuspended ? '激活' : '挂起'}
              </Button>
              <Button icon={<SwapOutlined />} onClick={() => setJumpOpen(true)}>
                跳转节点
              </Button>
              <Popconfirm
                title="删除该实例？"
                description="待办 / 变量将被清理，历史保留。"
                okText="删除"
                okButtonProps={{ danger: true }}
                cancelText="取消"
                onConfirm={() => void handleDelete()}
              >
                <Button danger icon={<DeleteOutlined />}>
                  删除实例
                </Button>
              </Popconfirm>
            </Space>
          )}

          <Tabs items={tabItems} />
        </Space>
      )}

      <Modal
        title="跳转节点"
        open={jumpOpen}
        confirmLoading={jumping}
        onOk={() => void handleJump()}
        onCancel={() => setJumpOpen(false)}
        okText="跳转"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={jumpForm} layout="vertical">
          <Form.Item
            name="targetActivityId"
            label="目标节点 ID"
            rules={[{ required: true, whitespace: true, message: '请输入目标节点 ID' }]}
          >
            <Input placeholder="BPMN 元素 id（可填结束事件 id 强制办结）" />
          </Form.Item>
          <Form.Item name="reason" label="跳转原因（可选）">
            <Input placeholder="写入被删任务与活动历史" />
          </Form.Item>
        </Form>
      </Modal>
    </Drawer>
  );
}

// ─────────────────────────── 描述信息子组件 ───────────────────────────

/** 仅静默 404（实例 / 历史不存在），403 与 5xx 上抛走查询错误通路展示。 */
function ignoreNotFound<T>(fallback: T): (error: unknown) => T {
  return (error) => {
    if (error instanceof ApiError && error.status === 404) {
      return fallback;
    }

    throw error;
  };
}

/** 运行实例基本信息（Descriptions）。 */
function RunningInstanceDescriptions({ instance }: { instance: ProcessInstanceResponse }) {
  return (
    <Descriptions size="small" bordered column={2}>
      <Descriptions.Item label="流程 Key" span={2}>
        {instance.processDefinitionKey ?? '—'}
      </Descriptions.Item>
      <Descriptions.Item label="实例 ID" span={2}>
        {instance.processInstanceId}
      </Descriptions.Item>
      <Descriptions.Item label="定义 ID" span={2}>
        {instance.processDefinitionId}
      </Descriptions.Item>
      <Descriptions.Item label="业务主键">{instance.businessKey ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="实例名">{instance.name ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="发起人">{instance.startUserId ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="开始时间">{formatTime(instance.startTime)}</Descriptions.Item>
      <Descriptions.Item label="状态">
        {instance.isSuspended ? <Tag color="warning">已挂起</Tag> : <Tag color="success">运行中</Tag>}
      </Descriptions.Item>
      <Descriptions.Item label="租户">{instance.tenantId ?? '—'}</Descriptions.Item>
    </Descriptions>
  );
}

/** 历史实例基本信息（Descriptions，只读）。 */
function HistoricInstanceDescriptions({ historic }: { historic: HistoricProcessInstance }) {
  return (
    <Descriptions size="small" bordered column={2}>
      <Descriptions.Item label="流程 Key" span={2}>
        {historic.processDefinitionId.split(':')[0] || historic.processDefinitionId}
      </Descriptions.Item>
      <Descriptions.Item label="实例 ID" span={2}>
        {historic.processInstanceId}
      </Descriptions.Item>
      <Descriptions.Item label="定义 ID" span={2}>
        {historic.processDefinitionId}
      </Descriptions.Item>
      <Descriptions.Item label="业务主键">{historic.businessKey ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="实例名">{historic.name ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="发起人">{historic.startUserId ?? '—'}</Descriptions.Item>
      <Descriptions.Item label="开始时间">{formatTime(historic.startTime)}</Descriptions.Item>
      <Descriptions.Item label="结束时间">{formatTime(historic.endTime)}</Descriptions.Item>
      <Descriptions.Item label="耗时">{formatDuration(historic.durationInMillis)}</Descriptions.Item>
      <Descriptions.Item label="删除原因" span={2}>
        {historic.deleteReason ?? '—'}
      </Descriptions.Item>
    </Descriptions>
  );
}
