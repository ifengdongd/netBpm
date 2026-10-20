import { DeleteOutlined } from '@ant-design/icons';
import { Button, Popconfirm, Space, Tag } from 'antd';
import type { ColumnsType } from 'antd/es/table';

import type { HistoricProcessInstance, ProcessInstanceResponse, TaskResponse } from '../../api/types';
import { formatTime } from '../../utils';

/** 转办 / 完成弹窗的模式（与 MonitoringPage 的 TaskActionState 共用）。 */
export type TaskActionMode = 'assignee' | 'complete';

/** 运行实例表格的操作回调。 */
export interface InstanceColumnHandlers {
  onDetail: (instanceId: string) => void;
  onToggleSuspension: (instance: ProcessInstanceResponse) => void;
  onRemove: (instance: ProcessInstanceResponse) => void;
}

/** 待办任务表格的操作回调。 */
export interface TaskColumnHandlers {
  onDetail: (taskId: string) => void;
  onClaim: (task: TaskResponse) => void;
  onOpenAction: (mode: TaskActionMode, task: TaskResponse) => void;
}

/** 历史实例表格的操作回调。 */
export interface HistoricColumnHandlers {
  onDetail: (processInstanceId: string) => void;
}

/** 运行实例表格列（约 25% 的页面代码量在此，拆出便于主文件聚焦数据与操作逻辑）。 */
export function buildInstanceColumns({
  onDetail,
  onToggleSuspension,
  onRemove,
}: InstanceColumnHandlers): ColumnsType<ProcessInstanceResponse> {
  return [
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
          <Button size="small" onClick={() => onDetail(record.id)}>
            详情
          </Button>
          <Button size="small" onClick={() => onToggleSuspension(record)}>
            {record.isSuspended ? '激活' : '挂起'}
          </Button>
          <Popconfirm
            title={`删除实例「${record.processInstanceId}」？`}
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => onRemove(record)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];
}

/** 待办任务表格列。 */
export function buildTaskColumns({ onDetail, onClaim, onOpenAction }: TaskColumnHandlers): ColumnsType<TaskResponse> {
  return [
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
          <Button size="small" onClick={() => onDetail(record.id)}>
            详情
          </Button>
          <Button size="small" disabled={record.assignee !== null && record.assignee !== ''} onClick={() => onClaim(record)}>
            认领
          </Button>
          <Button size="small" onClick={() => onOpenAction('assignee', record)}>
            转办
          </Button>
          <Button size="small" type="primary" onClick={() => onOpenAction('complete', record)}>
            完成
          </Button>
        </Space>
      ),
    },
  ];
}

/** 历史实例表格列（只读）。 */
export function buildHistoricColumns({ onDetail }: HistoricColumnHandlers): ColumnsType<HistoricProcessInstance> {
  return [
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
        <Button size="small" onClick={() => onDetail(record.processInstanceId)}>
          详情
        </Button>
      ),
    },
  ];
}
