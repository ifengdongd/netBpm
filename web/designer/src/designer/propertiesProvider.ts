/**
 * NetBpm 属性面板（开发计划 §6.2 第 3 项）。
 *
 * 🔑 **不装载 bpmn-js 自带的 `BpmnPropertiesProviderModule`**，而是整体接管属性组：
 * 官方默认组里有 Documentation / 多实例 / 事件定义 等本子集**不支持**的项 —— 它们要么被后端
 * 静默丢弃（documentation、potentialStarter、exporterVersion），要么被校验规则 9 拒绝。
 * "只暴露子集支持的属性" 因此落成一句话：**这里的白名单就是面板的全部内容**。
 *
 * 可写性以「后端能读、也能写回」为准（见 §15.4 / §15.5 与 BpmnXmlConverter 的读写对称性）：
 *  - 安全读写：id/name；userTask 的 assignee/owner/candidateUsers/candidateGroups/formKey/
 *    dueDate/priority/category/skipExpression；serviceTask 的 class/delegateExpression/expression/
 *    resultVariableName；scriptTask 的 scriptFormat/script/resultVariable/autoStoreVariables；
 *    顺序流的条件表达式；活动与排他网关的 default（默认流）；
 *  - **不暴露**：documentation、potentialStarter、process 级 candidateStarter*、自定义
 *    extensionElements、事件定义 —— 模型或转换器不支持，暴露就是"填了也白填"（风险 R5）。
 */
import { Group } from '@bpmn-io/properties-panel';

import {
  checkboxEntry,
  conditionEntry,
  defaultFlowEntry,
  normalizeText,
  noteEntry,
  selectEntryRaw,
  textAreaEntry,
  textEntry,
  unsupportedCapabilities,
  validateElementId,
  validatePriority,
  type EntryContext,
} from './entries';

import {
  businessObject,
  type DiagramElement,
  type PropertiesPanelService,
} from './types';

/** 子集元素（§1.2）。其它类型（子流程/边界事件/泳道…）不属白名单，面板不渲染。 */
const SUBSET_TYPES = new Set([
  'bpmn:StartEvent',
  'bpmn:EndEvent',
  'bpmn:UserTask',
  'bpmn:ServiceTask',
  'bpmn:ManualTask',
  'bpmn:ScriptTask',
  'bpmn:ExclusiveGateway',
  'bpmn:ParallelGateway',
  'bpmn:SequenceFlow',
]);

/** 服务任务的三种实现方式；顺序即后端 `ImplementationType` 的取值优先级。 */
const IMPLEMENTATION_KEYS = ['class', 'delegateExpression', 'expression'] as const;

const IMPLEMENTATION_LABELS: Record<(typeof IMPLEMENTATION_KEYS)[number], string> = {
  class: 'Java 类名（activiti:class）',
  delegateExpression: '委托表达式（activiti:delegateExpression）',
  expression: '值表达式（activiti:expression）',
};

export default class NetBpmPropertiesProvider {
  static $inject = ['propertiesPanel', 'modeling', 'moddle', 'translate'];

  private readonly context: EntryContext;
  private readonly translate: (text: string) => string;

  constructor(
    propertiesPanel: PropertiesPanelService,
    modeling: EntryContext['modeling'],
    moddle: EntryContext['moddle'],
    translate: (text: string) => string,
  ) {
    this.context = { modeling, moddle };
    this.translate = translate;

    propertiesPanel.registerProvider(500, this);
  }

  /**
   * properties-panel 每次重绘都会调用返回的函数，因此可以按「当前值」动态决定渲染哪些条目
   * （例如服务任务只显示当前实现方式对应的那个输入框）。
   */
  getGroups(element: DiagramElement): (groups: unknown[]) => unknown[] {
    const t = this.translate;
    const context = this.context;

    return () => buildGroups(element, context, t);
  }
}

