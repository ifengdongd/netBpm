import { DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons';
import { App, Button, Input, Popconfirm, Space, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';

import { modelsApi } from '../api';
import type { BpmnModelSummary } from '../api/types';
import { formatTime } from '../utils';

export default function ModelsPage() {
  const { message } = App.useApp();
  const navigate = useNavigate();

  const [models, setModels] = useState<BpmnModelSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [keyword, setKeyword] = useState('');

  const filteredModels = useMemo(() => {
    const query = keyword.trim().toLowerCase();

    if (query.length === 0) {
      return models;
    }

    return models.filter((model) =>
      [model.name, model.processKey, model.description ?? ''].some((field) =>
        field.toLowerCase().includes(query),
      ),
    );
  }, [models, keyword]);

  const load = useCallback(async () => {
    setLoading(true);

    try {
      setModels(await modelsApi.list());
    } catch (error) {
      message.error(`加载模型列表失败：${(error as Error).message}`);
    } finally {
      setLoading(false);
    }
  }, [message]);

  useEffect(() => {
    void load();
  }, [load]);

  const handleDelete = useCallback(
    async (id: string, name: string) => {
      try {
        await modelsApi.remove(id);
        message.success(`已删除：${name}`);
        await load();
      } catch (error) {
        message.error(`删除失败：${(error as Error).message}`);
      }
    },
    [load, message],
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
      width: 180,
      render: (_, record) => (
        <Space>
          <Button
            size="small"
            icon={<EditOutlined />}
            onClick={() => navigate(`/models/editor?modelId=${encodeURIComponent(record.id)}`)}
          >
            编辑
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
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <Typography.Text type="secondary">
            {keyword.trim().length > 0 ? `匹配 ${filteredModels.length} / ${models.length} 个模型` : `共 ${models.length} 个模型`}
          </Typography.Text>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/models/editor?action=new')}>
          新建模型
        </Button>
      </Space>
      <Table<BpmnModelSummary>
        rowKey="id"
        columns={columns}
        dataSource={filteredModels}
        loading={loading}
        pagination={{ pageSize: 20, hideOnSinglePage: true }}
      />
    </Space>
  );
}
