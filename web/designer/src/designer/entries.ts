/**
 * 属性面板的条目工厂（开发计划 §6.2 第 3 项）。
 *
 * 设计要点：
 *  1. 条目一律**声明式**构造（`component` + props），不在设计器里写 Preact 组件 ——
 *     bpmn-js-properties-panel 的 `useService` 只能在组件内调用，而 provider 本身可以直接
 *     注入 `modeling` / `moddle`，于是所有写入都集中在 `context.modeling.updateProperties`；
 *  2. 空字符串 → `undefined`：避免在 XML 里写出 `activiti:assignee=""`（往返时后端会读到空属性
 *     而不是"未设置"，属于静默偏差）；
 *  3. 校验函数与 `validation.ts` 共用，保证"字段级提示"与"整体校验"口径一致。
 */
import {
  CheckboxEntry,
  SelectEntry,
  TextAreaEntry,
  TextFieldEntry,
  isCheckboxEntryEdited,
  isSelectEntryEdited,
  isTextAreaEntryEdited,
  isTextFieldEntryEdited,
} from '@bpmn-io/properties-panel';

import { businessObject, type DiagramElement, type ModelingService, type ModdleService, type ModdleElement, type PropertyEntry } from './types';

export interface EntryContext {
  modeling: ModelingService;
  moddle: ModdleService;
}

interface BaseOptions {
  id: string;
  property: string;
  element: DiagramElement;
  label: string;
  description?: string;
  placeholder?: string;
  validate?: (value: unknown) => string | null;
}

/** 把面板里的输入归一化成"属性值"：空白 ⇒ undefined（即删除该属性）。 */
export function normalizeText(value: unknown): string | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  const text = String(value).trim();

  return text.length === 0 ? undefined : text;
}

/** 读取文本属性（自定义属性可能是数字，统一转成字符串给输入框）。 */
export function readText(element: DiagramElement, property: string): string | undefined {
  const value = businessObject(element)[property];

  return value === undefined || value === null ? undefined : String(value);
}

/** 读取布尔属性。 */
export function readBoolean(element: DiagramElement, property: string): boolean {
  const value = businessObject(element)[property];

  return value === true || value === 'true';
}

/** 文本条目。 */
export function textEntry(options: BaseOptions, context: EntryContext): PropertyEntry {
  return {
    id: options.id,
    component: TextFieldEntry,
    element: options.element,
    label: options.label,
    description: options.description,
    placeholder: options.placeholder,
    isEdited: isTextFieldEntryEdited,
    validate: options.validate,
    getValue: (element: DiagramElement) => readText(element, options.property),
    setValue: (element: DiagramElement, value: unknown) =>
      context.modeling.updateProperties(element, { [options.property]: normalizeText(value) }),
  };
}

/** 多行文本条目（脚本正文、条件表达式）。 */
export function textAreaEntry(
  options: BaseOptions & { rows?: number },
  context: EntryContext,
): PropertyEntry {
  return {
    id: options.id,
    component: TextAreaEntry,
    element: options.element,
    label: options.label,
    description: options.description,
    placeholder: options.placeholder,
    isEdited: isTextAreaEntryEdited,
    validate: options.validate,
    getValue: (element: DiagramElement) => readText(element, options.property),
    setValue: (element: DiagramElement, value: unknown) =>
      context.modeling.updateProperties(element, { [options.property]: normalizeText(value) }),
  };
}

/** 布尔条目；`trueValue` 用于写出 Activiti 的 `'true'` 字符串形式（如 autoStoreVariables）。 */
export function checkboxEntry(
  options: Omit<BaseOptions, 'property'> & { property: string; trueValue?: string | boolean },
  context: EntryContext,
): PropertyEntry {
  const trueValue = options.trueValue ?? true;

  return {
    id: options.id,
    component: CheckboxEntry,
    element: options.element,
    label: options.label,
    description: options.description,
    isEdited: isCheckboxEntryEdited,
    getValue: (element: DiagramElement) => readBoolean(element, options.property),
    // 取消勾选 ⇒ undefined（删除属性），避免写出 autoStoreVariables="false"
    setValue: (element: DiagramElement, value: unknown) =>
      context.modeling.updateProperties(element, {
        [options.property]: value === true ? trueValue : undefined,
      }),
  };
}

export interface SelectOption {
  label: string;
  value: string;
}

/** 下拉条目。 */
export function selectEntry(
  options: Omit<BaseOptions, 'validate'> & {
    options: SelectOption[];
    /** 值为空时的落点（默认写 undefined，即删除属性）。 */
    emptyValue?: string;
  },
  context: EntryContext,
): PropertyEntry {
  return {
    id: options.id,
    component: SelectEntry,
    element: options.element,
    label: options.label,
    description: options.description,
    isEdited: isSelectEntryEdited,
    options: options.options,
    emptyValue: options.emptyValue ?? '',
    getValue: (element: DiagramElement) => readText(element, options.property) ?? '',
    setValue: (element: DiagramElement, value: unknown) => {
      const text = normalizeText(value);
      const fallback = options.emptyValue === '' ? undefined : options.emptyValue;

      context.modeling.updateProperties(element, {
        [options.property]: text === undefined ? fallback : text,
      });
    },
  };
}

/**
 * 默认流下拉：`default` 是元素自身的属性，取值是**出流 id**。
 * 只有"有出流"的节点才显示该项（没有出流时无从选择）。
 */
