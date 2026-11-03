import {
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  FileTextOutlined,
  FolderOpenOutlined,
  HistoryOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  ReloadOutlined,
  RocketOutlined,
} from '@ant-design/icons';
import {
  Alert,
  App,
  Button,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { deploymentsApi, modelsApi, runtimeApi } from '../api';
import { newIdempotencyKey } from '../api/client';
import type {
  DeploymentDefinitionResponse,
  DeploymentResource,
  DeploymentSummaryResponse,
  ProcessDefinitionResponse,
  Variables,
} from '../api/types';
import { formatTime } from '../utils';
import { splitDeploymentsByVersion } from './deployments';

export default function DeploymentsPage() {
  const { message, modal } = App.useApp();
  const navigate = useNavigate();

  // 搜索框输入值 / 已提交关键字：过滤走服务端，部署多了也不会「只搜前 200 条」
  const [keywordInput, setKeywordInput] = useState('');
  const [keyword, setKeyword] = useState('');

  const deploymentsQuery = useQuery({
    queryKey: ['designer', 'deployments', keyword],
    queryFn: () =>
      deploymentsApi.list({
        keyword: keyword || undefined,
        firstResult: 0,
        maxResults: 200,
      }),
    // 部署列表低频变化（写操作后有 refetch），放宽新鲜度
    staleTime: 60_000,
  });

  // useMemo 稳定空数组引用，避免下游 useMemo 依赖每次渲染变化
  const deployments = useMemo(() => deploymentsQuery.data?.items ?? [], [deploymentsQuery.data]);
  const totalDeployments = deploymentsQuery.data?.total ?? deployments.length;
  const loading = deploymentsQuery.isFetching;

  useEffect(() => {
    if (deploymentsQuery.isError) {
      message.error(`加载部署列表失败：${(deploymentsQuery.error as Error).message}`);
    }
  }, [deploymentsQuery.isError, deploymentsQuery.error, message]);

  const applyKeyword = (value: string) => {
    const trimmed = value.trim();
    setKeywordInput(trimmed);
    setKeyword(trimmed);
  };

  /** 是否把「历史部署」也铺在列表里。默认关：同一流程只留最新那次部署。 */
  const [showHistoryDeployments, setShowHistoryDeployments] = useState(false);

  /**
   * 当前部署 / 历史部署。默认只显示前者 —— 发布新版本后，旧部署不再占一行。
   * 旧版本仍可在行内「查看历史版本」逐版查看，打开开关也能看到对应的部署实体
   *（删除部署、资源清单都按部署走，不能被真的合并掉）。
   */
  const { activeDeployments, historyDeployments } = useMemo(
    () => splitDeploymentsByVersion(deployments),
    [deployments],
  );

  const visibleDeployments = showHistoryDeployments ? deployments : activeDeployments;

  // 三个弹窗的数据源：target 为 null 时 enabled=false 不发请求；由 query 统一缓存 / 错误处理
  const [xmlTarget, setXmlTarget] = useState<DeploymentDefinitionResponse | null>(null);
  const [versionsKey, setVersionsKey] = useState<string | null>(null);
  const [resourcesTarget, setResourcesTarget] = useState<DeploymentSummaryResponse | null>(null);

  // 发起实例（部署列表 → 指定版本 → 表单）
  const [startTarget, setStartTarget] = useState<DeploymentDefinitionResponse | null>(null);
  const [startSubmitting, setStartSubmitting] = useState(false);
  const [startForm] = Form.useForm<{ businessKey?: string; startUserId?: string; variables?: string }>();

  const xmlQuery = useQuery({
    queryKey: ['designer', 'definition-xml', xmlTarget?.processDefinitionId],
    queryFn: () => deploymentsApi.definitionXml(xmlTarget!.processDefinitionId),
    enabled: xmlTarget !== null,
  });

  const versionsQuery = useQuery({
    queryKey: ['designer', 'versions', versionsKey],
    queryFn: () => deploymentsApi.versions(versionsKey!, { firstResult: 0, maxResults: 200 }),
    enabled: versionsKey !== null,
  });

  const resourcesQuery = useQuery({
    queryKey: ['designer', 'resources', resourcesTarget?.deploymentId],
    queryFn: () => deploymentsApi.resources(resourcesTarget!.deploymentId),
    enabled: resourcesTarget !== null,
  });

  useEffect(() => {
    if (xmlQuery.isError) {
      message.error(`获取 XML 失败：${(xmlQuery.error as Error).message}`);
    }
  }, [xmlQuery.isError, xmlQuery.error, message]);

  useEffect(() => {
    if (versionsQuery.isError) {
      message.error(`获取版本失败：${(versionsQuery.error as Error).message}`);
    }
  }, [versionsQuery.isError, versionsQuery.error, message]);

  useEffect(() => {
    if (resourcesQuery.isError) {
      message.error(`获取资源清单失败：${(resourcesQuery.error as Error).message}`);
    }
  }, [resourcesQuery.isError, resourcesQuery.error, message]);

  const toggleSuspension = async (definition: DeploymentDefinitionResponse) => {
    try {
      if (definition.isSuspended) {
        await deploymentsApi.activate(definition.processDefinitionId, true);
        message.success(`已激活：${definition.key} v${definition.version}`);
      } else {
        await deploymentsApi.suspend(definition.processDefinitionId, true);
        message.success(`已挂起：${definition.key} v${definition.version}`);
      }

      await deploymentsQuery.refetch();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  /** 把已部署定义另存为设计器模型（拉回线上版本继续修改）。 */
  const saveAsModel = async (definition: DeploymentDefinitionResponse) => {
    try {
      const created = await modelsApi.fromDefinition(
        definition.processDefinitionId,
        `${definition.name ?? definition.key} v${definition.version}`,
      );

      message.success('已从定义创建模型，正在打开设计器');
      navigate(`/models/editor?modelId=${encodeURIComponent(created.id)}`);
    } catch (error) {
      message.error(`创建模型失败：${(error as Error).message}`);
    }
  };

  /** 打开发起实例表单（清掉上一次输入，避免带到另一个定义）。 */
  const openStart = (definition: DeploymentDefinitionResponse) => {
    startForm.resetFields();
    setStartTarget(definition);
  };

  /** 发起实例：按 processDefinitionId 精确发起当前这一版，不随新版本漂移。 */
  const startInstance = async () => {
    if (startTarget === null) {
      return;
    }

    let values: { businessKey?: string; startUserId?: string; variables?: string };

    try {
      values = await startForm.validateFields();
    } catch {
      // 字段校验失败时 antd 已就地展示错误
      return;
    }

    setStartSubmitting(true);

    try {
      const started = await runtimeApi.startInstance(
        {
          processDefinitionId: startTarget.processDefinitionId,
          businessKey: values.businessKey?.trim() || undefined,
          startUserId: values.startUserId?.trim() || undefined,
          variables: parseVariables(values.variables),
        },
        newIdempotencyKey(),
      );

      setStartTarget(null);

      modal.success({
        title: `已发起：${startTarget.key} v${startTarget.version}`,
        content: (
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Typography.Text>
              流程实例 ID：
              <Typography.Text code copyable>
                {started.processInstanceId}
              </Typography.Text>
            </Typography.Text>
            <Button
              type="primary"
              onClick={() =>
                navigate(`/monitoring?instanceId=${encodeURIComponent(started.processInstanceId)}`)
              }
            >
              查看实例详情
            </Button>
          </Space>
        ),
      });
    } catch (error) {
      message.error(`发起失败：${(error as Error).message}`);
    } finally {
      setStartSubmitting(false);
    }
  };

  const downloadDefinition = async (definition: DeploymentDefinitionResponse) => {
    try {
      const content = await deploymentsApi.definitionXml(definition.processDefinitionId);
      downloadText(content, `${definition.key}-v${definition.version}.bpmn20.xml`);
    } catch (error) {
      message.error(`下载 XML 失败：${(error as Error).message}`);
    }
  };

  const removeDeployment = async (deployment: DeploymentSummaryResponse) => {
    try {
      await deploymentsApi.removeDeployment(deployment.deploymentId, true);
      message.success(`已删除部署：${deployment.name ?? deployment.deploymentId}`);
      await deploymentsQuery.refetch();
    } catch (error) {
      message.error(`删除部署失败：${(error as Error).message}`);
    }
  };

  // 展开行（某次部署包含的流程定义）：版本 / Key / 名称 / 状态一眼可见，
  // 操作收成「发起」+ 图标（都带 Tooltip）—— 六个文字按钮并排会把表格撑到 470px，
  // 反而看不清列与列的对应关系。
  const definitionColumns: ColumnsType<DeploymentDefinitionResponse> = [
    { title: '版本', dataIndex: 'version', key: 'version', width: 72, render: (v: number) => <Tag color="blue">v{v}</Tag> },
    {
      title: '流程 Key',
      dataIndex: 'key',
      key: 'key',
      width: 170,
      render: (key: string) => <Typography.Text code>{key}</Typography.Text>,
    },
    { title: '名称', dataIndex: 'name', key: 'name', ellipsis: true, render: (name?: string | null) => name ?? '—' },
    { title: '资源名', dataIndex: 'resourceName', key: 'resourceName', ellipsis: true },
    {
      title: '状态',
      dataIndex: 'isSuspended',
      key: 'isSuspended',
      width: 92,
      render: (suspended: boolean) => (suspended ? <Tag color="warning">已挂起</Tag> : <Tag color="success">运行中</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 250,
      align: 'right',
      render: (_, record) => (
        <Space size={4}>
          <Button
            size="small"
            type="primary"
            ghost
            icon={<RocketOutlined />}
            disabled={record.isSuspended}
            title={record.isSuspended ? '已挂起的流程定义不能发起实例，请先激活' : undefined}
            onClick={() => openStart(record)}
          >
            发起
          </Button>
          <Tooltip title="另存为设计器模型">
            <Button size="small" icon={<CopyOutlined />} onClick={() => void saveAsModel(record)} />
          </Tooltip>
          <Tooltip title={record.isSuspended ? '激活该版本' : '挂起该版本'}>
            <Button
              size="small"
              icon={record.isSuspended ? <PlayCircleOutlined /> : <PauseCircleOutlined />}
              onClick={() => void toggleSuspension(record)}
            />
          </Tooltip>
          <Tooltip title="查看历史版本">
            <Button size="small" icon={<HistoryOutlined />} onClick={() => setVersionsKey(record.key)} />
          </Tooltip>
          <Tooltip title="查看 BPMN XML">
            <Button size="small" icon={<FileTextOutlined />} onClick={() => setXmlTarget(record)} />
          </Tooltip>
          <Tooltip title="下载 BPMN 文件">
            <Button size="small" icon={<DownloadOutlined />} onClick={() => void downloadDefinition(record)} />
          </Tooltip>
        </Space>
      ),
    },
  ];

  // 租户列只在真有租户时出现：OPMS 部署的流程都不带租户，常年一列「—」纯占宽
  const showTenant = deployments.some((d) => (d.tenantId ?? '').trim() !== '');

  const columns: ColumnsType<DeploymentSummaryResponse> = [
    {
      title: '部署',
      key: 'name',
      ellipsis: true,
      render: (_, record) => {
        const count = record.definitionCount ?? record.definitions.length;
        const suspended = record.suspendedCount ?? 0;

        return (
          <Space direction="vertical" size={0} style={{ lineHeight: 1.6 }}>
            <Space size={6}>
              <Typography.Text strong ellipsis={{ tooltip: record.name ?? '' }}>
                {record.name || '（未命名部署）'}
              </Typography.Text>
              {suspended > 0 ? <Tag color="orange">挂起 {suspended}</Tag> : null}
            </Space>
            <Typography.Text
              type="secondary"
              style={{ fontSize: 12 }}
              // 部署 ID 只在排查时要，收起成次要信息 + 一键复制，不再单独占一列
              copyable={{ text: record.deploymentId, tooltips: ['复制部署 ID', '已复制'] }}
            >
              {count} 个定义
            </Typography.Text>
          </Space>
        );
      },
    },
    {
      title: '流程 Key',
      key: 'keys',
      width: 190,
      render: (_, record) => {
        // 一次部署可能含多个定义：只铺前两个，其余折成「+N」，避免把列撑爆
        const keys = [...new Set(record.definitions.map((d) => d.key))];
        if (keys.length === 0) return <Typography.Text type="secondary">—</Typography.Text>;

        return (
          <Space size={4} wrap>
            {keys.slice(0, 2).map((key) => (
              <Tag key={key} style={{ marginInlineEnd: 0 }}>
                {key}
              </Tag>
            ))}
            {keys.length > 2 ? <Tag style={{ marginInlineEnd: 0 }}>+{keys.length - 2}</Tag> : null}
          </Space>
        );
      },
    },
    {
      title: '部署时间',
      dataIndex: 'deployTime',
      key: 'deployTime',
      width: 170,
      render: (v?: string | null) => formatTime(v),
    },
    {
      title: '最新版本',
      key: 'latestVersion',
      width: 110,
      render: (_, record) => {
        const latest = record.latestVersion ?? Math.max(0, ...record.definitions.map((d) => d.version));
        return latest > 0 ? <Tag color="blue">v{latest}</Tag> : <Typography.Text type="secondary">—</Typography.Text>;
      },
    },
    ...(showTenant
      ? [{
          title: '租户',
          dataIndex: 'tenantId' as const,
          key: 'tenantId',
          width: 110,
          render: (v?: string | null) => v || '—',
        }]
      : []),
    {
      title: '操作',
      key: 'actions',
      width: 96,
      align: 'right' as const,
      render: (_, record) => (
        <Space size={4}>
          <Tooltip title="查看资源清单">
            <Button size="small" icon={<FolderOpenOutlined />} onClick={() => setResourcesTarget(record)} />
          </Tooltip>
          <Popconfirm
            title={`删除部署「${record.name || record.deploymentId}」？`}
            description="将级联删除其运行中的流程实例（历史保留）。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void removeDeployment(record)}
          >
            <Tooltip title="删除部署">
              <Button size="small" danger icon={<DeleteOutlined />} />
            </Tooltip>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      {/* 工具条：搜索 + 计数在左，刷新在右；与表格同宽，不再各占一行 */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <Input.Search
          allowClear
          placeholder="搜索部署名 / 部署 ID / 流程 Key"
          style={{ width: 320 }}
          value={keywordInput}
          onChange={(event) => {
            setKeywordInput(event.target.value);

            // 清空输入即撤销过滤（allowClear 不保证触发 onSearch）
            if (event.target.value === '' && keyword !== '') {
              setKeyword('');
            }
          }}
          onSearch={applyKeyword}
        />
        <Typography.Text type="secondary">
          {keyword ? `匹配 ${deployments.length} 次部署（共 ${totalDeployments} 次）` : `共 ${totalDeployments} 次部署`}
          {historyDeployments.length > 0 && !showHistoryDeployments
            ? ` · 已折叠 ${historyDeployments.length} 次历史部署`
            : ''}
        </Typography.Text>
        <Tooltip title="每次发布都会新建一次部署。打开后连被新版本取代的旧部署一起列出（旧版本也可以只在行内「查看历史版本」里看）。">
          <Space size={6} style={{ cursor: 'pointer' }}>
            <Switch
              size="small"
              checked={showHistoryDeployments}
              onChange={setShowHistoryDeployments}
              aria-label="显示历史部署"
            />
            <Typography.Text type="secondary">
              显示历史部署{historyDeployments.length > 0 ? `（${historyDeployments.length}）` : ''}
            </Typography.Text>
          </Space>
        </Tooltip>
        <div style={{ flex: 1 }} />
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void deploymentsQuery.refetch()}>
          刷新
        </Button>
      </div>
      <Table<DeploymentSummaryResponse>
        rowKey="deploymentId"
        // fixed：列宽按配置走，宽屏下「部署」列不会把其它列挤到两端
        tableLayout="fixed"
        columns={columns}
        dataSource={visibleDeployments}
        loading={loading}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有部署记录" /> }}
        expandable={{
          expandedRowRender: (record) => (
            <Table<DeploymentDefinitionResponse>
              rowKey="processDefinitionId"
              tableLayout="fixed"
              columns={definitionColumns}
              dataSource={record.definitions}
              pagination={false}
              size="small"
              style={{ margin: '-4px 0' }}
            />
          ),
        }}
        pagination={{ pageSize: 20, hideOnSinglePage: true, showSizeChanger: false }}
      />

      <Modal
        title={
          xmlTarget
            ? `${xmlTarget.key} v${xmlTarget.version} — ${xmlTarget.processDefinitionId}`
            : ''
        }
        open={xmlTarget !== null}
        width={820}
        onCancel={() => setXmlTarget(null)}
        footer={
          <Space>
            <Button onClick={() => setXmlTarget(null)}>关闭</Button>
            <Button
              type="primary"
              icon={<DownloadOutlined />}
              loading={xmlQuery.isFetching}
              onClick={() =>
                xmlTarget !== null &&
                xmlQuery.data !== undefined &&
                downloadText(
                  xmlQuery.data,
                  `${xmlTarget.key}-v${xmlTarget.version}.bpmn20.xml`,
                )
              }
            >
              下载 BPMN
            </Button>
          </Space>
        }
      >
        <pre style={{ maxHeight: '60vh', overflow: 'auto', background: '#fafafa', padding: 12, borderRadius: 6, fontSize: 12 }}>
          {xmlQuery.isPending && xmlTarget !== null ? '加载中…' : (xmlQuery.data ?? '')}
        </pre>
      </Modal>

      <Modal
        title={`「${versionsKey ?? ''}」的历史版本`}
        open={versionsKey !== null}
        footer={null}
        width={760}
        onCancel={() => setVersionsKey(null)}
      >
        <Table<ProcessDefinitionResponse>
          rowKey="processDefinitionId"
          loading={versionsQuery.isPending || versionsQuery.isFetching}
          dataSource={versionsQuery.data?.items ?? []}
          pagination={false}
          size="small"
          columns={[
            { title: '版本', dataIndex: 'version', key: 'version', width: 80, render: (v: number) => `v${v}` },
            { title: '定义 ID', dataIndex: 'processDefinitionId', key: 'processDefinitionId', ellipsis: true },
            { title: '名称', dataIndex: 'name', key: 'name', render: (n?: string | null) => n ?? '—' },
            { title: '部署时间', dataIndex: 'deployTime', key: 'deployTime', width: 180, render: (v?: string | null) => formatTime(v) },
            {
              title: '状态',
              dataIndex: 'isSuspended',
              key: 'isSuspended',
              width: 90,
              render: (s: boolean) => (s ? <Tag color="warning">已挂起</Tag> : <Tag color="success">运行中</Tag>),
            },
          ]}
        />
      </Modal>

      <Modal
        title={`部署资源清单${resourcesTarget?.name ? ` · ${resourcesTarget.name}` : ''}`}
        open={resourcesTarget !== null}
        footer={null}
        width={680}
        onCancel={() => setResourcesTarget(null)}
      >
        <Table<DeploymentResource>
          rowKey="processDefinitionId"
          loading={resourcesQuery.isPending || resourcesQuery.isFetching}
          dataSource={resourcesQuery.data ?? []}
          pagination={false}
          size="small"
          columns={[
            { title: '资源名', dataIndex: 'name', key: 'name', render: (v?: string | null) => v ?? '—' },
            { title: '流程定义 ID', dataIndex: 'processDefinitionId', key: 'processDefinitionId', ellipsis: true },
          ]}
        />
      </Modal>

      <Modal
        title={startTarget ? `发起实例：${startTarget.key} v${startTarget.version}` : ''}
        open={startTarget !== null}
        confirmLoading={startSubmitting}
        okText="发起"
        cancelText="取消"
        onOk={() => void startInstance()}
        onCancel={() => setStartTarget(null)}
        destroyOnClose
      >
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          <Alert
            type="info"
            showIcon
            message={`按流程定义 ${startTarget?.processDefinitionId ?? ''} 发起（精确到该版本，不随新版本漂移）。`}
          />
          <Form form={startForm} layout="vertical" preserve={false}>
            <Form.Item name="businessKey" label="业务主键（可选）">
              <Input placeholder="如 ORDER-20261021-001" autoComplete="off" />
            </Form.Item>
            <Form.Item
              name="startUserId"
              label="发起人（可选）"
              extra="留空则使用当前登录身份；JWT 模式下填写值必须与令牌 sub 一致，否则 403。"
            >
              <Input placeholder="如 zhangsan" autoComplete="off" />
            </Form.Item>
            <Form.Item
              name="variables"
              label="初始变量（JSON 对象，可留空）"
              rules={[{ validator: validateVariables }]}
            >
              <Input.TextArea rows={5} placeholder='{ "amount": 1200, "applicant": "zhangsan" }' />
            </Form.Item>
          </Form>
        </Space>
      </Modal>
    </Space>
  );
}

/** 解析发起实例的初始变量：空 → undefined；必须是 JSON 对象（与服务端 JsonVariableReader 口径一致）。 */
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

/** 表单校验：提前把 JSON 语法错误挡在提交之前。 */
function validateVariables(_: unknown, value?: string): Promise<void> {
  try {
    parseVariables(value);
    return Promise.resolve();
  } catch (error) {
    return Promise.reject(error instanceof Error ? error : new Error('变量必须是 JSON 对象'));
  }
}

/** 触发浏览器下载文本文件（BPMN XML / 资源清单等）。 */
function downloadText(content: string, fileName: string, mime = 'application/xml'): void {
  const blob = new Blob([content], { type: `${mime};charset=utf-8` });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');

  anchor.href = url;
  anchor.download = fileName;
  anchor.click();

  URL.revokeObjectURL(url);
}
