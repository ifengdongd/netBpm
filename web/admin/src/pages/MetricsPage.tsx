import { ReloadOutlined } from '@ant-design/icons';
import {
  App,
  Button,
  Card,
  Col,
  Empty,
  Progress,
  Row,
  Select,
  Space,
  Statistic,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useCallback, useEffect, useState } from 'react';

import { metricsApi } from '../api';
import type { AdminAuditActionCount, AdminMetricsResponse } from '../api/types';
import { auditActionColor, formatTime } from '../utils';

const WINDOW_OPTIONS = [1, 7, 30, 90].map((days) => ({ value: days, label: `最近 ${days} 天` }));

interface PendingRow {
  processKey: string;
  count: number;
}

export default function MetricsPage() {
  const { message } = App.useApp();

  const [metrics, setMetrics] = useState<AdminMetricsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [windowDays, setWindowDays] = useState(7);

  const load = useCallback(
    async (days: number) => {
      setLoading(true);

      try {
        // 「今日到期」按浏览器时区换算自然日
        setMetrics(
          await metricsApi.get({
            windowDays: days,
            timeZoneOffsetMinutes: -new Date().getTimezoneOffset(),
          }),
        );
      } catch (error) {
        message.error(`加载指标失败：${(error as Error).message}`);
      } finally {
        setLoading(false);
      }
    },
    [message],
  );

  useEffect(() => {
    void load(windowDays);
  }, [load, windowDays]);

  const pendingRows: PendingRow[] = Object.entries(metrics?.pendingTasksByProcessKey ?? {})
    .map(([processKey, count]) => ({ processKey, count }))
    .sort((a, b) => b.count - a.count);

  const actionRows = metrics?.auditActions ?? [];
  const pendingTotal = pendingRows.reduce((sum, row) => sum + row.count, 0);

  const pendingColumns: ColumnsType<PendingRow> = [
    {
      title: '流程 key',
      dataIndex: 'processKey',
      ellipsis: true,
    },
    {
      title: '待办数',
      dataIndex: 'count',
      width: 90,
      align: 'right',
    },
    {
      title: '占比',
      key: 'share',
      width: 200,
      render: (_, row) => (
        <Progress
          size="small"
          percent={pendingTotal > 0 ? Math.round((row.count / pendingTotal) * 100) : 0}
          status="normal"
        />
      ),
    },
  ];

  const actionColumns: ColumnsType<AdminAuditActionCount> = [
    {
      title: '动作',
      dataIndex: 'action',
      render: (value: string) => <Tag color={auditActionColor(value)}>{value}</Tag>,
    },
    {
      title: '次数',
      dataIndex: 'count',
      width: 90,
      align: 'right',
    },
  ];

  const cards: { title: string; value: number | null | undefined; danger?: boolean; hint?: string }[] = [
    { title: '运行中实例', value: metrics?.runningProcessInstances },
    { title: '待办总数', value: metrics?.pendingTasks, hint: '内存宿主没有任务表，无法统计' },
    { title: '逾期待办', value: metrics?.overdueTasks, danger: true },
    { title: '今日到期', value: metrics?.dueTodayTasks },
    { title: '历史实例', value: metrics?.historicProcessInstances },
    { title: '已结束', value: metrics?.finishedProcessInstances },
    { title: '已删除', value: metrics?.deletedProcessInstances },
    { title: '流程定义', value: metrics?.processDefinitions },
    { title: '已归档实例', value: metrics?.archivedInstances, hint: '归档未启用时不可统计' },
  ];

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card
        title="管理指标"
        extra={(
          <Space>
            <Select value={windowDays} options={WINDOW_OPTIONS} onChange={(days) => setWindowDays(days)} />
            <Typography.Text type="secondary">审计窗口</Typography.Text>
            <Typography.Text type="secondary" code>
              {formatTime(metrics?.generatedAt)}
            </Typography.Text>
            <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load(windowDays)}>
              刷新
            </Button>
          </Space>
        )}
      >
        <Typography.Paragraph type="secondary" style={{ marginTop: -8 }}>
          租户口径 <Tag color="geekblue">{metrics?.tenantId ?? '全部'}</Tag>
          「不可用」表示当前宿主无法统计（如内存宿主没有任务表、归档未启用），<strong>不是 0</strong>。
        </Typography.Paragraph>

        <Row gutter={[16, 16]}>
          {cards.map((card) => {
            const unavailable = card.value === null || card.value === undefined;
            const alert = card.danger === true && typeof card.value === 'number' && card.value > 0;

            return (
              <Col key={card.title} xs={12} sm={8} lg={6} xl={4}>
                <Card size="small">
                  <Statistic
                    title={card.title}
                    value={unavailable ? '不可用' : card.value!}
                    valueStyle={{
                      color: unavailable ? 'rgba(0, 0, 0, 0.25)' : alert ? '#cf1322' : undefined,
                      fontSize: 24,
                    }}
                    prefix={alert ? '⚠' : undefined}
                    suffix={unavailable ? <Tooltip title={card.hint}>?</Tooltip> : undefined}
                  />
                </Card>
              </Col>
            );
          })}
        </Row>
      </Card>

      <Row gutter={16}>
        <Col xs={24} lg={12}>
          <Card title="待办分布（按流程 key）" size="small">
            {pendingRows.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={pendingTasksUnavailable(metrics)} />
            ) : (
              <Table<PendingRow>
                rowKey="processKey"
                size="small"
                loading={loading}
                columns={pendingColumns}
                dataSource={pendingRows}
                pagination={false}
              />
            )}
          </Card>
        </Col>

        <Col xs={24} lg={12}>
          <Card title={`审计动作（${metrics?.auditWindowDays ?? 7} 天窗口）`} size="small">
            {actionRows.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="窗口内没有管理动作" />
            ) : (
              <Table<AdminAuditActionCount>
                rowKey="action"
                size="small"
                loading={loading}
                columns={actionColumns}
                dataSource={actionRows}
                pagination={false}
              />
            )}
          </Card>
        </Col>
      </Row>
    </Space>
  );
}

function pendingTasksUnavailable(metrics: AdminMetricsResponse | null): string {
  return metrics?.pendingTasks === null || metrics?.pendingTasks === undefined
    ? '当前宿主无法统计待办（内存宿主没有任务表）'
    : '当前没有待办';
}
