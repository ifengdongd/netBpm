/**
 * NetBpm 属性面板（开发计划 §6.2 第 3 项）。
 *
 * 🔑 **不装载 bpmn-js 自带的 `BpmnPropertiesProviderModule`**，而是整体接管属性组：
 * 官方默认组里有 Documentation / potentialStarter / 自定义扩展 等本子集**不执行**的项 ——
 * 暴露它们就是「填了也白填」。多实例（会签/串签）后端已支持，因此这里提供了独立属性组；
 * 事件定义仍由后端校验拒绝，只给只读提示。
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
import {
  boundaryTimerEntries,
  checkboxEntry,
  conditionEntry,
  defaultFlowEntry,
  multiInstanceEntries,
  noteEntry,
  terminateEndEventEntries,
  textAreaEntry,
  textEntry,
  unsupportedCapabilities,
  validateElementId,
  validatePriority,
  type EntryContext,
  type SelectOption,
} from './entries';
import {
  eventListenersGroup,
  formPropertiesGroup,
  listenersGroup,
} from './listEditors';

import {
  businessObject,
  type DiagramElement,
  type PropertiesPanelService,
} from './types';

/**
 * 属性组的形状（⚠️ 必须是**普通对象**）：
 * bpmn-js-properties-panel 拿到 groups 后用自带的 Group 组件渲染每一个，
 * `@bpmn-io/properties-panel` 导出的 `Group` 是 Preact 组件——在 provider 里
 * 把它当工厂函数调用会在渲染上下文之外执行 hooks 而抛错，面板因此整体空白。
 */
interface NetBpmGroup {
  id: string;
  label: string;
  entries: unknown[];
  shouldOpen?: boolean;
}

function group(id: string, label: string, entries: unknown[]): NetBpmGroup {
  return { id, label, entries };
}

/** 子集元素（§1.2）。其它类型（子流程/泳道…）不属白名单，面板不渲染。 */
const SUBSET_TYPES = new Set([
  'bpmn:StartEvent',
  'bpmn:EndEvent',
  'bpmn:BoundaryEvent',
  'bpmn:UserTask',
  'bpmn:ServiceTask',
  'bpmn:ManualTask',
  'bpmn:ScriptTask',
  'bpmn:ExclusiveGateway',
  'bpmn:ParallelGateway',
  'bpmn:InclusiveGateway',
  'bpmn:EventBasedGateway',
  'bpmn:SequenceFlow',
]);

// ─────────────────────────── 监听器事件选项 ───────────────────────────

// ⚠️ 对齐 Activiti 原版设计器：执行监听器的 Event 下拉统一提供 start / end / take
const EXECUTION_EVENT_CHOICES: SelectOption[] = [
  { label: 'start（进入时）', value: 'start' },
  { label: 'end（离开时）', value: 'end' },
  { label: 'take（流经时）', value: 'take' },
];

