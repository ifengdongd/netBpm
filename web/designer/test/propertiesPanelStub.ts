/**
 * `@bpmn-io/properties-panel` 的测试替身。
 *
 * 该包是浏览器运行时（依赖 preact/DOM），在 Node 里 import 会崩；而我们要测的是**自己的**
 * 逻辑（条目定义、属性映射、空值处理、校验），不需要它渲染。因此测试用 esbuild 的
 * `--alias` 把包名指向本文件：条目组件退化成可识别的标记，`Group` 退化成恒等函数，
 * 于是可以直接对 groups/entries 做断言。
 *
 * ⚠️ 这里的导出名必须与真实包一致（否则打包期就会报"找不到导出"，这本身就是一道防线）。
 */

export const TextFieldEntry = { component: 'TextFieldEntry' };
export const TextAreaEntry = { component: 'TextAreaEntry' };
export const SelectEntry = { component: 'SelectEntry' };
export const CheckboxEntry = { component: 'CheckboxEntry' };

export const isTextFieldEntryEdited = (): boolean => true;
export const isTextAreaEntryEdited = (): boolean => true;
export const isSelectEntryEdited = (): boolean => true;
export const isCheckboxEntryEdited = (): boolean => true;

export interface StubGroup {
  id: string;
  label: string;
  entries: StubEntry[];
}

export interface StubEntry {
  id: string;
  component: unknown;
  element: unknown;
  getValue?: (element: unknown) => unknown;
  setValue?: (element: unknown, value: unknown) => void;
  validate?: (value: unknown) => string | null;
  options?: { label: string; value: string }[];
  [key: string]: unknown;
}

/** 真实实现返回内部结构；测试只需要能取到 entries。 */
export function Group(options: { id: string; label: string; entries: unknown[] }): StubGroup {
  return { id: options.id, label: options.label, entries: options.entries as StubEntry[] };
}
