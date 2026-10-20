import {
  DeleteOutlined,
  DownloadOutlined,
  FileTextOutlined,
  HistoryOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { App, Button, Input, Modal, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import { deploymentsApi } from '../api';
import type {
  DeploymentDefinitionResponse,
  DeploymentResource,
  DeploymentSummaryResponse,
  ProcessDefinitionResponse,
} from '../api/types';
import { formatTime } from '../utils';

export default function DeploymentsPage() {
  const { message } = App.useApp();

  const [keyword, setKeyword] = useState('');

  const deploymentsQuery = useQuery({
    queryKey: ['designer', 'deployments'],
    queryFn: () => deploymentsApi.list({ firstResult: 0, maxResults: 200 }),
  });

  // useMemo 稳定空数组引用，避免下游 useMemo 依赖每次渲染变化
  const deployments = useMemo(() => deploymentsQuery.data?.items ?? [], [deploymentsQuery.data]);
  const loading = deploymentsQuery.isFetching;

  useEffect(() => {
    if (deploymentsQuery.isError) {
      message.error(`加载部署列表失败：${(deploymentsQuery.error as Error).message}`);
    }
  }, [deploymentsQuery.isError, deploymentsQuery.error, message]);

  const filteredDeployments = useMemo(() => {
    const query = keyword.trim().toLowerCase();

    if (query.length === 0) {
      return deployments;
    }

    // 命中部署名 / 部署 id / 租户，或该部署下任一流程定义的 key、名称
    return deployments.filter((deployment) => {
      const haystacks = [
        deployment.name ?? '',
        deployment.deploymentId,
        deployment.tenantId ?? '',
        ...deployment.definitions.flatMap((definition) => [definition.key, definition.name ?? '']),
      ];

      return haystacks.some((field) => field.toLowerCase().includes(query));
    });
  }, [deployments, keyword]);

  // 三个弹窗的数据源：target 为 null 时 enabled=false 不发请求；由 query 统一缓存 / 错误处理
  const [xmlTarget, setXmlTarget] = useState<DeploymentDefinitionResponse | null>(null);
  const [versionsKey, setVersionsKey] = useState<string | null>(null);
  const [resourcesTarget, setResourcesTarget] = useState<DeploymentSummaryResponse | null>(null);

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
      width: 340,
      render: (_, record) => (
        <Space>
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
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <Typography.Text type="secondary">
            {keyword.trim().length > 0
              ? `匹配 ${filteredDeployments.length} / ${deployments.length} 次部署`
              : `共 ${deployments.length} 次部署`}
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
    </Space>
  );
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
