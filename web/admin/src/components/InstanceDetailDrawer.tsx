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
import { lazy, Suspense, useCallback, useEffect, useState } from 'react';

import { runtimeApi } from '../api';
import type {
  Diagram,
  HistoricActivity,
  HistoricProcessInstance,
  HistoricTask,
  HistoricVariable,
  ProcessInstanceResponse,
  Variables,
} from '../api/types';
import { formatTime } from '../utils';

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

  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);

  const [instance, setInstance] = useState<ProcessInstanceResponse | null>(null);
  const [historic, setHistoric] = useState<HistoricProcessInstance | null>(null);

  const [variables, setVariables] = useState<Variables>({});
  const [historicVariables, setHistoricVariables] = useState<HistoricVariable[]>([]);
  const [activities, setActivities] = useState<HistoricActivity[]>([]);
  const [tasks, setTasks] = useState<HistoricTask[]>([]);
  const [diagram, setDiagram] = useState<Diagram | null>(null);

  const [jumpOpen, setJumpOpen] = useState(false);
  const [jumping, setJumping] = useState(false);
  const [jumpForm] = Form.useForm<{ targetActivityId: string; reason?: string }>();

  const loadDetails = useCallback(
    async (id: string) => {
      setLoading(true);

      try {
        // 各数据源独立兜底：授权不足 / 历史未启用时不影响基本信息展示
        const [running, hist, vars, histVars, histTasks, diagramResult] = await Promise.all([
          runtimeApi.instance(id).catch(() => null),
          runtimeApi.historicInstance(id).catch(() => null),
          runtimeApi.instanceVariables(id).catch(() => ({}) as Variables),
          runtimeApi.historicVariables(id).catch(() => [] as HistoricVariable[]),
          runtimeApi.historicTasks(id).catch(() => [] as HistoricTask[]),
          runtimeApi.diagram(id).catch(() => null),
        ]);

        setInstance(running);
        setHistoric(hist);
        setVariables(vars);
        setHistoricVariables(histVars);
        setTasks(histTasks);
        setDiagram(diagramResult);
        setActivities(diagramResult?.activities ?? []);
      } catch (error) {
        message.error(`加载实例详情失败：${(error as Error).message}`);
      } finally {
        setLoading(false);
        setLoaded(true);
      }
    },
    [message],
  );

  useEffect(() => {
    if (open && instanceId !== null) {
      setLoaded(false);
      setInstance(null);
      setHistoric(null);
      setVariables({});
      setHistoricVariables([]);
      setActivities([]);
      setTasks([]);
      setDiagram(null);
      void loadDetails(instanceId);
    }
  }, [open, instanceId, loadDetails]);

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

      onChanged();
      await loadDetails(instance.id);
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
      await loadDetails(instance.id);
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
          {isRunning && (
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
          )}

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

function formatValue(value: unknown): string {
  if (value === null) {
    return 'null';
  }

  if (value === undefined) {
    return '—';
  }

  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

function formatDuration(milliseconds?: number | null): string {
  if (milliseconds === undefined || milliseconds === null) {
    return '—';
  }

  if (milliseconds < 1000) {
    return `${milliseconds} ms`;
  }

  const seconds = Math.floor(milliseconds / 1000);

  if (seconds < 60) {
    return `${seconds} 秒`;
  }

  const minutes = Math.floor(seconds / 60);

  if (minutes < 60) {
    return `${minutes} 分 ${seconds % 60} 秒`;
  }

  return `${Math.floor(minutes / 60)} 时 ${minutes % 60} 分`;
}
