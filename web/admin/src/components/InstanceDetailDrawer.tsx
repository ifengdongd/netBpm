import { DeleteOutlined, PauseCircleOutlined, PlayCircleOutlined, SwapOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  AutoComplete,
  Button,
  Checkbox,
  Descriptions,
  Divider,
  Drawer,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Select,
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

import { deploymentsApi, runtimeApi } from '../api';
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

  // 变量直写（管理端高危动作：与运行时 PUT /variables 同口径，后端会写审计）
  const [varsOpen, setVarsOpen] = useState(false);
  const [varsSubmitting, setVarsSubmitting] = useState(false);
  const [varsForm] = Form.useForm<{ variables: string }>();

  // 活动实例 modify：取消 / 在活动之前启动（activityId 允许手填，便于并行 / 多实例场景）
  const [modifyActivityId, setModifyActivityId] = useState('');
  const [modifying, setModifying] = useState(false);

  // 实例迁移：先预演（validateOnly）再执行，避免点错目标版本后无法回退
  const [migrateOpen, setMigrateOpen] = useState(false);
  const [migrating, setMigrating] = useState(false);
  const [migrateForm] = Form.useForm<{
    targetProcessDefinitionId: string;
    reason?: string;
    validateOnly?: boolean;
  }>();

  // 目标定义候选：从部署列表摊平（部署 → 定义），够用且不额外加接口
  const definitionsQuery = useQuery({
    queryKey: ['designer', 'deployments', 'migrate-targets'],
    queryFn: () => deploymentsApi.list({ maxResults: 200 }),
    enabled: open && instanceId !== null,
  });

  /** 迁移目标候选：只给 id + key + 版本，避免顺手选错。 */
  const definitionOptions = (definitionsQuery.data?.items ?? []).flatMap((deployment) =>
    deployment.definitions.map((definition) => ({
      value: definition.processDefinitionId,
      label: `${definition.key} v${definition.version}${definition.isSuspended ? '（已挂起）' : ''}`,
    })),
  );

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

  /**
   * 活动级 modify：
   * - cancel：该活动上的待办 / 定时作业 / 历史一并收口，其它并行分支不受影响；
   * - start-before：额外开一条分支，目标活动会真正开始执行（不是「挪动」现有令牌）。
   */
  const runModify = async (action: 'cancel' | 'start-before') => {
    const activityId = modifyActivityId.trim();

    if (instance === null || activityId.length === 0) {
      return;
    }

    setModifying(true);

    try {
      if (action === 'cancel') {
        await runtimeApi.cancelActivity(instance.id, activityId, '控制台取消活动');
        message.success(`已取消活动 ${activityId}`);
      } else {
        await runtimeApi.startBeforeActivity(instance.id, activityId, '控制台活动前启动');
        message.success(`已在 ${activityId} 之前启动新分支`);
      }

      setModifyActivityId('');
      onChanged();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    } finally {
      setModifying(false);
    }
  };

  /** 实例迁移：`validateOnly` 只算计划；真正执行后待办会按映射重建。 */
  const submitMigrate = async () => {
    if (instance === null) {
      return;
    }

    let values: { targetProcessDefinitionId: string; reason?: string; validateOnly?: boolean };

    try {
      values = await migrateForm.validateFields();
    } catch {
      return;
    }

    setMigrating(true);

    try {
      await runtimeApi.migrateInstance(
        instance.id,
        values.targetProcessDefinitionId.trim(),
        values.reason,
        undefined,
        values.validateOnly === true,
      );

      if (values.validateOnly === true) {
        message.success('迁移预演通过（未落库），确认无误后取消勾选再执行');
      } else {
        message.success('迁移完成');
        setMigrateOpen(false);
        migrateForm.resetFields();
        onChanged();
      }
    } catch (error) {
      message.error(`迁移失败：${(error as Error).message}`);
    } finally {
      setMigrating(false);
    }
  };

  /** 变量直写：合并写入（不是整体替换），值为 JSON 原生类型。 */
  const handleWriteVariables = async () => {
    if (instance === null) {
      return;
    }

    let values: { variables: string };

    try {
      values = await varsForm.validateFields();
    } catch {
      return;
    }

    let payload: Variables;

    try {
      const parsed = JSON.parse(values.variables) as unknown;

      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        throw new Error('变量必须是 JSON 对象');
      }

      payload = parsed as Variables;
    } catch (error) {
      message.error(`变量格式错误：${(error as Error).message}`);
      return;
    }

    if (Object.keys(payload).length === 0) {
      message.error('至少提供一个变量');
      return;
    }

    setVarsSubmitting(true);

    try {
      await runtimeApi.setInstanceVariables(instance.id, payload);
      message.success(`已写入 ${Object.keys(payload).length} 个变量`);
      setVarsOpen(false);
      varsForm.resetFields();
      onChanged();
    } catch (error) {
      message.error(`写入失败：${(error as Error).message}`);
    } finally {
      setVarsSubmitting(false);
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

  // 「不存在」只在查询成功且运行态 / 历史都没有数据时才成立：
  // 查询失败（403 无权 / 5xx）时 data 也是 undefined，此时再显示"实例不存在"会把
  //「无权查看」误报成「没有这条实例」——错误通路由下方 Alert 承担。
  const isRunning = instance !== null;
  const isHistoric = !isRunning && historic !== null;
  const notFound = detailQuery.isSuccess && !isRunning && !isHistoric;

  const title = isRunning
    ? `实例详情 · ${instance.processDefinitionKey ?? instance.processDefinitionId}`
    : isHistoric
      ? `历史实例 · ${historic.processDefinitionId.split(':')[0] || historic.processDefinitionId}`
      : '实例详情';

  return (
    <Drawer title={title} open={open} width={760} onClose={onClose} destroyOnClose>
      {!loaded && <Spin style={{ display: 'block', margin: '120px auto' }} />}

      {detailQuery.isError && (
        <Alert
          type="error"
          showIcon
          style={{ marginTop: 60 }}
          message="加载实例详情失败"
          description={(detailQuery.error as Error).message}
        />
      )}

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
              <Button
                onClick={() => {
                  varsForm.setFieldsValue({ variables: JSON.stringify(variables, null, 2) });
                  setVarsOpen(true);
                }}
              >
                变量直写
              </Button>
              <Button onClick={() => setMigrateOpen(true)}>迁移定义</Button>
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

          {isRunning && (
            <>
              <Divider orientation="left" plain style={{ margin: '4px 0 8px' }}>
                活动实例（modify）
              </Divider>
              <Space wrap>
                <AutoComplete
                  style={{ width: 280 }}
                  placeholder="活动 id（可从当前节点里选，也可手填）"
                  value={modifyActivityId}
                  onChange={(value) => setModifyActivityId(value)}
                  options={activities
                    .filter((activity) => activity.isActive)
                    .map((activity) => ({
                      value: activity.activityId,
                      label: `${activity.activityName ?? activity.activityId}（${activity.activityId}）`,
                    }))}
                  allowClear
                />
                <Button
                  danger
                  disabled={modifying || modifyActivityId.trim().length === 0}
                  onClick={() => void runModify('cancel')}
                >
                  取消活动
                </Button>
                <Button
                  disabled={modifying || modifyActivityId.trim().length === 0}
                  onClick={() => void runModify('start-before')}
                >
                  在活动前启动
                </Button>
              </Space>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                取消：结束该活动的待办 / 定时作业并收口历史；在活动前启动：额外开一条分支，目标活动会重新执行。
              </Typography.Text>
            </>
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

      <Modal
        title="变量直写"
        open={varsOpen}
        confirmLoading={varsSubmitting}
        onOk={() => void handleWriteVariables()}
        onCancel={() => setVarsOpen(false)}
        okText="写入"
        cancelText="取消"
        width={640}
        destroyOnClose
      >
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message="合并写入当前运行实例的流程变量，属于管理端高危操作（会写审计）。"
        />
        <Form form={varsForm} layout="vertical">
          <Form.Item
            name="variables"
            label="变量（JSON 对象）"
            rules={[{ required: true, whitespace: true, message: '请输入 JSON 对象' }]}
          >
            <Input.TextArea rows={12} placeholder={'{\n  "approved": true\n}'} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="迁移流程实例"
        open={migrateOpen}
        confirmLoading={migrating}
        onOk={() => void submitMigrate()}
        onCancel={() => setMigrateOpen(false)}
        okText="执行"
        cancelText="取消"
        width={640}
        destroyOnClose
      >
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 12 }}
          message="迁移不可逆：目标定义的活动需与新版本对齐，待办会按映射重建。建议先勾选「仅预演」核对计划。"
        />
        <Form form={migrateForm} layout="vertical" initialValues={{ validateOnly: true }}>
          <Form.Item
            name="targetProcessDefinitionId"
            label="目标流程定义"
            rules={[{ required: true, whitespace: true, message: '请选择目标定义版本' }]}
          >
            <Select
              showSearch
              optionFilterProp="label"
              placeholder={definitionsQuery.isLoading ? '加载定义中…' : '选择目标定义版本'}
              options={definitionOptions}
              loading={definitionsQuery.isLoading}
            />
          </Form.Item>
          <Form.Item name="reason" label="迁移原因（可选）">
            <Input placeholder="写入迁移记录与审计" />
          </Form.Item>
          <Form.Item name="validateOnly" valuePropName="checked">
            <Checkbox>仅预演（只算迁移计划，不落库）</Checkbox>
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
