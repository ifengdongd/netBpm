/**
 * 列表型编辑器：执行监听器 / 任务监听器 / 事件监听器 / 表单属性。
 *
 * 对齐 Activiti 原版设计器（迁移源）的能力：
 *  - 流程/任务/顺序流可挂 activiti:executionListener（事件 start/end/take，实现 class/expression/delegateExpression）；
 *  - 用户任务可挂 activiti:taskListener（事件 create/assignment/complete/delete）；
 *  - 流程级 activiti:eventListener（events 逗号分隔 + entityType + throwOnFailure）；
 *  - 用户任务 activiti:formProperty（id/name/type，enum 时带 <activiti:value id name> 子项，
 *    以及 expression/variable/required/readable/writable）。
 *
 * @bpmn-io/properties-panel 没有现成的列表条目组件，因此这里用 preact 手写编辑器：
 * 组件在面板渲染树内执行（hooks 可用），所有修改都走 modeling 命令栈（可撤销）。
 */
import { h, type JSX } from 'preact';

import { localDebounce, type EntryContext, type SelectOption } from './entries';
import { businessObject, type DiagramElement, type ModdleElement, type PropertyEntry } from './types';

type Translate = (text: string) => string;

/** 监听器的三种实现方式（与后端取值优先级一致：class &gt; delegateExpression &gt; expression）。 */
const IMPLEMENTATION_KEYS = ['class', 'delegateExpression', 'expression'] as const;

type ImplementationKey = (typeof IMPLEMENTATION_KEYS)[number];

const IMPLEMENTATION_LABELS: Record<ImplementationKey, string> = {
  class: '类名',
  delegateExpression: '委托表达式',
  expression: '表达式',
};

const FORM_PROPERTY_TYPES = ['string', 'long', 'double', 'date', 'boolean', 'enum'] as const;

const ENTITY_TYPES = [
  'ATTACHMENT', 'COMMENT', 'EXECUTION', 'GROUP', 'IDENTITY_LINK', 'JOB',
  'MODEL', 'PROCESS_DEFINITION', 'PROCESS_INSTANCE', 'TASK', 'USER',
] as const;

// ⚠️ 对齐 Activiti 原版设计器：事件监听器的 Events 下拉（引擎支持的事件清单，按字母序）
const EVENT_LISTENER_EVENTS = [
  'ACTIVITY_COMPENSATE',
  'ACTIVITY_COMPLETED',
  'ACTIVITY_ERROR_RECEIVED',
  'ACTIVITY_MESSAGE_RECEIVED',
  'ACTIVITY_MESSAGE_WAITING',
  'ACTIVITY_SIGNALED',
  'ACTIVITY_STARTED',
  'ENGINE_CLOSED',
  'ENGINE_CREATED',
  'ENTITY_ACTIVATED',
  'ENTITY_CREATED',
  'ENTITY_DELETED',
  'ENTITY_INITIALIZED',
  'ENTITY_SUSPENDED',
  'ENTITY_UPDATED',
  'JOB_EXECUTION_FAILURE',
  'JOB_EXECUTION_SUCCESS',
  'JOB_RETRIES_DECREMENTED',
  'JOB_RESCHEDULED',
  'MEMBERSHIP_CREATED',
  'MEMBERSHIP_DELETED',
  'MEMBERSHIPS_DELETED',
  'PROCESS_CANCELLED',
  'PROCESS_COMPLETED',
  'PROCESS_STARTED',
  'TASK_ASSIGNED',
  'TASK_COMPLETED',
  'TASK_CREATED',
  'TASK_FAILED',
  'TIMER_FIRED',
  'UNCAUGHT_BPMN_ERROR',
  'VARIABLE_CREATED',
  'VARIABLE_DELETED',
  'VARIABLE_UPDATED',
] as const;

// ─────────────────────────── 扩展元素读写小工具 ───────────────────────────

function isSet(value: unknown): boolean {
  return value !== undefined && value !== null && String(value).trim().length > 0;
}

