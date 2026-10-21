import { FileImageOutlined, HistoryOutlined } from '@ant-design/icons';
import { App, Button, Drawer, InputNumber, Modal, Popconfirm, Space, Spin, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

import { modelsApi } from '../api';
import type { BpmnModelRevisionSummary } from '../api/types';
import BpmnDiagram from './BpmnDiagram';
import { formatTime } from '../utils';

interface Props {
  /** 模型 id；null 表示抽屉关闭。 */
  modelId: string | null;
  modelName?: string | null;
  open: boolean;
  onClose: () => void;
}

const DEFAULT_KEEP_LATEST = 10;

/**
 * 模型版本历史抽屉：查看 / 回滚 / 清理旧版本。
 * <para>
 * 回滚把历史版本内容作为新版本写入（历史只增不删）；清理只保留最近 N 版，
 * 最新版本永远保留。两者都由后端写管理动作审计（model.history_prune 等）。
 * </para>
 */
export default function ModelHistoryDrawer({ modelId, modelName, open, onClose }: Props) {
  const { message } = App.useApp();
  const queryClient = useQueryClient();

  const [keepLatest, setKeepLatest] = useState<number>(DEFAULT_KEEP_LATEST);
  const [rollingBack, setRollingBack] = useState<string | null>(null);
  const [pruning, setPruning] = useState(false);
  /** 正在查看流程图的版本（null 表示弹窗关闭）。 */
  const [viewing, setViewing] = useState<BpmnModelRevisionSummary | null>(null);

  const historyQuery = useQuery({
    queryKey: ['admin', 'model-history', modelId],
    queryFn: () => modelsApi.history(modelId as string),
    enabled: open && modelId !== null,
  });

  /** 查看流程图：拉取该版本的 XML 快照（弹窗打开时才查）。 */
  const revisionQuery = useQuery({
    queryKey: ['admin', 'model-history', modelId, viewing?.id],
    queryFn: () => modelsApi.revision(modelId as string, (viewing as BpmnModelRevisionSummary).id),
    enabled: viewing !== null && modelId !== null,
  });

  const revisions = historyQuery.data ?? [];

  /** 失效模型列表与历史缓存（回滚 / 清理后自动刷新本抽屉与列表页）。 */
  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'models'] });
    void queryClient.invalidateQueries({ queryKey: ['admin', 'model-history', modelId] });
  };

  const handleRollback = async (revision: BpmnModelRevisionSummary) => {
    if (modelId === null) {
      return;
    }

    setRollingBack(revision.id);

    try {
      await modelsApi.rollback(modelId, revision.id);
      message.success(`已回滚到版本 v${revision.revision}（写入为最新版本）`);
      invalidate();
    } catch (error) {
      message.error(`回滚失败：${(error as Error).message}`);
    } finally {
      setRollingBack(null);
    }
  };

  const handlePrune = async () => {
    if (modelId === null) {
      return;
    }

    setPruning(true);

    try {
      const result = await modelsApi.pruneHistory(modelId, keepLatest);
      message.success(
        result.removed > 0
          ? `已清理 ${result.removed} 个旧版本（保留最近 ${result.keepLatest} 版）`
          : `没有可清理的旧版本（历史共 ${revisions.length} 版，保留最近 ${result.keepLatest} 版）`,
      );
      invalidate();
    } catch (error) {
      message.error(`清理失败：${(error as Error).message}`);
    } finally {
      setPruning(false);
    }
  };

  const columns: ColumnsType<BpmnModelRevisionSummary> = [
    {
      title: '版本',
      dataIndex: 'revision',
      key: 'revision',
      width: 80,
      render: (revision: number) => <Tag color="blue">v{revision}</Tag>,
    },
    {
      title: '动作',
      dataIndex: 'action',
      key: 'action',
      width: 100,
      render: (action: string) =>
        action === 'rollback' ? <Tag color="orange">回滚</Tag> : action === 'publish' ? <Tag color="green">部署</Tag> : <Tag>保存</Tag>,
    },
    { title: '流程名称', dataIndex: 'name', key: 'name', ellipsis: true },
    { title: '操作人', dataIndex: 'operator', key: 'operator', width: 130, render: (v?: string | null) => v ?? '—' },
    {
      title: '时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 180,
      render: (v: string) => formatTime(v),
    },
    {
      title: '操作',
      key: 'actions',
      width: 150,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<FileImageOutlined />} onClick={() => setViewing(record)}>
            流程图
          </Button>
          <Popconfirm
            title={`回滚到 v${record.revision}？`}
            description="该版本内容将作为新版本写入，历史保留。"
            okText="回滚"
            cancelText="取消"
            onConfirm={() => void handleRollback(record)}
          >
            <Button size="small" loading={rollingBack === record.id}>
              回滚
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const title = modelName ? `版本历史 · ${modelName}` : '版本历史';

  return (
    <Drawer title={title} open={open} width={760} onClose={onClose} destroyOnClose>
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
          <Space wrap>
            <Typography.Text type="secondary">保留最近</Typography.Text>
            <InputNumber
              min={0}
              max={1000}
              precision={0}
              value={keepLatest}
              onChange={(value) => setKeepLatest(value ?? DEFAULT_KEEP_LATEST)}
              style={{ width: 90 }}
            />
            <Typography.Text type="secondary">个版本，清理更早的快照</Typography.Text>
          </Space>
          <Popconfirm
            title="确认清理旧版本？"
            description={`将删除较早的快照、只保留最近 ${keepLatest} 版（最新版本永远保留），不可恢复。`}
            okText="清理"
            okButtonProps={{ danger: true }}
            cancelText="取消"
            onConfirm={() => void handlePrune()}
          >
            <Button danger icon={<HistoryOutlined />} loading={pruning}>
              清理旧版本
            </Button>
          </Popconfirm>
        </Space>

        <Table<BpmnModelRevisionSummary>
          rowKey="id"
          size="small"
          loading={historyQuery.isPending || historyQuery.isFetching}
          columns={columns}
          dataSource={revisions}
          pagination={false}
        />
      </Space>

      <Modal
        title={`流程图 · v${viewing?.revision ?? ''} ${viewing?.name ?? ''}`}
        open={viewing !== null}
        footer={null}
        width={900}
        onCancel={() => setViewing(null)}
        destroyOnClose
      >
        {revisionQuery.isPending ? (
          <Spin style={{ display: 'block', margin: '80px auto' }} />
        ) : revisionQuery.isError ? (
          <Typography.Text type="danger">
            加载失败：{(revisionQuery.error as Error).message}
          </Typography.Text>
        ) : revisionQuery.data?.svg ? (
          // 部署（publish）版本带部署时生成的流程图图片：直接展示快照，保证与部署当时一致
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 8, padding: 12, overflow: 'auto' }}>
            <img
              src={`data:image/svg+xml;utf8,${encodeURIComponent(revisionQuery.data.svg)}`}
              alt={`v${revisionQuery.data.revision} 流程图`}
              style={{ width: '100%' }}
            />
          </div>
        ) : (
          <BpmnDiagram xml={revisionQuery.data?.bpmnXml ?? ''} activities={[]} height={520} />
        )}
      </Modal>
    </Drawer>
  );
}
