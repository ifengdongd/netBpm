import { ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Row,
  Space,
  Statistic,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';

import { redisApi } from '../api';
import type { RedisQueueKeyInfo } from '../api/types';
import { formatDuration, formatTime } from '../utils';

/** 队列键角色的中文口径：说清「这一段代表什么」，而不是只摆一个英文键名。 */
const ROLE_META: Record<string, { label: string; color: string; hint: string }> = {
  delay: {
    label: '延迟段',
    color: 'blue',
    hint: '还没到期的作业提示（有序集合，score = 到期时间 ticks）',
  },
  ready: {
    label: '就绪段',
    color: 'gold',
    hint: '已到期、等待宿主编排取走的提示（列表）',
  },
  inflight: {
    label: '在途段',
    color: 'purple',
    hint: '已取走但未确认完成的提示（有序集合，可见性超时后会被回收）',
  },
};

/** 每 10s 自动刷新：队列键条目数随时在变，静态数字容易误导。 */
const REFRESH_INTERVAL_MS = 10_000;

export default function RedisPage() {
  const { message } = App.useApp();

  const redisQuery = useQuery({
    queryKey: ['admin', 'redis'],
    queryFn: () => redisApi.info(),
    refetchInterval: REFRESH_INTERVAL_MS,
  });

  const info = redisQuery.data ?? null;
  const loading = redisQuery.isFetching;

  // 三态必须分开：「还没查回来 / 查失败」不是「不是 Redis」。
  // 旧写法在 info 为 null 时落到「未使用 Redis」，把一次接口失败渲染成一条配置结论，
  // 运维会照着黄条去改 NetBpm:Jobs:Queue:Provider —— 方向从一开始就错了。
  const pending = redisQuery.isPending;
  const queryFailed = info === null && !pending;

  useEffect(() => {
    if (redisQuery.isError) {
      message.error(`加载 Redis 诊断失败：${(redisQuery.error as Error).message}`);
    }
  }, [redisQuery.isError, redisQuery.error, message]);

  const keys = info?.keys ?? [];
  const totalEntries = keys.reduce((sum, item) => sum + item.length, 0);

  const keyColumns: ColumnsType<RedisQueueKeyInfo> = [
    {
      title: '角色',
      dataIndex: 'role',
      width: 110,
      render: (role: string) => {
        const meta = ROLE_META[role];

        return (
          <Tooltip title={meta?.hint}>
            <Tag color={meta?.color ?? 'default'}>{meta?.label ?? role}</Tag>
          </Tooltip>
        );
      },
    },
    {
      title: '键名',
      dataIndex: 'key',
      render: (key: string) => <Typography.Text code copyable>{key}</Typography.Text>,
    },
    {
      title: '类型',
      dataIndex: 'type',
      width: 90,
      render: (type: string) => <Typography.Text type="secondary">{type}</Typography.Text>,
    },
    {
      title: '条目数',
      dataIndex: 'length',
      width: 100,
      align: 'right',
      render: (length: number) => <Typography.Text strong>{length}</Typography.Text>,
    },
  ];

  const keyspaceEntries = Object.entries(info?.server?.keyspace ?? {}).sort(([a], [b]) =>
    a.localeCompare(b, 'en', { numeric: true }),
  );

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card
        title="Redis 作业队列"
        extra={(
          <Space>
            <Typography.Text type="secondary" code>
              {formatTime(info?.checkedAt)}
            </Typography.Text>
            <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void redisQuery.refetch()}>
              刷新
            </Button>
          </Space>
        )}
      >
        <Descriptions
          size="small"
          column={{ xs: 1, sm: 2, lg: 3 }}
          items={[
            {
              key: 'provider',
              label: '队列实现',
              children: (
                <Space size={4}>
                  <Tag color={info?.isRedis === true ? 'green' : 'default'}>{info?.provider ?? '—'}</Tag>
                  <Typography.Text type="secondary">NetBpm:Jobs:Queue:Provider</Typography.Text>
                </Space>
              ),
            },
            {
              key: 'target',
              label: '目标地址',
              children: info?.target === null || info?.target === undefined
                ? '—'
                : <Typography.Text code>{info.target}</Typography.Text>,
            },
            {
              key: 'database',
              label: '逻辑库',
              children: info?.database === null || info?.database === undefined
                ? '—'
                : <Typography.Text code>db{info.database}</Typography.Text>,
            },
            {
              key: 'prefix',
              label: '键前缀',
              children: info?.keyPrefix === null || info?.keyPrefix === undefined
                ? '—'
                : <Typography.Text code>{info.keyPrefix}</Typography.Text>,
            },
            {
              key: 'ssl',
              label: 'TLS',
              children: info?.ssl === true ? '已启用' : info?.ssl === false ? '未启用' : '—',
            },
            {
              key: 'source',
              label: '连接来源',
              children: info?.usesConnectionString === true
                ? 'ConnectionString（Host / Port / Database 配置被忽略）'
                : 'Host / Port / Database 配置项',
            },
            {
              key: 'status',
              label: '连接状态',
              children: info === null
                ? (pending ? <Tag>查询中…</Tag> : <Tag color="red">诊断接口不可用</Tag>)
                : info.isRedis !== true
                  ? <Tag>未使用 Redis</Tag>
                  : info.reachable
                    ? <Tag color="green">PING 成功</Tag>
                    : <Tag color="red">连接不可用</Tag>,
            },
            {
              key: 'latency',
              label: 'PING 时延',
              children: info?.latencyMs === null || info?.latencyMs === undefined
                ? '—'
                : <Typography.Text code>{info.latencyMs.toFixed(2)} ms</Typography.Text>,
            },
          ]}
        />

        {queryFailed && (
          <Alert
            type="error"
            showIcon
            style={{ marginTop: 16 }}
            message="Redis 诊断接口不可用"
            description={(
              <Space direction="vertical" size={4}>
                <span>{(redisQuery.error as Error | null)?.message ?? '请求 /api/admin/redis 失败。'}</span>
                <span>
                  这不代表「没配 Redis」——诊断本身没查出来。先确认登录态未过期、宿主在运行（
                  <Typography.Text code>/health/ready</Typography.Text>
                  ），再点右上角刷新。
                </span>
              </Space>
            )}
          />
        )}

        {info !== null && info.isRedis !== true && (
          <Alert
            type="warning"
            showIcon
            style={{ marginTop: 16 }}
            message="当前宿主的作业队列不是 Redis"
            description={(
              <Space direction="vertical" size={4}>
                <span>{info.error ?? '作业队列走进程内优先队列，Redis 里不会出现任何 netbpm 键。'}</span>
                <Typography.Text code>
                  NetBpm:Jobs:Queue:Provider = Redis（并配好 NetBpm:Jobs:Queue:Redis）
                </Typography.Text>
              </Space>
            )}
          />
        )}

        {info?.isRedis === true && !info.reachable && (
          <Alert
            type="error"
            showIcon
            style={{ marginTop: 16 }}
            message="Redis 连接不可用"
            description={(
              <Space direction="vertical" size={4}>
                <span>{info.error ?? 'PING 未成功。'}</span>
                <span>
                  异步作业仍会由数据库兜底扫库执行，但「到期即时唤醒」与「多宿主共享队列」已失效；
                  请检查 Redis 是否启动、Host / Port / Auth / Database 是否正确、网络与防火墙是否放通。
                </span>
              </Space>
            )}
          />
        )}
      </Card>

      <Row gutter={[16, 16]}>
        {[
          { title: '队列提示总数', value: totalEntries },
          { title: '延迟段', value: keys.find((item) => item.role === 'delay')?.length ?? 0 },
          { title: '就绪段', value: keys.find((item) => item.role === 'ready')?.length ?? 0 },
          { title: '在途段', value: keys.find((item) => item.role === 'inflight')?.length ?? 0 },
        ].map((card) => (
          <Col key={card.title} xs={12} sm={12} lg={6}>
            <Card size="small">
              <Statistic
                title={card.title}
                value={info?.isRedis === true ? card.value : info === null ? '—' : '不可用'}
                valueStyle={{
                  color: info?.isRedis === true ? undefined : 'rgba(0, 0, 0, 0.25)',
                  fontSize: 24,
                }}
              />
            </Card>
          </Col>
        ))}
      </Row>

      <Card title="队列键" size="small">
        <Typography.Paragraph type="secondary" style={{ marginTop: -4 }}>
          三个键都<b>没有数据是正常的</b>：Redis 只存「作业到期提示」，作业执行完即从
          <Typography.Text code>inflight</Typography.Text> 移除，空键由 Redis 自动回收；
          流程实例、任务、历史等数据始终在数据库里。
          多宿主共享队列时，只要有一个宿主把提示取走，这里的条目数就会立刻减少。
        </Typography.Paragraph>

        {keys.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={
              info === null
                ? '诊断接口未返回，暂无键信息'
                : info.isRedis === true
                  ? '连不上 Redis，暂无键信息'
                  : '未使用 Redis 队列'
            }
          />
        ) : (
          <Table<RedisQueueKeyInfo>
            rowKey="key"
            size="small"
            loading={loading}
            columns={keyColumns}
            dataSource={keys}
            pagination={false}
          />
        )}
      </Card>

      <Card
        title="服务端信息"
        size="small"
        extra={info?.server === null || info?.server === undefined
          ? <Typography.Text type="secondary">INFO 不可用</Typography.Text>
          : undefined}
      >
        {info?.server === null || info?.server === undefined ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="未取到服务端 INFO（连接不可用，或 Redis 未开放 INFO 命令权限）"
          />
        ) : (
          <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Descriptions
              size="small"
              column={{ xs: 1, sm: 2, lg: 3 }}
              items={[
                { key: 'version', label: '版本', children: info.server.version ?? '—' },
                { key: 'mode', label: '运行模式', children: info.server.mode ?? '—' },
                {
                  key: 'uptime',
                  label: '已运行',
                  children: info.server.uptimeSeconds === null || info.server.uptimeSeconds === undefined
                    ? '—'
                    : formatDuration(info.server.uptimeSeconds * 1000),
                },
                {
                  key: 'clients',
                  label: '连接数',
                  children: info.server.connectedClients ?? '—',
                },
                {
                  key: 'memory',
                  label: '已用内存',
                  children: info.server.usedMemoryBytes === null || info.server.usedMemoryBytes === undefined
                    ? '—'
                    : formatBytes(info.server.usedMemoryBytes),
                },
                {
                  key: 'commands',
                  label: '累计命令',
                  children: info.server.totalCommandsProcessed ?? '—',
                },
                { key: 'hits', label: '命中次数', children: info.server.keyspaceHits ?? '—' },
                { key: 'misses', label: '未命中次数', children: info.server.keyspaceMisses ?? '—' },
                {
                  key: 'hitRate',
                  label: '命中率',
                  children: formatHitRate(info.server.keyspaceHits, info.server.keyspaceMisses),
                },
              ]}
            />

            <div>
              <Typography.Text type="secondary">
                各逻辑库键数量（当前库已高亮；<Typography.Text code>netbpm</Typography.Text>
                只会出现在配置的那个库上）
              </Typography.Text>
              <Space size={8} wrap style={{ marginTop: 8 }}>
                {keyspaceEntries.length === 0 ? (
                  <Typography.Text type="secondary">服务端没有任何键</Typography.Text>
                ) : (
                  keyspaceEntries.map(([database, count]) => {
                    const current = info.database !== null
                      && info.database !== undefined
                      && database === `db${info.database}`;

                    return (
                      <Tag key={database} color={current ? 'blue' : 'default'}>
                        {database}：{count} 键{current ? '（当前）' : ''}
                      </Tag>
                    );
                  })
                )}
              </Space>
            </div>
          </Space>
        )}
      </Card>
    </Space>
  );
}

/** 字节数转可读容量（Redis 的 used_memory 是字节）。 */
function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/** 键空间命中率；两侧都缺数据时显示占位符。 */
function formatHitRate(hits?: number | null, misses?: number | null): string {
  if (hits === undefined || hits === null || misses === undefined || misses === null) {
    return '—';
  }

  const total = hits + misses;

  return total === 0 ? '—' : `${((hits / total) * 100).toFixed(1)}%`;
}
