import { App, Button, Descriptions, Drawer, Input, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { runtimeApi } from '../api';
import type { FormProperty, IdentityLink, Variables } from '../api/types';
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
  const queryClient = useQueryClient();

  // 任务操作（委派 / 解决 / 候选人）：输入框共用一个「用户或组 id」
  const [actorId, setActorId] = useState('');
  const [acting, setActing] = useState(false);

  /** 表单元数据：formKey + activiti:formProperty 声明（只读，供宿主渲染表单）。 */
  const formQuery = useQuery({
    queryKey: ['runtime', 'task-form', taskId],
    queryFn: () => runtimeApi.taskForm(taskId as string),
    enabled: open && taskId !== null,
  });

  const runAction = async (action: () => Promise<void>, success: string) => {
    setActing(true);

    try {
      await action();
      message.success(success);
      setActorId('');
      void queryClient.invalidateQueries({ queryKey: ['runtime'] });
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    } finally {
      setActing(false);
    }
  };

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
    {
      title: '操作',
      key: 'actions',
      width: 90,
      render: (_, record) => {
        // 只有候选关系可以单独移除；办理人 / 所有者由任务操作维护
        if (!record.type.toLowerCase().includes('candidate')) {
          return null;
        }

        return (
          <Button
            size="small"
            danger
            disabled={acting}
            onClick={() => {
              const target = record.userId ?? record.groupId;

              if (target == null) {
                return;
              }

              void runAction(
                () => record.userId !== null && record.userId !== undefined
                  ? runtimeApi.removeCandidateUser(task!.id, record.userId)
                  : runtimeApi.removeCandidateGroup(task!.id, target),
                '已移除候选关系',
              );
            }}
          >
            移除
          </Button>
        );
      },
    },
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
            任务操作
          </Typography.Title>
          <Space wrap>
            <Input
              style={{ width: 200 }}
              placeholder="用户 / 组 id"
              value={actorId}
              onChange={(event) => setActorId(event.target.value)}
            />
            <Button
              disabled={acting || actorId.trim().length === 0}
              onClick={() => void runAction(
                () => runtimeApi.delegateTask(task.id, actorId.trim()),
                '已委派（解决后回到原办理人）',
              )}
            >
              委派
            </Button>
            <Button
              disabled={acting || task.delegationState !== 'PENDING'}
              onClick={() => void runAction(() => runtimeApi.resolveTask(task.id), '已解决委派')}
            >
              解决委派
            </Button>
            <Button
              disabled={acting || actorId.trim().length === 0}
              onClick={() => void runAction(
                () => runtimeApi.addCandidateUser(task.id, actorId.trim()),
                '已加候选用户',
              )}
            >
              加候选用户
            </Button>
            <Button
              disabled={acting || actorId.trim().length === 0}
              onClick={() => void runAction(
                () => runtimeApi.addCandidateGroup(task.id, actorId.trim()),
                '已加候选组',
              )}
            >
              加候选组
            </Button>
          </Space>

          <Typography.Title level={5} style={{ margin: 0 }}>
            表单元数据（{formQuery.data?.formProperties.length ?? 0} 个属性）
          </Typography.Title>
          <Descriptions size="small" column={2} bordered>
            <Descriptions.Item label="表单 Key" span={2}>
              {formQuery.data?.formKey ?? '—'}
            </Descriptions.Item>
            <Descriptions.Item label="节点" span={2}>
              {formQuery.data?.activityName ?? formQuery.data?.activityId ?? '—'}
            </Descriptions.Item>
          </Descriptions>
          {(formQuery.data?.formProperties.length ?? 0) > 0 && (
            <Table<FormProperty>
              rowKey={(record) => record.id ?? record.name ?? 'prop'}
              size="small"
              pagination={false}
              dataSource={formQuery.data?.formProperties}
              columns={[
                { title: '属性', dataIndex: 'id', key: 'id', width: 160 },
                { title: '名称', dataIndex: 'name', key: 'name', render: (v?: string | null) => v ?? '—' },
                { title: '类型', dataIndex: 'type', key: 'type', width: 100, render: (v?: string | null) => (v ? <Tag>{v}</Tag> : '—') },
                {
                  title: '必填',
                  dataIndex: 'required',
                  key: 'required',
                  width: 70,
                  render: (v?: boolean | null) => (v === true ? '是' : '否'),
                },
              ]}
            />
          )}

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