// ⚠️ 不提供 delete：引擎尚未实现任务删除通知（各条删除路径都没有触发点），
// 配置了也会在部署期被 listener-configuration 规则拒绝。
const TASK_EVENT_CHOICES: SelectOption[] = [
  { label: 'create（创建时）', value: 'create' },
  { label: 'assignment（被指派时）', value: 'assignment' },
  { label: 'complete（完成时）', value: 'complete' },
];

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
    return [
      processGroup(element, context, t),
      listenersGroup('netbpm-process-exec-listeners', t('执行监听器'), element, context, t, {
        typeName: 'activiti:ExecutionListener',
        eventChoices: EXECUTION_EVENT_CHOICES,
        addDefaults: { event: 'start' },
        emptyHint: t('尚未配置执行监听器（activiti:executionListener）。'),
      }),
      eventListenersGroup(element, context, t),
    ];
  }

  if (!SUBSET_TYPES.has(type)) {
    return [];
  }

  const groups: unknown[] = [commonGroup(element, context, t)];

  switch (type) {
    case 'bpmn:UserTask':
      groups.push(userTaskGroup(element, context, t));
      groups.push(multiInstanceGroup(element, context, t));
      groups.push(
        listenersGroup('netbpm-user-task-exec-listeners', t('执行监听器'), element, context, t, {
          typeName: 'activiti:ExecutionListener',
          eventChoices: EXECUTION_EVENT_CHOICES,
          addDefaults: { event: 'start' },
          emptyHint: t('尚未配置执行监听器（activiti:executionListener）。'),
        }),
        listenersGroup('netbpm-user-task-task-listeners', t('任务监听器'), element, context, t, {
          typeName: 'activiti:TaskListener',
          eventChoices: TASK_EVENT_CHOICES,
          addDefaults: { event: 'create' },
          emptyHint: t('尚未配置任务监听器（activiti:taskListener）。'),
        }),
        formPropertiesGroup(element, context, t),
      );
      break;
    case 'bpmn:ServiceTask':
      groups.push(serviceTaskGroup(element, context, t));
      groups.push(
        listenersGroup('netbpm-service-task-exec-listeners', t('执行监听器'), element, context, t, {
          typeName: 'activiti:ExecutionListener',
          eventChoices: EXECUTION_EVENT_CHOICES,
          addDefaults: { event: 'start' },
          emptyHint: t('尚未配置执行监听器（activiti:executionListener）。'),
        }),
      );
      break;
    case 'bpmn:ScriptTask':
      groups.push(scriptTaskGroup(element, context, t));
      // 脚本任务同样可以有执行监听器（引擎对任意 FlowElement 都会触发 start/end）
      groups.push(
        listenersGroup('netbpm-script-task-exec-listeners', t('执行监听器'), element, context, t, {
          typeName: 'activiti:ExecutionListener',
          eventChoices: EXECUTION_EVENT_CHOICES,
          addDefaults: { event: 'start' },
          emptyHint: t('尚未配置执行监听器（activiti:executionListener）。'),
        }),
      );
      break;
    case 'bpmn:BoundaryEvent':
      groups.push(group('netbpm-boundary-timer', t('边界定时器'), boundaryTimerEntries(element, context, t)));
      break;
    case 'bpmn:EndEvent':
      groups.push(group('netbpm-end-event', t('结束事件'), terminateEndEventEntries(element, context, t)));
      break;
    case 'bpmn:SequenceFlow':
      groups.push(sequenceFlowGroup(element, context, t));
      groups.push(
        listenersGroup('netbpm-sequence-flow-exec-listeners', t('执行监听器'), element, context, t, {
          typeName: 'activiti:ExecutionListener',
          eventChoices: EXECUTION_EVENT_CHOICES,
          addDefaults: { event: 'take' },
          emptyHint: t('尚未配置执行监听器（activiti:executionListener，事件为 take）。'),
        }),
      );
      break;
    case 'bpmn:ExclusiveGateway':
    case 'bpmn:ParallelGateway':
    case 'bpmn:InclusiveGateway':
    case 'bpmn:EventBasedGateway':
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
  return group('netbpm-process', t('流程'), [
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
  ]);
}

function commonGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return group('netbpm-common', t('基本'), [
    textEntry(
      { id: 'elementId', property: 'id', element, label: t('id'), validate: validateElementId },
      context,
    ),
    textEntry({ id: 'elementName', property: 'name', element, label: t('名称') }, context),
  ]);
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
        label: t('办理人（审批人）'),
        description: t('任务派遣：直接指派办理人，支持表达式如 ${starter}；留空也可部署，但只能按流程实例查待办'),
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
        label: t('表单标识（formKey）'),
        description: t('任务关联的表单标识，如 leave-approve；后端只透传，不做渲染'),
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

  return group('netbpm-user-task', t('用户任务'), entries.filter((entry) => entry !== null));
}

/**
 * 多实例组（会签 / 串签）。
 *
 * 后端已支持 userTask 的 `multiInstanceLoopCharacteristics`，但面板过去完全没有入口 ——
 * 只能手写 XML。这里补齐：开关、串/并行、loopCardinality、collection、elementVariable、完成条件。
 */
function multiInstanceGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return group(
    'netbpm-user-task-multi-instance',
    t('多实例（会签 / 串签）'),
    multiInstanceEntries(element, context, t),
  );
}

function serviceTaskGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  // ⚠️ 对齐 Activiti 原版设计器：三个实现字段直接平铺（类名 / 表达式 / 代理的表达式），
  // 不再做"实现方式"下拉。三项并存时后端按 class > delegateExpression > expression
  // 静默取第一个，即时校验会给警告（见 validation.ts 规则 8）。
  return group('netbpm-service-task', t('服务任务'), [
    textEntry(
      {
        id: 'impl-class',
        property: 'class',
        element,
        label: t('类名'),
        // ⚠️ 不放示例占位值：避免"看起来已经填了 javaDelegate 类名"的错觉
        description: t('activiti:class，实现类的全限定名，如 com.example.MyJavaDelegate'),
      },
      context,
    ),
    textEntry(
      {
        id: 'impl-expression',
        property: 'expression',
        element,
        label: t('表达式'),
        placeholder: '${someBean.method(execution)}',
      },
      context,
    ),
    textEntry(
      {
        id: 'impl-delegateExpression',
        property: 'delegateExpression',
        element,
        label: t('代理的表达式'),
        placeholder: '${someDelegateBean}',
      },
      context,
    ),
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
  ]);
}

function scriptTaskGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return group('netbpm-script-task', t('脚本任务'), [
      textEntry(
        { id: 'scriptFormat', property: 'scriptFormat', element, label: t('脚本格式'), placeholder: 'csharp' },
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
  ]);
}

function sequenceFlowGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  return group('netbpm-sequence-flow', t('顺序流'), [conditionEntry(element, context)]);
}

function gatewayGroup(
  element: DiagramElement,
  context: EntryContext,
  t: (text: string) => string,
): unknown {
  const type = businessObject(element).$type;

  if (type === 'bpmn:ParallelGateway') {
    return group('netbpm-parallel-gateway', t('并行网关'), [
      noteEntry(
        element,
        t('默认流'),
        t('并行网关不支持默认流：出口不做条件判定，全部分支并行推进（后端校验会拒绝设置了默认流的并行网关）。'),
      ),
    ]);
  }

  if (type === 'bpmn:EventBasedGateway') {
    return group('netbpm-event-gateway', t('事件网关'), [
      noteEntry(
        element,
        t('后续节点'),
        t('事件网关的每条出流只能指向中间捕获事件（定时/消息/信号），本子集未实现，部署会被拒绝；请改用单一网关或包含网关。'),
      ),
    ]);
  }

  const defaultFlow = defaultFlowEntry(element, t('默认流'), context);
  const isInclusive = type === 'bpmn:InclusiveGateway';

  return group(isInclusive ? 'netbpm-inclusive-gateway' : 'netbpm-exclusive-gateway', isInclusive ? t('包含网关') : t('单一网关'), [
    defaultFlow ??
      noteEntry(element, t('默认流'), t('该网关还没有出流；请先连线再选择默认流（无默认流时至少要有一条无条件出流）。')),
  ]);
}

/** 子集不支持的能力：只提示，不提供编辑入口。 */
function unsupportedGroup(element: DiagramElement, t: (text: string) => string): unknown | null {
  const capabilities = unsupportedCapabilities(element);

  if (capabilities.length === 0) {
    return null;
  }

  return group(
    'netbpm-unsupported',
    t('子集不支持'),
    capabilities.map((capability) =>
      noteEntry(
        element,
        `${t('不支持')}：${capability}`,
        t('本子集未实现该能力，后端会显式拒绝部署（校验规则「不支持的能力」）。请移除后再部署。'),
      ),
    ),
  );
}

// ─────────────────────────── 小工具 ───────────────────────────