function buildGroups(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown[] {
  const type = businessObject(element).$type as string;

  if (type === 'bpmn:Process') {
    return [processGroup(element, context, t)];
  }

  if (!SUBSET_TYPES.has(type)) {
    return [];
  }

  const groups: unknown[] = [commonGroup(element, context, t)];

  switch (type) {
    case 'bpmn:UserTask':
      groups.push(userTaskGroup(element, context, t));
      break;
    case 'bpmn:ServiceTask':
      groups.push(serviceTaskGroup(element, context, t));
      break;
    case 'bpmn:ScriptTask':
      groups.push(scriptTaskGroup(element, context, t));
      break;
    case 'bpmn:SequenceFlow':
      groups.push(sequenceFlowGroup(element, context, t));
      break;
    case 'bpmn:ExclusiveGateway':
    case 'bpmn:ParallelGateway':
      groups.push(gatewayGroup(element, context, t));
      break;
    default:
      break;
  }

  const unsupported = unsupportedGroup(element, t);

  if (unsupported !== null) {
    groups.push(unsupported);
  }

  return groups;
}

// ─────────────────────────── 各元素属性组 ───────────────────────────

function processGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return Group({
    id: 'netbpm-process',
    label: t('流程'),
    entries: [
      textEntry(
        {
          id: 'processId',
          property: 'id',
          element,
          label: t('流程 ID'),
          description: t('流程定义 id 形如 {key}:{version}:{id}，其中的 key 就是它'),
          validate: validateElementId,
        },
        context,
      ),
      textEntry({ id: 'processName', property: 'name', element, label: t('流程名称') }, context),
      noteEntry(
        element,
        t('是否可执行'),
        t('固定为 true：后端导出时恒写 isExecutable="true"（见开发计划 §15 勘误），因此这里只读。'),
      ),
    ],
  });
}

function commonGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return Group({
    id: 'netbpm-common',
    label: t('基本'),
    entries: [
      textEntry(
        { id: 'elementId', property: 'id', element, label: t('id'), validate: validateElementId },
        context,
      ),
      textEntry({ id: 'elementName', property: 'name', element, label: t('名称') }, context),
    ],
  });
}

function userTaskGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  const entries = [
    textEntry(
      {
        id: 'assignee',
        property: 'assignee',
        element,
        label: t('办理人'),
        description: t('留空也可以部署（Activiti 允许无人办理的任务），但这样只能按流程实例查待办'),
      },
      context,
    ),
    textEntry({ id: 'owner', property: 'owner', element, label: t('所有者') }, context),
    textEntry(
      {
        id: 'candidateUsers',
        property: 'candidateUsers',
        element,
        label: t('候选用户'),
        description: t('多个用英文逗号分隔，例如 alice,bob'),
        placeholder: 'alice,bob',
      },
      context,
    ),
    textEntry(
      {
        id: 'candidateGroups',
        property: 'candidateGroups',
        element,
        label: t('候选组'),
        description: t('多个用英文逗号分隔，例如 managers,hr'),
        placeholder: 'managers,hr',
      },
      context,
    ),
    textEntry(
      {
        id: 'formKey',
        property: 'formKey',
        element,
        label: t('表单 Key'),
        description: t('本子集只透传，不做渲染'),
      },
      context,
    ),
    textEntry(
      { id: 'dueDate', property: 'dueDate', element, label: t('到期时间'), placeholder: '2030-01-02T03:04:05' },
      context,
    ),
    textEntry({ id: 'priority', property: 'priority', element, label: t('优先级'), validate: validatePriority }, context),
    textEntry({ id: 'category', property: 'category', element, label: t('分类') }, context),
    textEntry(
      { id: 'skipExpression', property: 'skipExpression', element, label: t('跳过表达式'), placeholder: '${skip}' },
      context,
    ),
    defaultFlowEntry(element, t('默认流'), context),
  ];

  return Group({
    id: 'netbpm-user-task',
    label: t('用户任务'),
    entries: entries.filter((entry) => entry !== null),
  });
}

function serviceTaskGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  const current = currentImplementation(element);
  const entries = [implementationSelectEntry(element, context, t)];

  if (current === undefined) {
    entries.push(
      noteEntry(
        element,
        t('实现方式取值'),
        t('尚未选择实现方式。服务任务必须指定 class / delegateExpression / expression 之一，否则后端校验会拒绝部署。'),
      ),
    );
  } else {
    entries.push(textEntry({ id: `implementation-${current}`, property: current, element, label: t('取值') }, context));
  }

  entries.push(
    textEntry(
      {
        id: 'resultVariableName',
        property: 'resultVariableName',
        element,
        label: t('结果变量名'),
        description: t('expression 的求值结果写入这个流程变量'),
      },
      context,
    ),
    defaultFlowEntry(element, t('默认流'), context) ?? noteEntry(element, t('默认流'), t('该任务还没有出流；请先连线。')),
  );

  return Group({
    id: 'netbpm-service-task',
    label: t('服务任务'),
    entries,
  });
}

function scriptTaskGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return Group({
    id: 'netbpm-script-task',
    label: t('脚本任务'),
    entries: [
      textEntry(
        { id: 'scriptFormat', property: 'scriptFormat', element, label: t('脚本格式'), placeholder: 'javascript' },
        context,
      ),
      textEntry({ id: 'resultVariable', property: 'resultVariable', element, label: t('结果变量名') }, context),
      checkboxEntry(
        {
          id: 'autoStoreVariables',
          property: 'autoStoreVariables',
          element,
          label: t('自动存储变量'),
          description: t('勾选后写出 activiti:autoStoreVariables="true"'),
          trueValue: 'true',
        },
        context,
      ),
      textAreaEntry(
        {
          id: 'script',
          property: 'script',
          element,
          label: t('脚本正文'),
          rows: 6,
          description: t('写入 <script> 子元素'),
        },
        context,
      ),
    ],
  });
}

function sequenceFlowGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return Group({
    id: 'netbpm-sequence-flow',
    label: t('顺序流'),
    entries: [conditionEntry(element, context)],
  });
}

function gatewayGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  if (businessObject(element).$type === 'bpmn:ParallelGateway') {
    return Group({
      id: 'netbpm-parallel-gateway',
      label: t('并行网关'),
      entries: [
        noteEntry(
          element,
          t('默认流'),
          t('并行网关不支持默认流：出口不做条件判定，全部分支并行推进（后端校验会拒绝设置了默认流的并行网关）。'),
        ),
      ],
    });
  }

  const defaultFlow = defaultFlowEntry(element, t('默认流'), context);

  return Group({
    id: 'netbpm-exclusive-gateway',
    label: t('排他网关'),
    entries: [
      defaultFlow ??
        noteEntry(element, t('默认流'), t('该网关还没有出流；请先连线再选择默认流（无默认流时至少要有一条无条件出流）。')),
    ],
  });
}

/** 子集不支持的能力：只提示，不提供编辑入口。 */
function unsupportedGroup(element: DiagramElement, t: (text: string) => string): unknown | null {
  const capabilities = unsupportedCapabilities(element);

  if (capabilities.length === 0) {
    return null;
  }

  return Group({
    id: 'netbpm-unsupported',
    label: t('子集不支持'),
    entries: capabilities.map((capability) =>
      noteEntry(
        element,
        `${t('不支持')}：${capability}`,
        t('本子集未实现该能力，后端会显式拒绝部署（校验规则「不支持的能力」）。请移除后再部署。'),
      ),
    ),
  });
}

// ─────────────────────────── 小工具 ───────────────────────────

/** 服务任务当前生效的实现方式（取值优先级与后端一致：class &gt; delegateExpression &gt; expression）。 */
function currentImplementation(element: DiagramElement): (typeof IMPLEMENTATION_KEYS)[number] | undefined {
  const bo = businessObject(element);

  for (const key of IMPLEMENTATION_KEYS) {
    const value = bo[key];

    if (value !== undefined && value !== null && String(value).length > 0) {
      return key;
    }
  }

  return undefined;
}

/**
 * 「实现方式」下拉：选中一项时**清空**另外两项。
 *
 * 后端对三项并存不做拦截（按优先级静默取第一个），这里强制互斥，
 * 避免出现「都填了、但只有 class 生效」的错觉。
 */
function implementationSelectEntry(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return selectEntryRaw(
    {
      id: 'implementationType',
      element,
      label: t('实现方式'),
      choices: [
        { label: t('（未选择）'), value: '' },
        ...IMPLEMENTATION_KEYS.map((key) => ({ label: t(IMPLEMENTATION_LABELS[key]), value: key })),
      ],
      getValue: (target) => currentImplementation(target) ?? '',
      setValue: (target, value) => {
        const properties: Record<string, unknown> = {};

        for (const key of IMPLEMENTATION_KEYS) {
          // 选中的一项保留原值（空值 ⇒ undefined）；未选中的显式置 undefined
          // ⇒ XML 里不写出该属性（避免 activiti:expression="" 这种空属性）
          properties[key] = key === value ? normalizeText(readValue(target, key)) : undefined;
        }

        context.modeling.updateProperties(target, properties);
      },
    },
    context,
  );
}

/** 切换实现方式时保留已填的取值，用户不必重打一遍。 */
function readValue(element: DiagramElement, property: string): string {
  const value = businessObject(element)[property];

  return value === undefined || value === null ? '' : String(value);
}
