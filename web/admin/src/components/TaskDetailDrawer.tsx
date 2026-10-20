import { App, Descriptions, Drawer, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useCallback, useEffect, useState } from 'react';

import { runtimeApi } from '../api';
import type { IdentityLink, TaskDetail, Variables } from '../api/types';
import { formatTime } from '../utils';

interface Props {
  taskId: string | null;
  open: boolean;
  onClose: () => void;
}

export default function TaskDetailDrawer({ taskId, open, onClose }: Props) {
  const { message } = App.useApp();

  const [loading, setLoading] = useState(false);
  const [task, setTask] = useState<TaskDetail | null>(null);
  const [variables, setVariables] = useState<Variables>({});

  const load = useCallback(
    async (id: string) => {
      setLoading(true);

      try {
        const [detail, vars] = await Promise.all([
          runtimeApi.task(id),
          runtimeApi.taskVariables(id).catch(() => ({}) as Variables),
        ]);

        setTask(detail);
        setVariables(vars);
      } catch (error) {
        message.error(`加载任务详情失败：${(error as Error).message}`);
        setTask(null);
      } finally {
        setLoading(false);
      }
    },
    [message],
  );

  useEffect(() => {
    if (open && taskId !== null) {
      setTask(null);
      setVariables({});
      void load(taskId);
    }
  }, [open, taskId, load]);

  const linkColumns: ColumnsType<IdentityLink> = [
    { title: '关系', dataIndex: 'type', key: 'type', width: 120, render: (value: string) => <Tag>{value}</Tag> },
    { title: '用户', dataIndex: 'userId', key: 'userId', render: (value?: string | null) => value ?? '—' },
    { title: '组', dataIndex: 'groupId', key: 'groupId', render: (value?: string | null) => value ?? '—' },
  ];

  return (
    <Drawer title="任务详情" open={open} width={680} onClose={onClose} destroyOnClose loading={loading}>
      {task !== null && (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Descriptions size="small" bordered column={2}>
            <Descriptions.Item label="任务名" span={2}>
              {task.name ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="任务 ID" span={2}>
              {task.id}
            </Descriptions.Item>
            <Descriptions.Item label="流程 Key">{task.processDefinitionKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="节点 Key">{task.taskDefinitionKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="办理人">{task.assignee ?? '未认领'}</Descriptions.Item>
            <Descriptions.Item label="所有者">{task.owner ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="优先级">{task.priority}</Descriptions.Item>
            <Descriptions.Item label="委派状态">{task.delegationState ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="创建时间">{formatTime(task.createTime)}</Descriptions.Item>
            <Descriptions.Item label="到期时间">{formatTime(task.dueDate)}</Descriptions.Item>
            <Descriptions.Item label="认领时间">{formatTime(task.claimTime)}</Descriptions.Item>
            <Descriptions.Item label="业务主键">{task.businessKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="描述" span={2}>
              {task.description ?? '—'}
            </Descriptions.Item>
          </Descriptions>

          <Typography.Title level={5} style={{ margin: 0 }}>
            身份关系（{task.identityLinks.length}）
          </Typography.Title>
          <Table<IdentityLink>
            rowKey={(record) => `${record.type}-${record.userId ?? ''}-${record.groupId ?? ''}`}
            size="small"
            pagination={false}
            columns={linkColumns}
            dataSource={task.identityLinks}
          />

          <Typography.Title level={5} style={{ margin: 0 }}>
            任务局部变量（{Object.keys(variables).length}）
          </Typography.Title>
          <Table<{ name: string; value: unknown }>
            rowKey="name"
            size="small"
            pagination={false}
            columns={[
              { title: '变量名', dataIndex: 'name', key: 'name' },
              {
                title: '值',
                dataIndex: 'value',
                key: 'value',
                render: (value: unknown) => (
                  <Typography.Text code>
                    {value === null ? 'null' : typeof value === 'object' ? JSON.stringify(value) : String(value)}
                  </Typography.Text>
                ),
              },
            ]}
            dataSource={Object.entries(variables).map(([name, value]) => ({ name, value }))}
          />
        </Space>
      )}
    </Drawer>
  );
}
