import {
  CloudUploadOutlined,
  DeleteOutlined,
  DownloadOutlined,
  EditOutlined,
  HistoryOutlined,
  PlusOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { App, Button, Input, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { modelsApi } from '../api';
import type { BpmnModelSummary } from '../api/types';
import ModelHistoryDrawer from '../components/ModelHistoryDrawer';
import { usePagedList } from '../hooks/usePagedList';
import { formatTime } from '../utils';
import { renderDiagramSvg } from '../utils/diagram';

export default function ModelsPage() {
  const { message, modal } = App.useApp();
  const navigate = useNavigate();

  // 搜索框即时输入值与已提交关键字（提交后由 queryKey 变化触发重新拉取）
  const [keywordInput, setKeywordInput] = useState('');
  const [keyword, setKeyword] = useState('');
  const [historyModel, setHistoryModel] = useState<BpmnModelSummary | null>(null);
  // 部署进行中的模型 id（行级 loading）
  const [deploying, setDeploying] = useState<string | null>(null);

  // 服务端分页 + 关键字过滤（queryKey 含关键字，变更自动重查）
  const modelList = usePagedList<BpmnModelSummary>(
    ['admin', 'models', keyword],
    (page, signal) =>
      modelsApi.list(
        {
          keyword: keyword || undefined,
          firstResult: (page.current - 1) * page.pageSize,
          maxResults: page.pageSize,
        },
        signal,
      ),
    (error) => message.error(`加载模型列表失败：${error.message}`),
    // 模型列表低频变化（操作后有 invalidate 联动），放宽新鲜度减少来回切换的重拉
    { staleTime: 30_000 },
  );

  const handleSearch = (value: string) => {
    const trimmed = value.trim();
    setKeywordInput(trimmed);

    if (trimmed === keyword) {
      // 相同关键字重复提交：强制刷新
      modelList.reload();
      return;
    }

    setKeyword(trimmed);

    if (modelList.page.current !== 1) {
      modelList.setPage({ ...modelList.page, current: 1 });
    }
  };

  const handleDelete = useCallback(
    async (id: string, name: string) => {
      try {
        await modelsApi.remove(id);
        message.success(`已删除：${name}`);
        modelList.reload();
      } catch (error) {
        message.error(`删除失败：${(error as Error).message}`);
      }
    },
    [modelList, message],
  );

  /** 导出：拉取模型详情，把 BPMN XML 下载为 .bpmn20.xml 文件。 */
  const handleExport = useCallback(
    async (record: BpmnModelSummary) => {
      try {
        const detail = await modelsApi.get(record.id);
        const blob = new Blob([detail.bpmnXml], { type: 'application/xml' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');

        anchor.href = url;
        anchor.download = `${detail.processKey || record.id}.bpmn20.xml`;
        anchor.click();
        URL.revokeObjectURL(url);
        message.success(`已导出：${detail.processKey || record.id}.bpmn20.xml`);
      } catch (error) {
        message.error(`导出失败：${(error as Error).message}`);
      }
    },
    [message],
  );

  /**
   * 部署：先取详情渲染流程图 SVG，再走引擎部署（解析 + 标准版 BPMN 校验 + 版本递增）。
   * 部署成功时把 XML 连同图片交给后端写入 publish 版本快照（历史版本可查图）。
   */
  const handleDeploy = useCallback(
    async (record: BpmnModelSummary) => {
      setDeploying(record.id);

      try {
        const detail = await modelsApi.get(record.id);
        const svg = await renderDiagramSvg(detail.bpmnXml);
        const deployed = await modelsApi.publish(record.id, svg ?? undefined);
        const warnings = deployed.warnings ?? [];

        if (warnings.length === 0) {
          message.success(`已部署：${deployed.processDefinitionKey} v${deployed.version}`);
        } else {
          // 提示级问题（如委托解析不到）不阻断部署，但实例跑到对应节点会失败 ——
          // 用弹窗逐条列出，避免被一行 success 提示淹没。
          modal.warning({
            title: `已部署：${deployed.processDefinitionKey} v${deployed.version}（${warnings.length} 条提示）`,
            width: 640,
            content: (
              <Space direction="vertical" size={8} style={{ width: '100%' }}>
                <Typography.Text type="secondary">
                  部署已成功。以下问题不影响定义落库，但实例运行到相关节点时可能失败，建议先修正再发起实例。
                </Typography.Text>
                {warnings.map((warning, index) => (
                  <Space key={`${warning.rule}-${index}`} align="start">
                    <Tag color="warning">{warning.rule}</Tag>
                    <Typography.Text>{warning.message}</Typography.Text>
                  </Space>
                ))}
              </Space>
            ),
          });
        }

        modelList.reload();
      } catch (error) {
        message.error(`部署失败：${(error as Error).message}`);
      } finally {
        setDeploying(null);
      }
    },
    [message, modal, modelList],
  );

  const columns: ColumnsType<BpmnModelSummary> = [
    {
      title: '流程名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string) => <Typography.Text strong>{name}</Typography.Text>,
    },
    {
      title: '流程 Key',
      dataIndex: 'processKey',
      key: 'processKey',
      render: (key: string) => <Tag>{key}</Tag>,
    },
    {
      title: '最近修改',
      dataIndex: 'modifyTime',
      key: 'modifyTime',
      width: 200,
      render: (value: string) => formatTime(value),
    },
    {
      title: '操作',
      key: 'actions',
      width: 340,
      render: (_, record) => (
        <Space>
          <Button
            size="small"
            icon={<EditOutlined />}
            onClick={() => navigate(`/models/editor?modelId=${encodeURIComponent(record.id)}`)}
          >
            编辑
          </Button>
          <Button
            size="small"
            icon={<CloudUploadOutlined />}
            loading={deploying === record.id}
            onClick={() =>
              modal.confirm({
                title: `部署流程模型「${record.name}」？`,
                content: '将按标准版 BPMN 规则校验流程图，通过后部署到引擎并递增版本；同时把当前流程图写入版本历史。',
                okText: '部署',
                cancelText: '取消',
                onOk: () => handleDeploy(record),
              })
            }
          >
            部署
          </Button>
          <Button
            size="small"
            icon={<DownloadOutlined />}
            onClick={() => void handleExport(record)}
          >
            导出
          </Button>
          <Button
            size="small"
            icon={<HistoryOutlined />}
            onClick={() => setHistoryModel(record)}
          >
            历史
          </Button>
          <Popconfirm
            title={`删除流程模型「${record.name}」？`}
            description="删除后不可恢复（不影响已部署的定义）。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void handleDelete(record.id, record.name)}
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
            placeholder="搜索名称 / 流程 Key / 描述"
            style={{ width: 280 }}
            value={keywordInput}
            onChange={(event) => setKeywordInput(event.target.value)}
            onSearch={handleSearch}
          />
          <Typography.Text type="secondary">
            {keyword ? `匹配 ${modelList.total} 个模型` : `共 ${modelList.total} 个模型`}
          </Typography.Text>
        </Space>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={modelList.reload}>
            刷新
          </Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/models/editor?action=new')}>
            新建模型
          </Button>
        </Space>
      </Space>
      <Table<BpmnModelSummary>
        rowKey="id"
        columns={columns}
        dataSource={modelList.items}
        loading={modelList.loading}
        pagination={modelList.pagination}
      />

      <ModelHistoryDrawer
        modelId={historyModel?.id ?? null}
        modelName={historyModel?.name ?? null}
        open={historyModel !== null}
        onClose={() => setHistoryModel(null)}
      />
    </Space>
  );
}
