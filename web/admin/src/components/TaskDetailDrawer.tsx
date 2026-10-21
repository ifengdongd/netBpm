import { App, Descriptions, Drawer, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';

import { runtimeApi } from '../api';
import type { IdentityLink, Variables } from '../api/types';
import { formatTime, formatValue } from '../utils';

interface Props {
  taskId: string | null;
  open: boolean;
  onClose: () => void;
  /** 点「流程实例」时切到实例详情（由列表页负责关闭本抽屉并打开实例抽屉）。 */
  onOpenInstance?: (processInstanceId: string) => void;
}

export default function TaskDetailDrawer({ taskId, open, onClose, onOpenInstance }: Props) {
  const { message } = App.useApp();

  // 详情查询：键含任务 id，由 queryClient 统一缓存；任务操作后 invalidate ['runtime'] 即可联动刷新
  const taskQuery = useQuery({
    queryKey: ['runtime', 'task-detail', taskId],
    queryFn: async () => {
      const id = taskId as string;

      const [detail, vars] = await Promise.all([
        runtimeApi.task(id),
        runtimeApi.taskVariables(id).catch(() => ({}) as Variables),
      ]);

      return { detail, vars };
    },
    enabled: open && taskId !== null,
  });

  const task = taskQuery.data?.detail ?? null;
  const variables = taskQuery.data?.vars ?? {};

  useEffect(() => {
    if (taskQuery.isError) {
      message.error(`加载任务详情失败：${(taskQuery.error as Error).message}`);
    }
  }, [taskQuery.isError, taskQuery.error, message]);

  const linkColumns: ColumnsType<IdentityLink> = [
    { title: '关系', dataIndex: 'type', key: 'type', width: 120, render: (value: string) => <Tag>{value}</Tag> },
    { title: '用户', dataIndex: 'userId', key: 'userId', render: (value?: string | null) => value ?? '—' },
    { title: '组', dataIndex: 'groupId', key: 'groupId', render: (value?: string | null) => value ?? '—' },
  ];

  return (
    <Drawer title="任务详情" open={open} width={680} onClose={onClose} destroyOnClose loading={open && taskQuery.isPending}>
      {task !== null && (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Descriptions size="small" bordered column={2}>
            <Descriptions.Item label="任务名" span={2}>
              {task.name ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="任务 ID" span={2}>
              {task.id}
            </Descriptions.Item>
            <Descriptions.Item label="流程实例" span={2}>
              {task.processInstanceId !== null && onOpenInstance !== undefined ? (
                <Typography.Link
                  title="在实例详情中查看完整流程图与历史"
                  onClick={() => onOpenInstance(task.processInstanceId as string)}
                >
                  {task.processInstanceId}
                </Typography.Link>
              ) : (
                (task.processInstanceId ?? '—')
              )}
            </Descriptions.Item>
            <Descriptions.Item label="流程 Key">{task.processDefinitionKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="节点 Key">{task.taskDefinitionKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="流程定义 ID" span={2}>
              {task.processDefinitionId ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="办理人">{task.assignee ?? '未认领'}</Descriptions.Item>
            <Descriptions.Item label="所有者">{task.owner ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="优先级">{task.priority}</Descriptions.Item>
            <Descriptions.Item label="委派状态">{task.delegationState ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="创建时间">{formatTime(task.createTime)}</Descriptions.Item>
            <Descriptions.Item label="到期时间">{formatTime(task.dueDate)}</Descriptions.Item>
            <Descriptions.Item label="认领时间">{formatTime(task.claimTime)}</Descriptions.Item>
            <Descriptions.Item label="业务主键">{task.businessKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="表单 Key">{task.formKey ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="分类">{task.category ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="租户">{task.tenantId ?? '—'}</Descriptions.Item>
            <Descriptions.Item label="挂起状态">
              {task.isSuspended ? <Tag color="warning">已挂起</Tag> : <Tag color="success">正常</Tag>}
            </Descriptions.Item>
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
                  <Typography.Text code>{formatValue(value)}</Typography.Text>
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
