import { DeleteOutlined, EditOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import {
  Alert,
  App,
  AutoComplete,
  Button,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import {
  APPROVAL_OUTCOME_CLASSES,
  APPROVAL_SEMANTICS,
  semanticsApi,
} from '../api/semantics';
import type {
  ApprovalDictionaryEntryView,
  ApprovalDictionaryUpsertRequest,
  DeclaredApprovalOptionsView,
} from '../api/semantics';

interface EditorState {
  open: boolean;
  /** 编辑时的原行（新增为 null）。 */
  editing: ApprovalDictionaryEntryView | null;
  /** 新增时预填的选项 / 环节。 */
  optionId?: string;
  nodeId?: string | null;
}

const SEMANTIC_COLORS: Record<string, string> = {
  APPROVE: 'success',
  REJECT: 'error',
  RETURN: 'warning',
  DELEGATE: 'processing',
  CUSTOM: 'default',
  SETTLED: 'default',
};

function semanticLabel(value: string): string {
  return APPROVAL_SEMANTICS.find((item) => item.value === value)?.label ?? value;
}

function outcomeLabel(value: string): string {
  return APPROVAL_OUTCOME_CLASSES.find((item) => item.value === value)?.label ?? value;
}

/**
 * 审批语义字典维护。
 *
 * 页面的核心不是「表单」，而是左右对照：左边是**流程定义里声明的选项**（唯一真源），
 * 右边是**平台给这些选项补的语义**。没有左边那份清单，运维只能去翻 BPMN 或盲猜 optionId，
 * 配出来的字典「不报错但不生效」。
 */
export default function SemanticsPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<ApprovalDictionaryUpsertRequest & { optionId: string }>();

  const [processKey, setProcessKey] = useState('');
  const [activeKey, setActiveKey] = useState('');
  const [editor, setEditor] = useState<EditorState>({ open: false, editing: null });
  const [saving, setSaving] = useState(false);

  const declaredQuery = useQuery({
    queryKey: ['platform', 'declared', activeKey],
    queryFn: ({ signal }) => semanticsApi.declared(activeKey, signal),
    enabled: activeKey.length > 0,
    retry: false,
  });

  const dictionaryQuery = useQuery({
    queryKey: ['platform', 'dictionary', activeKey],
    queryFn: ({ signal }) => semanticsApi.list(activeKey, signal),
    enabled: activeKey.length > 0,
  });

  const declared: DeclaredApprovalOptionsView | undefined = declaredQuery.data;
  const entries = useMemo(() => dictionaryQuery.data ?? [], [dictionaryQuery.data]);

  useEffect(() => {
    if (processKey.length > 0 && dictionaryQuery.isError) {
      message.error(`加载字典失败：${(dictionaryQuery.error as Error).message}`);
    }
  }, [dictionaryQuery.isError, dictionaryQuery.error, processKey.length, message]);

  useEffect(() => {
    if (activeKey.length > 0 && declaredQuery.isError) {
      message.error(
        `读取流程定义里声明的选项失败：${(declaredQuery.error as Error).message}`
        + '（流程未部署 / key 拼错时就是这样；字典仍可手工维护）',
      );
    }
  }, [declaredQuery.isError, declaredQuery.error, activeKey.length, message]);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['platform', 'dictionary', activeKey] });
    void queryClient.invalidateQueries({ queryKey: ['platform', 'declared', activeKey] });
  };

  /** 所有声明过的选项（去重），用于「新增映射」的下拉。 */
  const declaredOptions = useMemo(() => {
    const seen = new Map<string, string>();

    for (const node of declared?.nodes ?? []) {
      for (const option of node.options) {
        if (!seen.has(option.optionId)) {
          seen.set(option.optionId, option.name ?? option.optionId);
        }
      }
    }

    return [...seen.entries()].map(([value, label]) => ({ value, label }));
  }, [declared]);

  const declaredNodes = useMemo(
    () => (declared?.nodes ?? []).map((node) => ({
      value: node.activityId,
      label: node.activityName ? `${node.activityName}（${node.activityId}）` : node.activityId,
    })),
    [declared],
  );

  const openCreate = (optionId?: string, nodeId?: string | null) => {
    form.resetFields();
    form.setFieldsValue({
      optionId: optionId ?? '',
      nodeId: nodeId ?? null,
      semantic: 'APPROVE',
      requiresComment: false,
      requiresTargetNode: false,
    });
    setEditor({ open: true, editing: null, optionId, nodeId });
  };

  const openEdit = (record: ApprovalDictionaryEntryView) => {
    form.setFieldsValue({
      optionId: record.optionId,
      nodeId: record.nodeId,
      semantic: record.semantic,
      label: record.label,
      requiresComment: record.requiresComment,
      requiresTargetNode: record.requiresTargetNode,
      allowedTargetNodes: record.allowedTargetNodes,
    });
    setEditor({ open: true, editing: record });
  };

  const handleSave = async () => {
    const values = await form.validateFields();

    if (values.optionId.trim().length === 0) {
      message.error('optionId 不能为空');

      return;
    }

    setSaving(true);

    try {
      await semanticsApi.upsert(activeKey, values.optionId.trim(), {
        nodeId: values.nodeId ?? null,
        semantic: values.semantic,
        label: values.label ?? null,
        requiresComment: values.requiresComment ?? false,
        requiresTargetNode: values.requiresTargetNode ?? false,
        allowedTargetNodes: values.allowedTargetNodes ?? null,
      });

      message.success('已保存映射（下一条审批立刻按新语义解释）');
      setEditor({ open: false, editing: null });
      refresh();
    } catch (error) {
      message.error(`保存失败：${(error as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (record: ApprovalDictionaryEntryView) => {
    try {
      await semanticsApi.remove(activeKey, record.optionId, record.nodeId);
      message.success('已删除映射（该选项回到「流程自定义」语义）');
      refresh();
    } catch (error) {
      message.error(`删除失败：${(error as Error).message}`);
    }
  };

  const columns: ColumnsType<ApprovalDictionaryEntryView> = [
    {
      title: '选项',
      key: 'option',
      width: 200,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text code>{record.optionId}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.label ?? '（未设显示名）'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '作用范围',
      dataIndex: 'nodeId',
      key: 'nodeId',
      width: 160,
      render: (nodeId: string | null) =>
        nodeId === null ? <Tag color="blue">流程级默认</Tag> : <Tag>{nodeId}</Tag>,
    },
    {
      title: '语义',
      dataIndex: 'semantic',
      key: 'semantic',
      width: 200,
      render: (semantic: string, record) => (
        <Space direction="vertical" size={0}>
          <Tag color={SEMANTIC_COLORS[semantic] ?? 'default'}>{semanticLabel(semantic)}</Tag>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {outcomeLabel(record.outcomeClass)}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '附加要求',
      key: 'requirements',
      width: 200,
      render: (_, record) => (
        <Space size={[0, 4]} wrap>
          {record.requiresComment ? <Tag color="volcano">必填意见</Tag> : null}
          {record.requiresTargetNode ? <Tag color="volcano">必选目标环节</Tag> : null}
          {record.allowedTargetNodes.length > 0 ? (
            <Tooltip title={record.allowedTargetNodes.join(' / ')}>
              <Tag>限定 {record.allowedTargetNodes.length} 个目标</Tag>
            </Tooltip>
          ) : null}
          {!record.requiresComment && !record.requiresTargetNode && record.allowedTargetNodes.length === 0 ? (
            <Typography.Text type="secondary">—</Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: '更新时间',
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 170,
      render: (updatedAt: string) => new Date(updatedAt).toLocaleString(),
    },
    {
      title: '操作',
      key: 'actions',
      width: 120,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} />
          <Popconfirm
            title={`删除「${record.optionId}」的映射？`}
            description="删除后该选项按「流程自定义」处理，统计口径回到中性。"
            okText="删除"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void handleDelete(record)}
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message="审批语义字典：把流程私有的 optionId 翻译成平台稳定的语义"
        description="调用方只认 semantic 与 outcomeClass，流程新增「转财务复核」这类选项不会破坏集成代码。字典按「租户 + 流程」生效；环节级映射优先于流程级默认。"
      />

      <Space wrap>
        <Input.Search
          allowClear
          placeholder="流程 key，如 leave_process"
          style={{ width: 320 }}
          enterButton="加载"
          value={processKey}
          onChange={(event) => setProcessKey(event.target.value)}
          onSearch={(value) => {
            const trimmed = value.trim();
            setActiveKey(trimmed);
            setProcessKey(trimmed);
          }}
        />
        <Button icon={<ReloadOutlined />} disabled={activeKey.length === 0} onClick={refresh}>
          刷新
        </Button>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          disabled={activeKey.length === 0}
          onClick={() => openCreate()}
        >
          新增映射
        </Button>
        {declared ? (
          <Typography.Text type="secondary">
            当前生效定义 v{declared.definitionVersion}
            {declared.nodes.length === 0 ? '（该流程没有声明审批选项）' : ` · ${declared.nodes.length} 个环节声明了选项`}
          </Typography.Text>
        ) : null}
      </Space>

      {activeKey.length === 0 ? (
        <Empty description="输入流程 key 后加载该流程的审批口径" />
      ) : (
        <Space direction="vertical" size="middle" style={{ width: '100%' }}>
          {declared && declared.nodes.length > 0 ? (
            <Space direction="vertical" size={6} style={{ width: '100%' }}>
              <Typography.Text strong>定义里声明的选项（点「配置」直接补语义）</Typography.Text>
              {declared.nodes.map((node) => (
                <Space key={node.activityId} wrap size={6}>
                  <Tag color="geekblue">{node.activityName ?? node.activityId}</Tag>
                  {node.options.map((option) => {
                    const mapped = entries.some(
                      (entry) => entry.optionId === option.optionId && entry.nodeId === null,
                    );

                    return (
                      <Tag
                        key={option.optionId}
                        color={mapped ? 'success' : 'default'}
                        style={{ cursor: 'pointer' }}
                        onClick={() => openCreate(option.optionId, null)}
                      >
                        {option.name ?? option.optionId}
                        {mapped ? ' ✓' : ''}
                      </Tag>
                    );
                  })}
                </Space>
              ))}
            </Space>
          ) : null}

          <Table<ApprovalDictionaryEntryView>
            rowKey={(record) => `${record.nodeId ?? '*'}#${record.optionId}`}
            columns={columns}
            dataSource={entries}
            loading={dictionaryQuery.isFetching}
            pagination={false}
            locale={{ emptyText: '还没有配置：未配置的选项按「流程自定义」处理' }}
          />
        </Space>
      )}

      <Modal
        title={editor.editing === null ? '新增 / 覆盖映射' : `编辑「${editor.editing.optionId}」`}
        open={editor.open}
        confirmLoading={saving}
        onOk={() => void handleSave()}
        onCancel={() => setEditor({ open: false, editing: null })}
        okText="保存"
        cancelText="取消"
        width={620}
        destroyOnClose
      >
        <Form form={form} layout="vertical">
          <Form.Item
            name="optionId"
            label="选项 id（BPMN 里的 activiti:value/@id）"
            rules={[{ required: true, whitespace: true, message: '请选择或填写 optionId' }]}
            extra="优先从定义声明的选项里选；手填也可以（历史数据里可能出现已删除的选项）。
              写了声明里没有的 id 不会报错，但那行映射永远不会生效。"
          >
            <AutoComplete
              options={declaredOptions}
              disabled={editor.editing !== null}
              placeholder={declaredOptions.length > 0 ? '从定义声明的选项里选' : '该流程没有声明选项，可手工填写'}
              filterOption={(input, option) =>
                (option?.label ?? '').toLowerCase().includes(input.toLowerCase())
                || (option?.value ?? '').toLowerCase().includes(input.toLowerCase())}
            />
          </Form.Item>
          <Form.Item
            name="nodeId"
            label="作用范围"
            extra="留空 = 流程级默认；选具体环节 = 只对该环节生效（优先级更高）。"
          >
            <Select
              allowClear
              placeholder="流程级默认"
              options={declaredNodes}
              disabled={editor.editing !== null}
            />
          </Form.Item>
          <Form.Item
            name="semantic"
            label="语义"
            rules={[{ required: true, message: '请选择语义' }]}
            extra="统计口径（正向 / 负向 / 中性）由语义自动推导，不需要单独选。"
          >
            <Select options={APPROVAL_SEMANTICS.map((item) => ({ value: item.value, label: item.label }))} />
          </Form.Item>
          <Form.Item name="label" label="显示名（可选）">
            <Input placeholder="如 转财务复核" />
          </Form.Item>
          <Space size="large">
            <Form.Item name="requiresComment" label="必填审批意见" valuePropName="checked">
              <Switch />
            </Form.Item>
            <Form.Item name="requiresTargetNode" label="必选目标环节" valuePropName="checked">
              <Switch />
            </Form.Item>
          </Space>
          <Form.Item
            name="allowedTargetNodes"
            label="允许退回 / 跳转的目标环节（可选）"
            extra="留空 = 不限制；填了就只允许这些 activityId。"
          >
            <Select mode="tags" tokenSeparators={[',', ' ']} placeholder="如 task_leader_approve" />
          </Form.Item>
        </Form>
      </Modal>
    </Space>
  );
}


