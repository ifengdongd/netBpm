import { DeleteOutlined, EditOutlined, HolderOutlined, PlusOutlined } from '@ant-design/icons';
import {
  closestCenter,
  DndContext,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core';
import { restrictToVerticalAxis } from '@dnd-kit/modifiers';
import { arrayMove, SortableContext, useSortable, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { App, Button, Form, Input, InputNumber, Modal, Popconfirm, Space, Switch, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { CSSProperties, HTMLAttributes } from 'react';

import { processTypesApi } from '../api';
import type { ProcessType, SaveProcessTypeRequest } from '../api/types';

const PROCESS_TYPES_KEY = ['admin', 'process-types'] as const;

interface EditorState {
  open: boolean;
  editing: ProcessType | null;
}

export default function ProcessTypesPage() {
  const { message } = App.useApp();
  const queryClient = useQueryClient();
  const [form] = Form.useForm<SaveProcessTypeRequest>();

  const [saving, setSaving] = useState(false);
  const [editor, setEditor] = useState<EditorState>({ open: false, editing: null });
  const [reordering, setReordering] = useState(false);
  const [keyword, setKeyword] = useState('');

  const typesQuery = useQuery({
    queryKey: PROCESS_TYPES_KEY,
    queryFn: () => processTypesApi.list(),
    // 流程类型注册表低频变化（写操作后有 invalidate），放宽新鲜度
    staleTime: 60_000,
  });

  // useMemo 稳定空数组引用，避免下游 useMemo 依赖每次渲染变化
  const items = useMemo(() => typesQuery.data ?? [], [typesQuery.data]);
  const loading = typesQuery.isFetching;

  useEffect(() => {
    if (typesQuery.isError) {
      message.error(`加载流程类型失败：${(typesQuery.error as Error).message}`);
    }
  }, [typesQuery.isError, typesQuery.error, message]);

  const hasKeyword = keyword.trim().length > 0;

  const filteredItems = useMemo(() => {
    const query = keyword.trim().toLowerCase();

    if (query.length === 0) {
      return items;
    }

    return items.filter((item) =>
      [item.name, item.describe, item.viewUrl, item.approveUrl].some((field) =>
        field.toLowerCase().includes(query),
      ),
    );
  }, [items, keyword]);

  // 拖拽需移动 4px 才激活，避免点击「编辑 / 删除」按钮时误触发
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));

  /** 拖拽结束后按新顺序重排 sortOrder 并逐个持久化（流程类型数量少，串行更新保证一致）。 */
  const persistOrder = async (ordered: ProcessType[]) => {
    setReordering(true);

    try {
      for (const item of ordered) {
        await processTypesApi.update(item.id, {
          name: item.name,
          describe: item.describe,
          viewUrl: item.viewUrl,
          approveUrl: item.approveUrl,
          sortOrder: item.sortOrder,
          enabled: item.enabled,
        });
      }

      message.success('排序已保存');
    } catch (error) {
      message.error(`保存排序失败：${(error as Error).message}`);
      void queryClient.invalidateQueries({ queryKey: PROCESS_TYPES_KEY });
    } finally {
      setReordering(false);
    }
  };

  const handleDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;

    // 搜索状态下禁止拖拽：过滤后的顺序不代表全量顺序，重排会打乱 sort_order
    if (hasKeyword || over === null || active.id === over.id) {
      return;
    }

    const oldIndex = items.findIndex((item) => item.id === String(active.id));
    const newIndex = items.findIndex((item) => item.id === String(over.id));

    if (oldIndex < 0 || newIndex < 0) {
      return;
    }

    // 重排后按 0..n 重新分配 sortOrder（后端按 sort_order asc, id asc 返回）
    const reordered = arrayMove(items, oldIndex, newIndex).map((item, index) => ({ ...item, sortOrder: index }));

    // 乐观更新缓存，随后逐行持久化；失败时 refetch 回滚为服务端顺序
    queryClient.setQueryData(PROCESS_TYPES_KEY, reordered);
    void persistOrder(reordered);
  };

  const openCreate = () => {
    form.resetFields();
    form.setFieldsValue({ sortOrder: 0, enabled: true });
    setEditor({ open: true, editing: null });
  };

  const openEdit = (record: ProcessType) => {
    form.setFieldsValue({
      name: record.name,
      describe: record.describe,
      viewUrl: record.viewUrl,
      approveUrl: record.approveUrl,
      sortOrder: record.sortOrder,
      enabled: record.enabled,
    });
    setEditor({ open: true, editing: record });
  };

  const handleSave = async () => {
    const values = await form.validateFields();
    setSaving(true);

    try {
      if (editor.editing === null) {
        await processTypesApi.create(values);
        message.success('已创建流程类型');
      } else {
        await processTypesApi.update(editor.editing.id, values);
        message.success('已更新流程类型');
      }

      setEditor({ open: false, editing: null });
      void queryClient.invalidateQueries({ queryKey: PROCESS_TYPES_KEY });
    } catch (error) {
      message.error(`保存失败：${(error as Error).message}`);
    } finally {
      setSaving(false);
    }
  };

  const handleDelete = async (id: string, name: string) => {
    try {
      await processTypesApi.remove(id);
      message.success(`已删除：${name}`);
      void queryClient.invalidateQueries({ queryKey: PROCESS_TYPES_KEY });
    } catch (error) {
      message.error(`删除失败：${(error as Error).message}`);
    }
  };

  const columns: ColumnsType<ProcessType> = [
    {
      key: 'drag',
      width: 44,
      title: '',
      render: () => <HolderOutlined style={{ color: '#999', cursor: 'move' }} />,
    },
    {
      title: '流程名称',
      dataIndex: 'name',
      key: 'name',
      render: (name: string) => <Typography.Text strong>{name}</Typography.Text>,
    },
    { title: '描述', dataIndex: 'describe', key: 'describe', ellipsis: true },
    {
      title: '查看页 / 审批页',
      key: 'urls',
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.viewUrl || '—'}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.approveUrl || '—'}
          </Typography.Text>
        </Space>
      ),
    },
    { title: '排序', dataIndex: 'sortOrder', key: 'sortOrder', width: 80 },
    {
      title: '状态',
      dataIndex: 'enabled',
      key: 'enabled',
      width: 90,
      render: (enabled: boolean) => (enabled ? <Tag color="success">启用</Tag> : <Tag>停用</Tag>),
    },
    {
      title: '操作',
      key: 'actions',
      width: 140,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} />
          <Popconfirm
            title={`删除流程类型「${record.name}」？`}
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
            placeholder="搜索名称 / 描述 / 页面地址"
            style={{ width: 280 }}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <Typography.Text type="secondary">
            {hasKeyword
              ? `匹配 ${filteredItems.length} / ${items.length} 个流程类型（搜索时不可拖拽）`
              : '拖动行可调整顺序，保存后按 sort_order 生效'}
          </Typography.Text>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>
          新建流程类型
        </Button>
      </Space>
      <DndContext
        sensors={sensors}
        collisionDetection={closestCenter}
        modifiers={[restrictToVerticalAxis]}
        onDragEnd={handleDragEnd}
      >
        <SortableContext items={filteredItems.map((item) => item.id)} strategy={verticalListSortingStrategy}>
          <DragDisabledContext.Provider value={hasKeyword}>
            <Table<ProcessType>
              rowKey="id"
              columns={columns}
              dataSource={filteredItems}
              loading={loading || reordering}
              components={{ body: { row: SortableRow } }}
              pagination={false}
            />
          </DragDisabledContext.Provider>
        </SortableContext>
      </DndContext>

      <Modal
        title={editor.editing === null ? '新建流程类型' : `编辑流程类型「${editor.editing.name}」`}
        open={editor.open}
        confirmLoading={saving}
        onOk={() => void handleSave()}
        onCancel={() => setEditor({ open: false, editing: null })}
        okText="保存"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={form} layout="vertical">
          <Form.Item
            name="name"
            label="流程名称（= 流程 key）"
            rules={[{ required: true, whitespace: true, message: '请输入流程名称' }]}
          >
            <Input placeholder="如 order_process" />
          </Form.Item>
          <Form.Item name="describe" label="描述">
            <Input placeholder="流程用途说明" />
          </Form.Item>
          <Form.Item name="viewUrl" label="业务单查看页地址（支持 {businessId}/{processInstanceId} 占位符）">
            <Input placeholder="/bill.html?process=order_process&businessId={businessId}" />
          </Form.Item>
          <Form.Item name="approveUrl" label="流程审批页地址（支持 {taskId}/{processInstanceId} 占位符）">
            <Input placeholder="/approve.html?process=order_process&taskId={taskId}" />
          </Form.Item>
          <Space size="large">
            <Form.Item name="sortOrder" label="排序（小在前）">
              <InputNumber min={0} precision={0} />
            </Form.Item>
            <Form.Item name="enabled" label="是否启用" valuePropName="checked">
              <Switch />
            </Form.Item>
          </Space>
        </Form>
      </Modal>
    </Space>
  );
}

interface SortableRowProps extends HTMLAttributes<HTMLTableRowElement> {
  'data-row-key'?: string | number;
}

/** 搜索状态下禁用拖拽（避免只对过滤结果重排、打乱全量顺序）。 */
const DragDisabledContext = createContext(false);

/** antd Table 的可拖拽行：把行 id 交给 useSortable，整行作为拖拽把手。 */
function SortableRow(props: SortableRowProps) {
  const id = String(props['data-row-key'] ?? '');
  const disabled = useContext(DragDisabledContext);
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id, disabled });

  const style: CSSProperties = {
    ...props.style,
    transform: CSS.Translate.toString(transform),
    transition,
    cursor: disabled ? 'default' : 'move',
    ...(isDragging ? { position: 'relative', zIndex: 9999 } : {}),
  };

  return <tr {...props} ref={setNodeRef} style={style} {...attributes} {...listeners} />;
}
