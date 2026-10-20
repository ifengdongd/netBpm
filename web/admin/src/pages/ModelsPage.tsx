import {
  DeleteOutlined,
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

export default function ModelsPage() {
  const { message } = App.useApp();
  const navigate = useNavigate();

  // 搜索框即时输入值与已提交关键字（提交后由 queryKey 变化触发重新拉取）
  const [keywordInput, setKeywordInput] = useState('');
  const [keyword, setKeyword] = useState('');
  const [historyModel, setHistoryModel] = useState<BpmnModelSummary | null>(null);

  // 服务端分页 + 关键字过滤（queryKey 含关键字，变更自动重查）
  const modelList = usePagedList<BpmnModelSummary>(
    ['admin', 'models', keyword],
    (page) =>
      modelsApi.list({
        keyword: keyword || undefined,
        firstResult: (page.current - 1) * page.pageSize,
        maxResults: page.pageSize,
      }),
    (error) => message.error(`加载模型列表失败：${error.message}`),
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
      width: 240,
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
