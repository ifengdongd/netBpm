import { DeleteOutlined, ReloadOutlined, SearchOutlined } from '@ant-design/icons';
import {
  App,
  AutoComplete,
  Button,
  Card,
  DatePicker,
  Descriptions,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import dayjs, { type Dayjs } from 'dayjs';
import { useCallback, useState } from 'react';

import { auditApi } from '../api';
import type { AuditQuery } from '../api';
import type { AdminAuditItem } from '../api/types';
import { usePagedList, type PageState } from '../hooks/usePagedList';
import { auditActionColor, formatTime } from '../utils';

/** 常用动作（`AutoComplete` 仍允许手填，后端新增动作不需要改前端）。 */
const ACTION_OPTIONS = [
  'task.reassign',
  'task.batch_reassign',
  'task.update',
  'task.candidate_add',
  'task.candidate_remove',
  'instance.jump',
  'instance.migrate',
  'instance.delete',
  'instance.batch_delete',
  'instance.suspend',
  'instance.activate',
  'variable.write',
  'variable.delete',
  'task_variable.write',
  'task_variable.delete',
  'deployment.create',
  'deployment.delete',
  'definition.suspend',
  'definition.activate',
  'history.archive',
  'audit.purge',
].map((value) => ({ value }));

interface FilterValues {
  actor?: string;
  action?: string;
  processInstanceId?: string;
  success?: 'all' | 'true' | 'false';
  range?: [Dayjs, Dayjs];
}

function prettyJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

export default function AuditPage() {
  const { message } = App.useApp();
  const [form] = Form.useForm<FilterValues>();

  const buildQuery = useCallback(
    (target: PageState): AuditQuery => {
      const values = form.getFieldsValue();

      return {
        actor: values.actor?.trim() || undefined,
        action: values.action?.trim() || undefined,
        processInstanceId: values.processInstanceId?.trim() || undefined,
        success: values.success === 'all' || values.success === undefined
          ? undefined
          : values.success === 'true',
        since: values.range?.[0]?.toISOString(),
        until: values.range?.[1]?.toISOString(),
        firstResult: (target.current - 1) * target.pageSize,
        maxResults: target.pageSize,
      };
    },
    [form],
  );

  // 分页列表状态与加载由 usePagedList 统一管理（缓存 / 失效联动 / 过期响应防护）
  const auditList = usePagedList<AdminAuditItem>(
    ['admin', 'audit'],
    (target) => auditApi.list(buildQuery(target)),
    (error) => message.error(`加载审计失败：${error.message}`),
  );

  const [purgeOpen, setPurgeOpen] = useState(false);
  const [purgeBefore, setPurgeBefore] = useState<Dayjs>(() => dayjs().subtract(30, 'day'));
  const [purging, setPurging] = useState(false);

  const { page, setPage, reload } = auditList;

  const resetToFirstPage = () => {
    if (page.current === 1) {
      reload();
      return;
    }

    setPage({ ...page, current: 1 });
  };

  const onSearch = resetToFirstPage;

  const onReset = () => {
    form.resetFields();
    resetToFirstPage();
  };

  const onPurge = async () => {
    setPurging(true);

    try {
      const result = await auditApi.purge({ before: purgeBefore.toISOString() });

      message.success(`已清理 ${result.removed} 条（早于 ${formatTime(result.before)}）`);
      setPurgeOpen(false);
      reload();
    } catch (error) {
      message.error(`清理失败：${(error as Error).message}`);
    } finally {
      setPurging(false);
    }
  };

  const columns: ColumnsType<AdminAuditItem> = [
    {
      title: '时间',
      dataIndex: 'createdAt',
      width: 180,
      render: (value: string) => formatTime(value),
    },
    {
      title: '操作人',
      dataIndex: 'actor',
      width: 200,
      render: (value: string | null | undefined, row) => (
        <Space size={4}>
          <span>{value ?? '—'}</span>
          <Tag>{row.authMode}</Tag>
        </Space>
      ),
    },
    {
      title: '动作',
      dataIndex: 'action',
      width: 200,
      render: (value: string) => <Tag color={auditActionColor(value)}>{value}</Tag>,
    },
    {
      title: '目标',
      key: 'target',
      ellipsis: true,
      render: (_, row) => `${row.targetType}: ${row.targetId}`,
    },
    {
      title: '实例',
      dataIndex: 'processInstanceId',
      width: 200,
      ellipsis: true,
      render: (value: string | null | undefined) => value ?? '—',
    },
    {
      title: '结果',
      dataIndex: 'success',
      width: 110,
      render: (success: boolean, row) => (
        <Tag color={success ? 'green' : 'red'} title={row.error ?? undefined}>
          {success ? '成功' : '失败'}
        </Tag>
      ),
    },
  ];

  return (
    <Card
      title="管理动作审计"
      extra={(
        <Space>
          <Button icon={<ReloadOutlined />} onClick={reload}>
            刷新
          </Button>
          <Button danger icon={<DeleteOutlined />} onClick={() => setPurgeOpen(true)}>
            清理历史
          </Button>
        </Space>
      )}
    >
      <Form form={form} layout="inline" initialValues={{ success: 'all' }} style={{ marginBottom: 16, rowGap: 8 }}>
        <Form.Item name="actor" label="操作人">
          <Input allowClear placeholder="如 alice" style={{ width: 160 }} />
        </Form.Item>

        <Form.Item name="action" label="动作">
          <AutoComplete
            allowClear
            options={ACTION_OPTIONS}
            placeholder="如 task.reassign"
            style={{ width: 200 }}
            filterOption={(input, option) => (option?.value ?? '').includes(input)}
          />
        </Form.Item>

        <Form.Item name="processInstanceId" label="实例 id">
          <Input allowClear placeholder="流程实例 id" style={{ width: 180 }} />
        </Form.Item>

        <Form.Item name="success" label="结果">
          <Select
            style={{ width: 100 }}
            options={[
              { value: 'all', label: '全部' },
              { value: 'true', label: '成功' },
              { value: 'false', label: '失败' },
            ]}
          />
        </Form.Item>

        <Form.Item name="range" label="时间">
          <DatePicker.RangePicker showTime allowEmpty={[true, true]} />
        </Form.Item>

        <Form.Item>
          <Space>
            <Button type="primary" icon={<SearchOutlined />} onClick={onSearch}>
              查询
            </Button>
            <Button onClick={onReset}>重置</Button>
          </Space>
        </Form.Item>
      </Form>

      <Typography.Paragraph type="secondary" style={{ marginTop: -4 }}>
        记录转办、改任务 / 候选、跳转 / 迁移、实例挂起与删除、变量直写、部署与定义变更、归档与审计清理。
        被 403 拒绝的请求不会记录；明细只含变量名，不含变量值。
      </Typography.Paragraph>

      <Table<AdminAuditItem>
        rowKey="id"
        size="small"
        loading={auditList.loading}
        columns={columns}
        dataSource={auditList.items}
        pagination={{
          ...auditList.pagination,
          pageSizeOptions: [20, 50, 100],
        }}
        expandable={{
          rowExpandable: (row) => Boolean(row.detail || row.error),
          expandedRowRender: (row) => (
            <Descriptions size="small" column={1} bordered>
              <Descriptions.Item label="明细">
                <pre style={{ margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-all', fontSize: 12 }}>
                  {row.detail ? prettyJson(row.detail) : '—'}
                </pre>
              </Descriptions.Item>
              <Descriptions.Item label="错误">{row.error ?? '—'}</Descriptions.Item>
            </Descriptions>
          ),
        }}
      />

      <Modal
        title="清理审计记录"
        open={purgeOpen}
        okText="确认清理"
        okButtonProps={{ danger: true, loading: purging }}
        cancelText="取消"
        onOk={() => void onPurge()}
        onCancel={() => setPurgeOpen(false)}
      >
        <Typography.Paragraph>
          删除<strong>该时刻之前</strong>的全部审计记录（当前租户），<strong>不可恢复</strong>。
          清理动作本身会留下一条 <Typography.Text code>audit.purge</Typography.Text> 记录。
        </Typography.Paragraph>

        <DatePicker
          showTime
          value={purgeBefore}
          style={{ width: '100%' }}
          onChange={(value) => {
            if (value) {
              setPurgeBefore(value);
            }
          }}
        />
      </Modal>
    </Card>
  );
}