function textOf(value: unknown): string {
  return value === undefined || value === null ? '' : String(value);
}

function normalize(value: unknown): string | undefined {
  const text = textOf(value).trim();
  return text.length === 0 ? undefined : text;
}

/** 取（或创建）元素的 <extensionElements>。 */
function ensureExtensionElements(element: DiagramElement, context: EntryContext): ModdleElement {
  const bo = businessObject(element);

  if (bo.extensionElements !== undefined) {
    return bo.extensionElements as ModdleElement;
  }

  const created = context.moddle.create('bpmn:ExtensionElements', { values: [] });
  context.modeling.updateModdleProperties(element, bo, { extensionElements: created });

  // 命令同步执行，此后 bo.extensionElements 一定存在
  return businessObject(element).extensionElements as ModdleElement;
}

function readItems(bo: ModdleElement, typeName: string): ModdleElement[] {
  return (bo.extensionElements?.values ?? []).filter((value) => value.$type === typeName);
}

/** 整体重写 <extensionElements> 的 values（增删监听器时用）。 */
function rewriteValues(
  element: DiagramElement,
  context: EntryContext,
  extensionElements: ModdleElement,
  transform: (values: ModdleElement[]) => ModdleElement[],
): void {
  const next = transform((extensionElements.values as ModdleElement[] | undefined) ?? []);
  context.modeling.updateModdleProperties(element, extensionElements, { values: next });
}

// ─────────────────────────── 编辑器组件 ───────────────────────────

/** 实现方式「类型 + 取值」联动行；切换类型时清空取值（三项互斥）。 */
function implementationFields(
  element: DiagramElement,
  context: EntryContext,
  t: Translate,
  item: ModdleElement,
  debounce: <A>(fn: (value: A) => void) => (value: A) => void,
): JSX.Element[] {
  const currentKey: ImplementationKey =
    IMPLEMENTATION_KEYS.find((key) => isSet(item[key])) ?? 'class';

  const setType = (key: string): void => {
    const properties: Record<string, unknown> = {};

    for (const candidate of IMPLEMENTATION_KEYS) {
      // 新选中的保留原值（若无则为空 ⇒ undefined）；其余清空
      properties[candidate] = candidate === key ? (item[candidate] ?? undefined) : undefined;
    }

    context.modeling.updateModdleProperties(element, item, properties);
  };

  const setValue = (raw: unknown): void => {
    const properties: Record<string, unknown> = {};

    for (const candidate of IMPLEMENTATION_KEYS) {
      properties[candidate] = candidate === currentKey ? normalize(raw) : undefined;
    }

    context.modeling.updateModdleProperties(element, item, properties);
  };

  return [
    h(
      'select',
      {
        class: 'nb-impl-type',
        value: currentKey,
        onChange: (event: Event) => setType((event.target as HTMLSelectElement).value),
      },
      IMPLEMENTATION_KEYS.map((key) =>
        h('option', { value: key, key }, t(IMPLEMENTATION_LABELS[key])),
      ),
    ),
    h('input', {
      class: 'nb-impl-value',
      type: 'text',
      placeholder: t(IMPLEMENTATION_LABELS[currentKey]),
      value: textOf(item[currentKey]),
      onChange: (event: Event) => debounce(setValue)((event.target as HTMLInputElement).value),
    }),
  ];
}

interface ListenersEditorProps {
  element: DiagramElement;
  context: EntryContext;
  t: Translate;
  /** activiti:ExecutionListener | activiti:TaskListener */
  typeName: string;
  /** 事件属性名：单事件 'event'，事件监听器是逗号分隔的 'events'。 */
  eventProperty: 'event' | 'events';
  eventChoices?: SelectOption[];
  addDefaults: Record<string, unknown>;
  emptyHint: string;
}