export function defaultFlowEntry(
  element: DiagramElement,
  label: string,
  context: EntryContext,
): PropertyEntry | null {
  const outgoing = businessObject(element).outgoing ?? [];

  if (outgoing.length === 0) {
    return null;
  }

  return selectEntry(
    {
      id: 'defaultFlow',
      property: 'default',
      element,
      label,
      description: '当所有出流的条件都不成立时走这条流（后端校验要求排他网关要么有默认流、要么至少一条无条件出流）',
      options: [
        { label: '（无默认流）', value: '' },
        ...outgoing.map((flow) => ({
          label: `${flow.id ?? '(未命名)'}${flow.name ? ` · ${flow.name}` : ''}`,
          value: flow.id ?? '',
        })),
      ],
    },
    context,
  );
}

/** 顺序流的条件表达式（子元素，需要 moddle.create）。 */
export function conditionEntry(
  element: DiagramElement,
  context: EntryContext,
): PropertyEntry {
  return {
    id: 'conditionExpression',
    component: TextAreaEntry,
    element,
    label: '条件表达式',
    description:
      "例如 ${approved}、${amount > 100 && status == 'ok'}、${order.items.size() > 0}；" +
      '留空表示无条件（该流恒为真）。支持算术 / 比较 / 逻辑 / 三目、属性与索引、方法调用（与 JUEL 语义一致）',
    placeholder: '${approved}',
    isEdited: isTextAreaEntryEdited,
    validate: validateConditionExpression,
    getValue: (target: DiagramElement) => readCondition(target),
    setValue: (target: DiagramElement, value: unknown) => {
      const text = normalizeText(value);

      context.modeling.updateProperties(target, {
        conditionExpression:
          text === undefined
            ? undefined
            : context.moddle.create('bpmn:FormalExpression', { body: text }),
      });
    },
  };
}

function readCondition(element: DiagramElement): string | undefined {
  const expression = businessObject(element).conditionExpression;
  const body = expression?.body;

  return body === undefined || body === null ? undefined : String(body);
}

/**
 * 只读说明条目。
 *
 * 用于两种情况：(a) 本子集**不支持**的能力（写了会被后端校验拒绝）；
 * (b) 由后端行为固定、不允许改的项（如 `isExecutable` 恒为 true）。
 * 用只读 textarea 而不是 input，是为了让长提示能读全。
 */
export function noteEntry(element: DiagramElement, label: string, text: string): PropertyEntry {
  return {
    id: `note-${element.id}-${label}`,
    component: TextAreaEntry,
    element,
    label,
    disabled: true,
    rows: 3,
    getValue: () => text,
    setValue: () => undefined,
  };
}

/**
 * 自定义读写逻辑的下拉条目（如"实现方式三选一，选中即清空另外两项"）。
 */
export function selectEntryRaw(
  options: {
    id: string;
    element: DiagramElement;
    label: string;
    description?: string;
    choices: SelectOption[];
    getValue: (element: DiagramElement) => string;
    setValue: (element: DiagramElement, value: string) => void;
  },
  context: EntryContext,
): PropertyEntry {
  void context;

  return {
    id: options.id,
    component: SelectEntry,
    element: options.element,
    label: options.label,
    description: options.description,
    isEdited: isSelectEntryEdited,
    options: options.choices,
    emptyValue: '',
    getValue: (element: DiagramElement) => options.getValue(element),
    setValue: (element: DiagramElement, value: unknown) =>
      options.setValue(element, value === undefined || value === null ? '' : String(value)),
  };
}

/** 条件表达式校验（与 `validation.ts` 的口径一致）。 */
export function validateConditionExpression(value: unknown): string | null {
  const text = normalizeText(value);

  if (text === undefined) {
    return null;
  }

  if (!/^[$#]\{.*\}$/.test(text)) {
    return '条件需写成 ${...} 或 #{...} 形式；否则后端按字面量处理，不会参与求值。';
  }

  return null;
}

/** 元素 id 校验：非空、且符合 Activiti 的 id 习惯（字母/下划线开头，只含字母数字下划线短横）。 */
export function validateElementId(value: unknown): string | null {
  const text = normalizeText(value);

  if (text === undefined) {
    return 'id 不能为空。';
  }

  if (!/^[A-Za-z_][A-Za-z0-9_-]*$/.test(text)) {
    return 'id 需以字母或下划线开头，且只含字母、数字、下划线与短横。';
  }

  return null;
}

/** 优先级校验（Activiti 的 priority 是整数）。 */
export function validatePriority(value: unknown): string | null {
  const text = normalizeText(value);

  if (text === undefined) {
    return null;
  }

  if (!/^-?\d+$/.test(text)) {
    return '优先级必须是整数。';
  }

  return null;
}

/** 条件表达式编辑器里描述过的"看起来像表达式"的判定，供校验复用。 */
export function looksLikeExpression(text: string | undefined): boolean {
  return text !== undefined && /^[$#]\{.*\}$/.test(text);
}

/** 该元素是否声明了子集不支持的能力（会被后端校验规则 9 拒绝）。 */
export function unsupportedCapabilities(element: DiagramElement): string[] {
  const bo: ModdleElement = businessObject(element);
  const found: string[] = [];
  const extensions = bo.extensionElements?.values ?? [];

  if (bo.$type === 'bpmn:UserTask' && extensions.some((value) => value.$type === 'activiti:TaskListener')) {
    found.push('任务监听器（activiti:taskListener）');
  }

  if (extensions.some((value) => value.$type === 'activiti:ExecutionListener')) {
    found.push('执行监听器（activiti:executionListener）');
  }

  if (bo.$type === 'bpmn:ServiceTask' && extensions.some((value) => value.$type === 'activiti:Field')) {
    found.push('字段注入（activiti:field）');
  }

  if ((bo.eventDefinitions ?? []).length > 0) {
    found.push('事件定义（定时/消息/信号等）');
  }

  return found;
}
