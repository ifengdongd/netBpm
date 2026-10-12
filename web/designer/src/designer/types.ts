/**
 * 设计器内部使用的结构类型。
 *
 * 刻意**不**照搬 bpmn-js 的类型（上游没有发布 .d.ts）：这里只描述我们真正依赖的形状，
 * 并把 bpmn-js 的对象当作"带 $type 的松散属性包"处理 —— 属性名与后端契约的对应关系
 * 由 `propertiesProvider.ts` 一处集中维护。
 */

/** moddle 元素（`bpmn:UserTask`、`bpmn:Process` …）。属性按 XML 名（去掉命名空间前缀）。 */
export interface ModdleElement {
  $type: string;
  /** 扩展属性挂在 `$attrs`（如 `xsi:type`），一般用不到。 */
  $attrs?: Record<string, unknown>;
  id?: string;
  name?: string;
  incoming?: ModdleElement[];
  outgoing?: ModdleElement[];
  flowElements?: ModdleElement[];
  eventDefinitions?: ModdleElement[];
  extensionElements?: {
    values?: ModdleElement[];
  };
  conditionExpression?: ModdleElement;
  body?: string;
  [key: string]: unknown;
}

/** 画布上的图形/连线（properties-panel 传给 provider 的 element）。 */
export interface DiagramElement {
  id: string;
  type: string;
  businessObject: ModdleElement;
}

/** properties-panel 的条目定义（`component` + 组件的 props）。 */
export interface PropertyEntry {
  id: string;
  component: unknown;
  element: DiagramElement;
  [key: string]: unknown;
}

/** bpmn-js `modeling` 服务（只用 updateProperties）。 */
export interface ModelingService {
  updateProperties(element: unknown, properties: Record<string, unknown>): void;
}

/** bpmn-js `moddle` 服务（用于创建 conditionExpression 这类子元素）。 */
export interface ModdleService {
  create(type: string, properties?: Record<string, unknown>): ModdleElement;
}

/** bpmn-js `elementRegistry` 服务。 */
export interface ElementRegistryService {
  getAll(): DiagramElement[];
  get(id: string): DiagramElement | undefined;
}

/** bpmn-js `selection` / `canvas` 服务（点击校验问题后定位元素）。 */
export interface SelectionService {
  select(element: unknown): void;
}

export interface CanvasService {
  scrollToElement(element: unknown): void;
}

/** properties-panel 服务（注册属性组的入口）。 */
export interface PropertiesPanelService {
  registerProvider(priority: number, provider: unknown): void;
}

/** 读取条目的属性来源（`element.businessObject`）。 */
export function businessObject(element: DiagramElement | ModdleElement): ModdleElement {
  return (element as DiagramElement).businessObject ?? (element as ModdleElement);
}
