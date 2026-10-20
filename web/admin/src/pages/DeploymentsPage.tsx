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
import { useCallback, useEffect, useMemo, useState } from 'react';

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

  const [deployments, setDeployments] = useState<DeploymentSummaryResponse[]>([]);
  const [loading, setLoading] = useState(false);
  const [keyword, setKeyword] = useState('');

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

  const [xml, setXml] = useState<{ title: string; content: string; fileName: string } | null>(null);
  const [versions, setVersions] = useState<{ key: string; rows: ProcessDefinitionResponse[]; loading: boolean } | null>(null);
  const [resources, setResources] = useState<{
    deploymentId: string;
    name?: string | null;
    rows: DeploymentResource[];
    loading: boolean;
  } | null>(null);

  const load = useCallback(async () => {
    setLoading(true);

    try {
      const page = await deploymentsApi.list({ firstResult: 0, maxResults: 200 });
      setDeployments(page.items);
    } catch (error) {
      message.error(`加载部署列表失败：${(error as Error).message}`);
    } finally {
      setLoading(false);
    }
  }, [message]);

  useEffect(() => {
    void load();
  }, [load]);

  const toggleSuspension = async (definition: DeploymentDefinitionResponse) => {
    try {
      if (definition.isSuspended) {
        await deploymentsApi.activate(definition.processDefinitionId, true);
        message.success(`已激活：${definition.key} v${definition.version}`);
      } else {
        await deploymentsApi.suspend(definition.processDefinitionId, true);
        message.success(`已挂起：${definition.key} v${definition.version}`);
      }

      await load();
    } catch (error) {
      message.error(`操作失败：${(error as Error).message}`);
    }
  };

  const showXml = async (definition: DeploymentDefinitionResponse) => {
    try {
      const content = await deploymentsApi.definitionXml(definition.processDefinitionId);
      setXml({
        title: `${definition.key} v${definition.version} — ${definition.processDefinitionId}`,
        content,
        fileName: `${definition.key}-v${definition.version}.bpmn20.xml`,
      });
    } catch (error) {
      message.error(`获取 XML 失败：${(error as Error).message}`);
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

  const showResources = async (deployment: DeploymentSummaryResponse) => {
    setResources({ deploymentId: deployment.deploymentId, name: deployment.name, rows: [], loading: true });

    try {
      const rows = await deploymentsApi.resources(deployment.deploymentId);
      setResources({ deploymentId: deployment.deploymentId, name: deployment.name, rows, loading: false });
    } catch (error) {
      message.error(`获取资源清单失败：${(error as Error).message}`);
      setResources(null);
    }
  };

  const showVersions = async (definition: DeploymentDefinitionResponse) => {
    setVersions({ key: definition.key, rows: [], loading: true });

    try {
      const page = await deploymentsApi.versions(definition.key, { firstResult: 0, maxResults: 200 });
      setVersions({ key: definition.key, rows: page.items, loading: false });
    } catch (error) {
      message.error(`获取版本失败：${(error as Error).message}`);
      setVersions(null);
    }
  };

  const removeDeployment = async (deployment: DeploymentSummaryResponse) => {
    try {
      await deploymentsApi.removeDeployment(deployment.deploymentId, true);
      message.success(`已删除部署：${deployment.name ?? deployment.deploymentId}`);
      await load();
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
          <Button size="small" icon={<HistoryOutlined />} onClick={() => void showVersions(record)}>
            版本
          </Button>
          <Button size="small" icon={<FileTextOutlined />} onClick={() => void showXml(record)}>
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
          <Button size="small" onClick={() => void showResources(record)}>
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
        <Button icon={<ReloadOutlined />} onClick={() => void load()}>
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
        title={xml?.title ?? ''}
        open={xml !== null}
        width={820}
        onCancel={() => setXml(null)}
        footer={
          <Space>
            <Button onClick={() => setXml(null)}>关闭</Button>
            <Button
              type="primary"
              icon={<DownloadOutlined />}
              onClick={() => xml && downloadText(xml.content, xml.fileName)}
            >
              下载 BPMN
            </Button>
          </Space>
        }
      >
        <pre style={{ maxHeight: '60vh', overflow: 'auto', background: '#fafafa', padding: 12, borderRadius: 6, fontSize: 12 }}>
          {xml?.content ?? ''}
        </pre>
      </Modal>

      <Modal
        title={`「${versions?.key ?? ''}」的历史版本`}
        open={versions !== null}
        footer={null}
        width={760}
        onCancel={() => setVersions(null)}
      >
        <Table<ProcessDefinitionResponse>
          rowKey="processDefinitionId"
          loading={versions?.loading}
          dataSource={versions?.rows ?? []}
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
        title={`部署资源清单${resources?.name ? ` · ${resources.name}` : ''}`}
        open={resources !== null}
        footer={null}
        width={680}
        onCancel={() => setResources(null)}
      >
        <Table<DeploymentResource>
          rowKey="processDefinitionId"
          loading={resources?.loading}
          dataSource={resources?.rows ?? []}
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
