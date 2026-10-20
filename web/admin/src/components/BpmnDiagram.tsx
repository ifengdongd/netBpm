import NavigatedViewer from 'bpmn-js/lib/NavigatedViewer';
import type { Canvas } from 'bpmn-js/lib/NavigatedViewer';
import 'bpmn-js/dist/assets/diagram-js.css';
import 'bpmn-js/dist/assets/bpmn-js.css';
import 'bpmn-js/dist/assets/bpmn-font/css/bpmn-embedded.css';
import { Empty } from 'antd';
import { useEffect, useRef } from 'react';

import type { HistoricActivity } from '../api/types';

import './BpmnDiagram.css';

interface Props {
  /** BPMN XML（含 BPMNDI）；空串表示无图可渲染。 */
  xml: string;
  /** 活动高亮序列：isActive=true 标当前节点，其余标已完成。 */
  activities: HistoricActivity[];
  height?: number;
}

/**
 * 只读流程图查看器（bpmn-js NavigatedViewer）。
 * 用于实例详情：渲染部署时的 BPMN XML，并按历史活动给已完成 / 当前节点着色。
 */
export default function BpmnDiagram({ xml, activities, height = 420 }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const viewerRef = useRef<NavigatedViewer | null>(null);

  // 初始化 viewer + 监听容器尺寸（Tab 从隐藏切到可见时需要 resized）
  useEffect(() => {
    const container = containerRef.current;

    if (container === null) {
      return;
    }

    const viewer = new NavigatedViewer({ container });
    viewerRef.current = viewer;

    const observer = new ResizeObserver(() => {
      const current = viewerRef.current;

      if (current === null) {
        return;
      }

      try {
        const canvas = current.get<Canvas>('canvas');
        canvas.resized();

        // 尺寸真正可用时再自适应（隐藏 Tab 里 clientWidth 为 0）
        if (container.clientWidth > 0 && container.clientHeight > 0) {
          canvas.zoom('fit-viewport');
        }
      } catch {
        // 容器尺寸为 0 时忽略
      }
    });

    observer.observe(container);

    return () => {
      observer.disconnect();
      viewer.destroy();
      viewerRef.current = null;
    };
  }, []);

  // 导入 XML 并着色
  useEffect(() => {
    const viewer = viewerRef.current;

    if (viewer === null || xml.length === 0) {
      return;
    }

    let disposed = false;

    void viewer
      .importXML(xml)
      .then(() => {
        if (disposed) {
          return;
        }

        const canvas = viewer.get<Canvas>('canvas');
        canvas.zoom('fit-viewport');

        for (const activity of activities) {
          try {
            canvas.addMarker(activity.activityId, activity.isActive ? 'netbpm-hl-current' : 'netbpm-hl-completed');
          } catch {
            // 活动节点可能不在当前 BPMN 图中（历史残留），忽略
          }
        }
      })
      .catch(() => {
        // XML 无法解析时保持空白，由上层展示空态
      });

    return () => {
      disposed = true;
    };
  }, [xml, activities]);

  // 容器常驻：空态用覆盖层展示，避免 xml 后到时 ref 不存在导致 viewer 无法初始化
  return (
    <div className="netbpm-diagram" ref={containerRef} style={{ height }}>
      {xml.length === 0 && (
        <div className="netbpm-diagram-empty">
          <Empty description="暂无流程图数据（历史可能未启用）" />
        </div>
      )}
    </div>
  );
}
