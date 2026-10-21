/**
 * 把 BPMN XML 渲染成 SVG 文本（bpmn-js NavigatedViewer，按需动态加载）。
 *
 * 用途：模型「部署」前生成当前流程图图片，随发布请求交给后端写入版本快照 ——
 * 版本历史里即可查看「部署当时」的图（部署时后端没有 DOM，画不了图）。
 *
 * 失败（XML 无法解析等）返回 null，由调用方决定是否降级为「不带图部署」。
 */
export async function renderDiagramSvg(xml: string): Promise<string | null> {
  if (xml.length === 0) {
    return null;
  }

  try {
    const { default: NavigatedViewer } = await import('bpmn-js/lib/NavigatedViewer');

    // 画布必须在真实尺寸的容器里渲染：detached / 零尺寸容器导出的 SVG 是空壳（10x10）。
    // 挂到屏外固定尺寸节点，导入后自适应缩放，再导出。
    const holder = document.createElement('div');
    holder.setAttribute(
      'style',
      'position:fixed;left:-10000px;top:0;width:1200px;height:800px;overflow:hidden;',
    );
    document.body.appendChild(holder);

    const viewer = new NavigatedViewer({ container: holder });

    try {
      await viewer.importXML(xml);

      const canvas = viewer.get<{ zoom: (arg?: string) => void }>('canvas');
      canvas.zoom('fit-viewport');

      // NavigatedViewer 的类型定义未导出 saveSVG（运行时由 bpmn-js 基类提供），这里按运行时契约断言
      const saver = viewer as unknown as { saveSVG(options?: object): Promise<{ svg: string }> };
      const { svg } = await saver.saveSVG({ format: true });
      return svg;
    } finally {
      viewer.destroy();
      holder.remove();
    }
  } catch {
    return null;
  }
}
