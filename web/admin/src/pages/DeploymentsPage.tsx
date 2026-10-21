import {
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  FileTextOutlined,
  HistoryOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  ReloadOutlined,
  RocketOutlined,
} from '@ant-design/icons';
import { Alert, App, Button, Form, Input, Modal, Popconfirm, Space, Table, Tag, Typography } from 'antd';
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

  const filteredDeployments = deployments;

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

  const definitionColumns: ColumnsType<DeploymentDefinitionResponse> = [
    { title: '流程 Key', dataIndex: 'key', key: 'key', render: (key: string) => <Tag>{key}</Tag> },
    { title: '名称', dataIndex: 'name', key: 'name', render: (name?: string | null) => name ?? '—' },
    { title: '版本', dataIndex: 'version', key: 'version', width: 80, render: (v: number) => `v${v}` },
    { title: '资源名', dataIndex: 'resourceName', key: 'resourceName', ellipsis: true },
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
      width: 470,
      render: (_, record) => (
        <Space>
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
          <Button
            size="small"
            icon={<CopyOutlined />}
            title="把该定义另存为设计器模型"
            onClick={() => void saveAsModel(record)}
          />
          <Button
            size="small"
            icon={record.isSuspended ? <PlayCircleOutlined /> : <PauseCircleOutlined />}
            onClick={() => void toggleSuspension(record)}
          >
            {record.isSuspended ? '激活' : '挂起'}
          </Button>
          <Button size="small" icon={<HistoryOutlined />} onClick={() => setVersionsKey(record.key)}>
            版本
          </Button>
          <Button size="small" icon={<FileTextOutlined />} onClick={() => setXmlTarget(record)}>
            XML
          </Button>
          <Button size="small" icon={<DownloadOutlined />} onClick={() => void downloadDefinition(record)}>
            下载
          </Button>
        </Space>
      ),
    },
  ];

  const columns: ColumnsType<DeploymentSummaryResponse> = [
    {
      title: '部署名',
      key: 'name',
      render: (_, record) => <Typography.Text strong>{record.name || '（未命名部署）'}</Typography.Text>,
    },
    { title: '部署时间', dataIndex: 'deployTime', key: 'deployTime', width: 200, render: (v?: string | null) => formatTime(v) },
    {
      title: '定义',
      key: 'definitionCount',
      width: 110,
      render: (_, record) => {
        const count = record.definitionCount ?? record.definitions.length;
        const latest = record.latestVersion ?? Math.max(0, ...record.definitions.map((d) => d.version));
        const suspended = record.suspendedCount ?? 0;

        return (
          <Space size={4} wrap>
            <Tag>{count} 个</Tag>
            {latest > 0 ? <Tag color="blue">v{latest}</Tag> : null}
            {suspended > 0 ? <Tag color="orange">挂起 {suspended}</Tag> : null}
          </Space>
        );
      },
    },
    { title: '租户', dataIndex: 'tenantId', key: 'tenantId', width: 120, render: (v?: string | null) => v || '—' },
    {
      title: '操作',
      key: 'actions',
      width: 180,
      render: (_, record) => (
        <Space>
          <Button size="small" onClick={() => setResourcesTarget(record)}>
            资源
          </Button>
          <Popconfirm
            title={`删除部署「${record.name || record.deploymentId}」？`}
            description="将级联删除其运行中的流程实例（历史保留）。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void removeDeployment(record)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
        <Space wrap>
          <Input.Search
            allowClear
            placeholder="搜索部署名 / 部署 ID / 流程 Key"
            style={{ width: 300 }}
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
          </Typography.Text>
        </Space>
        <Button icon={<ReloadOutlined />} onClick={() => void deploymentsQuery.refetch()}>
          刷新
        </Button>
      </Space>
      <Table<DeploymentSummaryResponse>
        rowKey="deploymentId"
        columns={columns}
        dataSource={filteredDeployments}
        loading={loading}
        expandable={{
          expandedRowRender: (record) => (
            <Table<DeploymentDefinitionResponse>
              rowKey="processDefinitionId"
              columns={definitionColumns}
              dataSource={record.definitions}
              pagination={false}
              size="small"
            />
          ),
        }}
        pagination={{ pageSize: 20, hideOnSinglePage: true }}
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