function ListenersEditor(props: ListenersEditorProps): unknown {
  const { element, context, t, typeName, eventProperty, eventChoices, addDefaults, emptyHint } = props;
  const debounce = localDebounce;

  const bo = businessObject(element);
  const items = readItems(bo, typeName);

  const addItem = (): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => [
      ...values,
      context.moddle.create(typeName, { ...addDefaults }),
    ]);
  };

  const removeItem = (item: ModdleElement): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => values.filter((value) => value !== item));
  };

  const setProp = (item: ModdleElement, name: string, value: unknown): void => {
    context.modeling.updateModdleProperties(element, item, { [name]: value });
  };

  const eventEditor = (item: ModdleElement): JSX.Element =>
    eventChoices !== undefined
      ? h(
          'select',
          {
            class: 'nb-event',
            value: textOf(item[eventProperty]),
            onChange: (event: Event) => setProp(item, eventProperty, normalize((event.target as HTMLSelectElement).value)),
          },
          eventChoices.map((choice) => h('option', { value: choice.value, key: choice.value }, choice.label)),
        )
      : h('input', {
          class: 'nb-event',
          type: 'text',
          placeholder: t('事件，如 take'),
          value: textOf(item[eventProperty]),
          onChange: (event: Event) => debounce((value: string) => setProp(item, eventProperty, normalize(value)))((event.target as HTMLInputElement).value),
        });

  return h('div', { class: 'nb-list-editor' },
    items.length === 0 ? h('p', { class: 'nb-empty' }, t(emptyHint)) : null,
    items.map((item, index) =>
      h('div', { class: 'nb-item', key: index },
        h('span', { class: 'nb-item-index' }, `${index + 1}.`),
        eventEditor(item),
        ...implementationFields(element, context, t, item, debounce),
        h('button', { class: 'nb-remove', type: 'button', title: t('删除'), onClick: () => removeItem(item) }, '✕'),
      ),
    ),
    h('button', { class: 'nb-add', type: 'button', onClick: () => addItem() }, `＋ ${t('添加监听器')}`),
  );
}

/** 流程级 activiti:eventListener（事件监听器）。 */
function EventListenersEditor(props: { element: DiagramElement; context: EntryContext; t: Translate }): unknown {
  const { element, context, t } = props;
  const debounce = localDebounce;

  const bo = businessObject(element);
  const items = readItems(bo, 'activiti:EventListener');

  const addItem = (): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => [
      ...values,
      context.moddle.create('activiti:EventListener', { events: 'TASK_COMPLETED' }),
    ]);
  };

  const removeItem = (item: ModdleElement): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => values.filter((value) => value !== item));
  };

  const setProp = (item: ModdleElement, name: string, value: unknown): void => {
    context.modeling.updateModdleProperties(element, item, { [name]: value });
  };

  return h('div', { class: 'nb-list-editor' },
    items.length === 0 ? h('p', { class: 'nb-empty' }, t('尚未配置事件监听器（activiti:eventListener）。')) : null,
    items.map((item, index) =>
      h('div', { class: 'nb-item nb-item-stack', key: index },
        h('div', { class: 'nb-item-row' },
          h('span', { class: 'nb-item-index' }, `${index + 1}.`),
          (() => {
            // ⚠️ Events 改为下拉（对齐 Activiti 原版）；XML 里已有但不在清单里的值
            // 自动追加为额外选项，避免打开面板就把存量数据改掉
            const current = textOf(item.events);
            const choices = EVENT_LISTENER_EVENTS.includes(current as never) || current.length === 0
              ? EVENT_LISTENER_EVENTS
              : [current, ...EVENT_LISTENER_EVENTS];

            return h(
              'select',
              {
                class: 'nb-event',
                value: current,
                onChange: (event: Event) => debounce((value: string) => setProp(item, 'events', normalize(value)))((event.target as HTMLSelectElement).value),
              },
              [
                h('option', { value: '', key: '' }, t('（选择事件）')),
                ...choices.map((name) => h('option', { value: name, key: name }, name)),
              ],
            );
          })(),
          h(
            'select',
            {
              class: 'nb-entity-type',
              value: textOf(item.entityType),
              title: t('实体类型（entityType）'),
              onChange: (event: Event) => setProp(item, 'entityType', normalize((event.target as HTMLSelectElement).value)),
            },
            [
              h('option', { value: '', key: '' }, t('（无实体类型）')),
              ...ENTITY_TYPES.map((type) => h('option', { value: type, key: type }, type)),
            ],
          ),
          h('button', { class: 'nb-remove', type: 'button', title: t('删除'), onClick: () => removeItem(item) }, '✕'),
        ),
        ...implementationFields(element, context, t, item, debounce),
        h('label', { class: 'nb-check' },
          h('input', {
            type: 'checkbox',
            checked: item.throwOnFailure === true,
            onChange: (event: Event) => setProp(item, 'throwOnFailure', (event.target as HTMLInputElement).checked),
          }),
          t('失败时抛错（throwOnFailure）'),
        ),
      ),
    ),
    h('button', { class: 'nb-add', type: 'button', onClick: () => addItem() }, `＋ ${t('添加事件监听器')}`),
  );
}

