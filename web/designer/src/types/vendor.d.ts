/**
 * 第三方模块的最小类型声明。
 *
 * 背景：`bpmn-js` / `bpmn-js-properties-panel` / `@bpmn-io/properties-panel` 三个包
 * 都不带 `.d.ts`，而 tsconfig 开了 `strict` —— 缺声明时 `npm run build` 的 `tsc --noEmit`
 * 直接失败（TS7016）。这里只声明**我们用到的成员**（而不是整包 any 化），
 * 这样属性名/方法名写错仍会在编译期暴露。
 */

declare module 'bpmn-js/lib/Modeler' {
  export interface ImportResult {
    warnings: unknown[];
  }

  export interface SaveResult {
    xml?: string;
  }

  export default class BpmnModeler {
    constructor(options: Record<string, unknown>);
    importXML(xml: string): Promise<ImportResult>;
    saveXML(options?: { format?: boolean }): Promise<SaveResult>;
    get<T>(service: string): T;
    on(event: string, callback: (event: unknown) => void): void;
    destroy(): void;
  }
}

declare module 'bpmn-js-properties-panel' {
  /** properties-panel 的渲染宿主（提供 #properties 容器与面板布局）。 */
  export const BpmnPropertiesPanelModule: Record<string, unknown>;

  /** 官方默认属性组（General/Documentation/…）。本设计器**不装载**它：见 propertiesProvider.ts。 */
  export const BpmnPropertiesProviderModule: Record<string, unknown>;

  export function useService<T = unknown>(name: string): T;
}

declare module '@bpmn-io/properties-panel' {
  /** 条目组件（由 properties-panel 内部渲染，我们只负责传 props）。 */
  export const TextFieldEntry: unknown;
  export const TextAreaEntry: unknown;
  export const SelectEntry: unknown;
  export const CheckboxEntry: unknown;

  export interface GroupOptions {
    id: string;
    label: string;
    entries: unknown[];
  }

  export function Group(options: GroupOptions): unknown;

  /** 各条目类型的 `isEdited` 判定函数。 */
  export const isTextFieldEntryEdited: (entry: unknown) => boolean;
  export const isTextAreaEntryEdited: (entry: unknown) => boolean;
  export const isSelectEntryEdited: (entry: unknown) => boolean;
  export const isCheckboxEntryEdited: (entry: unknown) => boolean;
}
