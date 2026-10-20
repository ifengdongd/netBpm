/**
 * bpmn-js 18 不随包提供 .d.ts，这里只声明只读 Viewer 用到的成员
 * （与 web/designer/src/types/vendor.d.ts 的思路一致，避免整包 any 化）。
 */
declare module 'bpmn-js/lib/NavigatedViewer' {
  export interface ImportResult {
    warnings: unknown[];
  }

  export interface Canvas {
    /** 给元素加高亮标记（对应 CSS 类名）。 */
    addMarker(elementId: string, marker: string): void;
    /** 清除标记。 */
    removeMarker(elementId: string, marker: string): void;
    /** 缩放；'fit-viewport' 表示自适应容器。 */
    zoom(level: 'fit-viewport' | number): void;
    /** 容器尺寸变化后重新计算。 */
    resized(): void;
  }

  export default class NavigatedViewer {
    constructor(options: { container: HTMLElement | string });
    importXML(xml: string): Promise<ImportResult>;
    get<T>(service: 'canvas' | string): T;
    destroy(): void;
  }
}
