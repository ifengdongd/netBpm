import { DeleteOutlined, ReloadOutlined } from '@ant-design/icons';
import {
  App,
  Button,
  Card,
  Col,
  Form,
  Input,
  Modal,
  Popconfirm,
  Row,
  Space,
  Statistic,
  Table,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { runtimeApi } from '../api';
import type {
  HistoricProcessInstance,
  ProcessInstanceResponse,
  TaskResponse,
  Variables,
} from '../api/types';
import InstanceDetailDrawer from '../components/InstanceDetailDrawer';
import TaskDetailDrawer from '../components/TaskDetailDrawer';
import { usePagedList } from '../hooks/usePagedList';
import { formatTime } from '../utils';

interface TaskActionState {
  mode: 'assignee' | 'complete';
  task: TaskResponse;
}

export default function MonitoringPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [actionForm] = Form.useForm<{ assignee?: string; variables?: string }>();

  // 统计
  const statsQuery = useQuery({
    queryKey: ['runtime', 'task-stats'],
    queryFn: () => runtimeApi.taskStats({ timeZoneOffsetMinutes: -new Date().getTimezoneOffset() }),
  });
  const stats = statsQuery.data ?? null;

  useEffect(() => {
    if (statsQuery.isError) {
      message.error(`加载待办统计失败：${(statsQuery.error as Error).message}`);
    }
  }, [statsQuery.isError, statsQuery.error, message]);

  // 三个列表共用 usePagedList：缓存、去重与失效联动由 queryClient 统一管理
  const instanceList = usePagedList<ProcessInstanceResponse>(
    ['runtime', 'instances'],
    (page) =>
      runtimeApi.instances({
        firstResult: (page.current - 1) * page.pageSize,
        maxResults: page.pageSize,
      }),
    (error) => message.error(`加载运行实例失败：${error.message}`),
  );

  const taskList = usePagedList<TaskResponse>(
    ['runtime', 'tasks'],
    (page) =>
      runtimeApi.tasks({
        firstResult: (page.current - 1) * page.pageSize,
        maxResults: page.pageSize,
      }),
    (error) => message.error(`加载待办任务失败：${error.message}`),
  );

  const historicList = usePagedList<HistoricProcessInstance>(
    ['runtime', 'historic-instances'],
    (page) =>
      runtimeApi.historicInstances({
        firstResult: (page.current - 1) * page.pageSize,
        maxResults: page.pageSize,
      }),
    (error) => message.error(`加载历史实例失败：${error.message}`),
  );

  // 抽屉 / 弹窗
  const [detailInstanceId, setDetailInstanceId] = useState<string | null>(null);
  const [detailTaskId, setDetailTaskId] = useState<string | null>(null);
  const [taskAction, setTaskAction] = useState<TaskActionState | null>(null);
  const [actionSubmitting, setActionSubmitting] = useState(false);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);

  // 所有 runtime 查询（统计 / 三个列表 / 两个抽屉详情）统一失效刷新
  const refreshAll = () => {
    setSelectedIds([]);
    void queryClient.invalidateQueries({ queryKey: ['runtime'] });
  };

  // ─────────────────────────── 实例操作 ───────────────────────────

  const toggleInstanceSuspension = async (instance: ProcessInstanceResponse) => {
    try {
      if (instance.isSuspended) {
        await runtimeApi.activateInstance(instance.id);
        message.success('已激活实例');
      } else {
        await runtimeApi.suspendInstance(instance.id);
        message.success('已挂起实例');
      }

      refreshAll();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const removeInstance = async (instance: ProcessInstanceResponse) => {
    try {
      await runtimeApi.removeInstance(instance.id, '管理端删除');
      message.success('已删除实例');
      refreshAll();
    } catch (error) {
      message.error(`删除实例失败：${(error as Error).message}`);
    }
  };

  const batchDelete = async () => {
    if (selectedIds.length === 0) {
      message.warning('请先勾选要删除的实例');
      return;
    }

    try {
      const result = await runtimeApi.batchDeleteInstances(selectedIds, '管理端批量删除');
      message.success(`批量删除完成：成功 ${result.succeeded} 条，失败 ${result.failed} 条`);
      refreshAll();
    } catch (error) {
      message.error(`批量删除失败：${(error as Error).message}`);
    }
  };

  // ─────────────────────────── 任务操作 ───────────────────────────

  const claimTask = async (task: TaskResponse) => {
    try {
      await runtimeApi.claimTask(task.id);
      message.success('已认领任务');
      refreshAll();
    } catch (error) {
      message.error(`认领失败：${(error as Error).message}`);
    }
  };

  const openTaskAction = (mode: TaskActionState['mode'], task: TaskResponse) => {
    actionForm.resetFields();
    setTaskAction({ mode, task });
  };

  const submitTaskAction = async () => {
    if (taskAction === null) {
      return;
    }

    let values: { assignee?: string; variables?: string };

    try {
      values = await actionForm.validateFields();
    } catch {
      // 字段校验失败时 antd 已就地展示错误，无需额外提示
      return;
    }

    setActionSubmitting(true);

    try {
      if (taskAction.mode === 'assignee') {
        await runtimeApi.setTaskAssignee(taskAction.task.id, values.assignee?.trim() ?? '');
        message.success('已转办');
      } else {
        await runtimeApi.completeTask(taskAction.task.id, parseVariables(values.variables));
        message.success('已完成任务');
      }

      setTaskAction(null);
      refreshAll();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    } finally {
      setActionSubmitting(false);
    }
  };

  // ─────────────────────────── 表格列 ───────────────────────────

  const instanceColumns: ColumnsType<ProcessInstanceResponse> = [
    {
      title: '流程 Key',
      dataIndex: 'processDefinitionKey',
      key: 'processDefinitionKey',
      render: (value?: string | null) => <Tag>{value ?? '—'}</Tag>,
    },
    { title: '业务主键', dataIndex: 'businessKey', key: 'businessKey', render: (v?: string | null) => v ?? '—' },
    { title: '实例名称', dataIndex: 'name', key: 'name', ellipsis: true, render: (v?: string | null) => v ?? '—' },
    { title: '发起人', dataIndex: 'startUserId', key: 'startUserId', width: 120, render: (v?: string | null) => v ?? '—' },
    {
      title: '开始时间',
      dataIndex: 'startTime',
      key: 'startTime',
      width: 180,
      render: (v?: string | null) => formatTime(v),
    },
    {
      title: '状态',
      dataIndex: 'isSuspended',
      key: 'isSuspended',
      width: 90,
      render: (suspended: boolean) => (suspended ? <Tag color="warning">已挂起</Tag> : <Tag color="success">运行中</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 240,
      render: (_, record) => (
        <Space size={4}>
          <Button size="small" onClick={() => setDetailInstanceId(record.id)}>
            详情
          </Button>
          <Button size="small" onClick={() => void toggleInstanceSuspension(record)}>
            {record.isSuspended ? '激活' : '挂起'}
          </Button>
          <Popconfirm
            title={`删除实例「${record.processInstanceId}」？`}
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void removeInstance(record)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const taskColumns: ColumnsType<TaskResponse> = [
    { title: '任务名', dataIndex: 'name', key: 'name', ellipsis: true, render: (v?: string | null) => v ?? '—' },
    {
      title: '流程 Key',
      dataIndex: 'processDefinitionKey',
      key: 'processDefinitionKey',
      render: (v?: string | null) => <Tag>{v ?? '—'}</Tag>,
    },
    {
      title: '办理人',
      dataIndex: 'assignee',
      key: 'assignee',
      width: 120,
      render: (v?: string | null) => v ?? '（未认领）',
    },
    { title: '优先级', dataIndex: 'priority', key: 'priority', width: 80 },
    {
      title: '创建时间',
      dataIndex: 'createTime',
      key: 'createTime',
      width: 180,
      render: (v?: string | null) => formatTime(v),
    },
    { title: '到期时间', dataIndex: 'dueDate', key: 'dueDate', width: 180, render: (v?: string | null) => formatTime(v) },
    {
      title: '操作',
      key: 'actions',
      width: 280,
      render: (_, record) => (
        <Space size={4}>
          <Button size="small" onClick={() => setDetailTaskId(record.id)}>
            详情
          </Button>
          <Button size="small" disabled={record.assignee !== null && record.assignee !== ''} onClick={() => void claimTask(record)}>
            认领
          </Button>
          <Button size="small" onClick={() => openTaskAction('assignee', record)}>
            转办
          </Button>
          <Button size="small" type="primary" onClick={() => openTaskAction('complete', record)}>
            完成
          </Button>
        </Space>
      ),
    },
  ];

  const historicColumns: ColumnsType<HistoricProcessInstance> = [
    {
      title: '流程 Key',
      key: 'key',
      render: (_, record) => <Tag>{record.processDefinitionId.split(':')[0] || '—'}</Tag>,
    },
    { title: '业务主键', dataIndex: 'businessKey', key: 'businessKey', render: (v?: string | null) => v ?? '—' },
    { title: '实例名称', dataIndex: 'name', key: 'name', ellipsis: true, render: (v?: string | null) => v ?? '—' },
    { title: '发起人', dataIndex: 'startUserId', key: 'startUserId', width: 120, render: (v?: string | null) => v ?? '—' },
    { title: '开始', dataIndex: 'startTime', key: 'startTime', width: 180, render: (v?: string | null) => formatTime(v) },
    { title: '结束', dataIndex: 'endTime', key: 'endTime', width: 180, render: (v?: string | null) => formatTime(v) },
    {
      title: '状态',
      key: 'state',
      width: 110,
      render: (_, record) =>
        record.isRunning ? <Tag color="processing">运行中</Tag> : record.deleteReason ? <Tag color="error">已删除</Tag> : <Tag>已结束</Tag>,
    },
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_, record) => (
        <Button size="small" onClick={() => setDetailInstanceId(record.processInstanceId)}>
          详情
        </Button>
      ),
    },
  ];

  const tabItems = [
    {
      key: 'instances',
      label: `运行实例（${instanceList.total}）`,
      children: (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Button danger icon={<DeleteOutlined />} disabled={selectedIds.length === 0} onClick={() => void batchDelete()}>
            批量删除选中（{selectedIds.length}）
          </Button>
          <Table<ProcessInstanceResponse>
            rowKey="id"
            columns={instanceColumns}
            dataSource={instanceList.items}
            loading={instanceList.loading}
            rowSelection={{ selectedRowKeys: selectedIds, onChange: (keys) => setSelectedIds(keys as string[]) }}
            pagination={instanceList.pagination}
          />
        </Space>
      ),
    },
    {
      key: 'tasks',
      label: `待办任务（${taskList.total}）`,
      children: (
        <Table<TaskResponse>
          rowKey="id"
          columns={taskColumns}
          dataSource={taskList.items}
          loading={taskList.loading}
          pagination={taskList.pagination}
        />
      ),
    },
    {
      key: 'historic',
      label: `历史实例（${historicList.total}）`,
      children: (
        <Table<HistoricProcessInstance>
          rowKey="processInstanceId"
          columns={historicColumns}
          dataSource={historicList.items}
          loading={historicList.loading}
          pagination={historicList.pagination}
        />
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Typography.Text type="secondary">
          运行实例、待办任务与历史实例的实时监控；支持实例挂起/激活/跳转、任务认领/转办/完成。
        </Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={refreshAll}>
          刷新
        </Button>
      </Space>

      <Row gutter={16}>
        <Col span={6}>
          <Card>
            <Statistic title="待办总数" value={stats?.total ?? 0} />
          </Card>
        </Col>
        <Col span={6}>
          <Card>
            <Statistic
              title="已逾期"
              value={stats?.overdue ?? 0}
              valueStyle={{ color: stats && stats.overdue > 0 ? '#cf1322' : undefined }}
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card>
            <Statistic title="今日到期" value={stats?.dueToday ?? 0} />
          </Card>
        </Col>
        <Col span={6}>
          <Card title="按流程分布">
            <Space direction="vertical" size={4} style={{ width: '100%' }}>
              {stats && Object.keys(stats.byDefinition).length > 0 ? (
                Object.entries(stats.byDefinition).map(([key, count]) => (
                  <Space key={key} style={{ justifyContent: 'space-between', width: '100%' }}>
                    <Tag>{key}</Tag>
                    <Typography.Text strong>{count}</Typography.Text>
                  </Space>
                ))
              ) : (
                <Typography.Text type="secondary">无待办</Typography.Text>
              )}
            </Space>
          </Card>
        </Col>
      </Row>

      <Tabs items={tabItems} />

      <InstanceDetailDrawer
        instanceId={detailInstanceId}
        open={detailInstanceId !== null}
        onClose={() => setDetailInstanceId(null)}
        onChanged={refreshAll}
      />

      <TaskDetailDrawer taskId={detailTaskId} open={detailTaskId !== null} onClose={() => setDetailTaskId(null)} />

      <Modal
        title={taskAction?.mode === 'assignee' ? `转办任务「${taskAction.task.name ?? taskAction.task.id}」` : `完成任务「${taskAction?.task.name ?? taskAction?.task.id ?? ''}」`}
        open={taskAction !== null}
        confirmLoading={actionSubmitting}
        onOk={() => void submitTaskAction()}
        onCancel={() => setTaskAction(null)}
        okText={taskAction?.mode === 'assignee' ? '转办' : '完成'}
        cancelText="取消"
        destroyOnClose
      >
        <Form form={actionForm} layout="vertical">
          {taskAction?.mode === 'assignee' ? (
            <Form.Item name="assignee" label="新办理人（留空表示退回未分配）">
              <Input placeholder="如 zhangsan" autoComplete="off" />
            </Form.Item>
          ) : (
            <Form.Item
              name="variables"
              label="随完成写入的变量（JSON 对象，可留空）"
              rules={[{ validator: validateVariables }]}
            >
              <Input.TextArea rows={5} placeholder='{ "approved": true, "comment": "同意" }' />
            </Form.Item>
          )}
        </Form>
      </Modal>
    </Space>
  );
}

function parseVariables(text?: string): Variables | undefined {
  const trimmed = text?.trim();

  if (!trimmed) {
    return undefined;
  }

  const parsed = JSON.parse(trimmed) as unknown;

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('变量必须是 JSON 对象');
  }

  return parsed as Variables;
}

function validateVariables(_: unknown, value?: string): Promise<void> {
  try {
    parseVariables(value);
    return Promise.resolve();
  } catch (error) {
    return Promise.reject(error instanceof Error ? error : new Error('变量必须是 JSON 对象'));
  }
}