/** 用户任务 activiti:formProperty（表单属性）。 */
function FormPropertiesEditor(props: { element: DiagramElement; context: EntryContext; t: Translate }): unknown {
  const { element, context, t } = props;
  const debounce = localDebounce;

  const bo = businessObject(element);
  const items = readItems(bo, 'activiti:FormProperty');

  const addItem = (): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => [
      ...values,
      context.moddle.create('activiti:FormProperty', { type: 'string', readable: true, writable: true }),
    ]);
  };

  const removeItem = (item: ModdleElement): void => {
    const extensionElements = ensureExtensionElements(element, context);
    rewriteValues(element, context, extensionElements, (values) => values.filter((value) => value !== item));
  };

  const setProp = (item: ModdleElement, name: string, value: unknown): void => {
    context.modeling.updateModdleProperties(element, item, { [name]: value });
  };

  const enumValuesEditor = (item: ModdleElement): JSX.Element => {
    const enumValues = (item.value as ModdleElement[] | undefined) ?? [];

    const addEnumValue = (): void => {
      context.modeling.updateModdleProperties(element, item, {
        value: [...enumValues, context.moddle.create('activiti:Value', { id: '', name: '' })],
      });
    };

    const removeEnumValue = (value: ModdleElement): void => {
      context.modeling.updateModdleProperties(element, item, {
        value: enumValues.filter((candidate) => candidate !== value),
      });
    };

    return h('div', { class: 'nb-enum-values' },
      h('div', { class: 'nb-enum-title' }, t('枚举选项（activiti:value）')),
      enumValues.map((value, index) =>
        h('div', { class: 'nb-item-row', key: index },
          h('input', {
            class: 'nb-enum-id',
            type: 'text',
            placeholder: t('Id'),
            value: textOf(value.id),
            onChange: (event: Event) => debounce((v: string) => setProp(value, 'id', normalize(v)))((event.target as HTMLInputElement).value),
          }),
          h('input', {
            class: 'nb-enum-name',
            type: 'text',
            placeholder: t('名称'),
            value: textOf(value.name),
            onChange: (event: Event) => debounce((v: string) => setProp(value, 'name', normalize(v)))((event.target as HTMLInputElement).value),
          }),
          h('button', { class: 'nb-remove', type: 'button', title: t('删除'), onClick: () => removeEnumValue(value) }, '✕'),
        ),
      ),
      h('button', { class: 'nb-add', type: 'button', onClick: addEnumValue }, `＋ ${t('添加选项')}`),
    );
  };

  return h('div', { class: 'nb-list-editor' },
    items.length === 0 ? h('p', { class: 'nb-empty' }, t('尚未配置表单属性（activiti:formProperty）。')) : null,
    items.map((item, index) =>
      h('div', { class: 'nb-item nb-item-stack', key: index },
        h('div', { class: 'nb-item-row' },
          h('span', { class: 'nb-item-index' }, `${index + 1}.`),
          h('input', {
            class: 'nb-form-id',
            type: 'text',
            placeholder: t('Id，如 approvalResult'),
            value: textOf(item.id),
            onChange: (event: Event) => debounce((v: string) => setProp(item, 'id', normalize(v)))((event.target as HTMLInputElement).value),
          }),
          h('input', {
            class: 'nb-form-name',
            type: 'text',
            placeholder: t('名称，如 审批结果选项'),
            value: textOf(item.name),
            onChange: (event: Event) => debounce((v: string) => setProp(item, 'name', normalize(v)))((event.target as HTMLInputElement).value),
          }),
          h(
            'select',
            {
              class: 'nb-form-type',
              value: textOf(item.type) || 'string',
              onChange: (event: Event) => setProp(item, 'type', normalize((event.target as HTMLSelectElement).value)),
            },
            FORM_PROPERTY_TYPES.map((type) => h('option', { value: type, key: type }, type)),
          ),
          h('button', { class: 'nb-remove', type: 'button', title: t('删除'), onClick: () => removeItem(item) }, '✕'),
        ),
        textOf(item.type) === 'enum' ? enumValuesEditor(item) : null,
        h('div', { class: 'nb-item-row' },
          h('input', {
            class: 'nb-impl-value',
            type: 'text',
            placeholder: t('表达式（expression）'),
            value: textOf(item.expression),
            onChange: (event: Event) => debounce((v: string) => setProp(item, 'expression', normalize(v)))((event.target as HTMLInputElement).value),
          }),
          h('input', {
            class: 'nb-impl-value',
            type: 'text',
            placeholder: t('变量（variable）'),
            value: textOf(item.variable),
            onChange: (event: Event) => debounce((v: string) => setProp(item, 'variable', normalize(v)))((event.target as HTMLInputElement).value),
          }),
        ),
        h('div', { class: 'nb-check-row' },
          ['required', 'readable', 'writable'].map((name) =>
            h('label', { class: 'nb-check', key: name },
              h('input', {
                type: 'checkbox',
                checked: item[name] === true,
                onChange: (event: Event) => setProp(item, name, (event.target as HTMLInputElement).checked),
              }),
              t(name),
            ),
          ),
        ),
      ),
    ),
    h('button', { class: 'nb-add', type: 'button', onClick: () => addItem() }, `＋ ${t('添加表单属性')}`),
  );
}

