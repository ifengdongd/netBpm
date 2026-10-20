import { ArrowLeftOutlined } from '@ant-design/icons';
import { Button, Space, Typography } from 'antd';
import { useNavigate, useSearchParams } from 'react-router-dom';

/**
 * 内嵌设计器的全屏编辑页。设计器由同一宿主托管在 /designer/index.html，
 * 通过 ?modelId= 打开已有模型、?action=new 新建模型。
 */
export default function ModelEditorPage() {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();

  const modelId = searchParams.get('modelId');
  const action = searchParams.get('action');

  const designerParams = new URLSearchParams();

  if (modelId !== null) {
    designerParams.set('modelId', modelId);
  } else if (action !== null) {
    designerParams.set('action', action);
  }

  const designerSrc = `/designer/index.html?${designerParams.toString()}`;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: 'calc(100vh - 112px)' }}>
      <Space style={{ marginBottom: 12 }}>
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/models')}>
          返回列表
        </Button>
        <Typography.Text type="secondary">
          {modelId !== null ? '编辑流程模型（下方为内嵌设计器）' : '新建流程模型（下方为内嵌设计器）'}
        </Typography.Text>
      </Space>
      <iframe
        src={designerSrc}
        title="NetBpm 流程设计器"
        style={{ flex: 1, border: '1px solid #f0f0f0', borderRadius: 8, background: '#fff' }}
      />
    </div>
  );
}