// ─────────────────────────── 组工厂（供 propertiesProvider 使用）───────────────────────────

function listGroup(
  id: string,
  label: string,
  editor: unknown,
  element: DiagramElement,
  props: Record<string, unknown>,
): { id: string; label: string; entries: PropertyEntry[] } {
  return {
    id,
    label,
    entries: [
      {
        id: `${id}-editor`,
        component: editor,
        element,
        ...props,
      } as PropertyEntry,
    ],
  };
}

/** 执行/任务监听器编辑组。 */
export function listenersGroup(
  id: string,
  label: string,
  element: DiagramElement,
  context: EntryContext,
  t: Translate,
  options: {
    typeName: 'activiti:ExecutionListener' | 'activiti:TaskListener';
    eventChoices: SelectOption[];
    addDefaults: Record<string, unknown>;
    emptyHint: string;
  },
): { id: string; label: string; entries: PropertyEntry[] } {
  return listGroup(id, label, ListenersEditor, element, {
    context,
    t,
    typeName: options.typeName,
    eventProperty: 'event',
    eventChoices: options.eventChoices,
    addDefaults: options.addDefaults,
    emptyHint: options.emptyHint,
  });
}

/** 流程级事件监听器编辑组。 */
export function eventListenersGroup(
  element: DiagramElement,
  context: EntryContext,
  t: Translate,
): { id: string; label: string; entries: PropertyEntry[] } {
  return listGroup('netbpm-event-listeners', t('事件监听器'), EventListenersEditor, element, { context, t });
}

/** 用户任务表单属性编辑组。 */
export function formPropertiesGroup(
  element: DiagramElement,
  context: EntryContext,
  t: Translate,
): { id: string; label: string; entries: PropertyEntry[] } {
  return listGroup('netbpm-form-properties', t('表单属性'), FormPropertiesEditor, element, { context, t });
}
