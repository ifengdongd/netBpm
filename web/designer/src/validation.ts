/**
 * 即时校验（开发计划 §6.2 第 3 项）。
 *
 * ⚠️ 这里**镜像后端** `ProcessValidationRules.cs` / `StructureRules.cs` / `ExpressionRules.cs`
 * 的部署期规则（外加少量面板便利检查），目的是让用户在部署**之前**就看到会被拒绝的原因。
 * 它是**便利设施，不是权威**：真正的裁决永远在后端 `ProcessValidator`，因此本文件的规则
 * 一旦与后端不一致，应以「后端报错」为准并修这里
 * （测试见 tests/NetBpm.UnitTests/ProcessValidatorTests.cs）。
 */
import type { ElementRegistryService, ModdleElement } from './designer/types';

export interface DraftIssue {
  severity: 'error' | 'warning';
  elementId: string;
  message: string;
}

const NODE_LABELS: Record<string, string> = {
  'bpmn:StartEvent': '开始事件',
  'bpmn:EndEvent': '结束事件',
  'bpmn:BoundaryEvent': '边界事件',
  'bpmn:UserTask': '用户任务',
  'bpmn:ServiceTask': '服务任务',
  'bpmn:ManualTask': '手工任务',
  'bpmn:ScriptTask': '脚本任务',
  'bpmn:ExclusiveGateway': '单一网关',
  'bpmn:ParallelGateway': '并行网关',
  'bpmn:InclusiveGateway': '包含网关',
  'bpmn:EventBasedGateway': '事件网关',
};

/** 提示语里可出现的全部元素名（含顺序流与流程本身）。 */
const TYPE_LABELS: Record<string, string> = {
  ...NODE_LABELS,
  'bpmn:SequenceFlow': '顺序流',
  'bpmn:Process': '流程',
};

const NODE_TYPES = new Set(Object.keys(NODE_LABELS));

const IMPLEMENTATION_KEYS = ['class', 'delegateExpression', 'expression'] as const;

/** ISO-8601 时长（PnYnMnDTnHnMnS），与后端 XmlConvert.ToTimeSpan 的口径一致。 */
const ISO_DURATION = /^P(?!$)(\d+Y)?(\d+M)?(\d+D)?(T(?=\d)(\d+H)?(\d+M)?(\d+(\.\d+)?S)?)?$/;

/** ISO-8601 时间点（必须带时间与时区/UTC 标记）。 */
const ISO_DATE_TIME = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$/;

/** 校验整张图（逐流程）。 */
export function validateDiagram(registry: ElementRegistryService): DraftIssue[] {
  const processes = uniqueBusinessObjects(registry).filter((bo) => bo.$type === 'bpmn:Process');
  const issues: DraftIssue[] = [];

  for (const process of processes) {
    issues.push(...validateProcess(process));
  }

  return issues;
}

/** 是否有阻断部署的错误（警告不阻断）。 */
export function hasBlockingError(issues: DraftIssue[]): boolean {
  return issues.some((issue) => issue.severity === 'error');
}

/** 状态栏文案。 */
export function summarize(issues: DraftIssue[]): string {
  if (issues.length === 0) {
    return '校验通过';
  }

  const errors = issues.filter((issue) => issue.severity === 'error').length;
  const warnings = issues.length - errors;

  return `校验：${errors} 个错误、${warnings} 个警告`;
}

function validateProcess(process: ModdleElement): DraftIssue[] {
  const issues: DraftIssue[] = [];
  const processId = process.id ?? '(未命名流程)';
  const flowElements = (process.flowElements ?? []).filter((element) => element.$type !== 'bpmn:SequenceFlow');
  const sequenceFlows = (process.flowElements ?? []).filter((element) => element.$type === 'bpmn:SequenceFlow');

  // ⚠️ 连通性从顺序流的 sourceRef/targetRef 解析：导入的 XML 未必带显式
  // <bpmn:outgoing>/<bpmn:incoming> 子元素（businessObject.outgoing 只有画布内
  // 新连线才会被 bpmn-js 维护），这里与后端"解析 XML 后重连"的语义保持一致。
  const outgoingByNode = new Map<string, ModdleElement[]>();
  const incomingByNode = new Map<string, ModdleElement[]>();

  for (const flow of sequenceFlows) {
    const sourceId = (flow.sourceRef as ModdleElement | undefined)?.id;
    const targetId = (flow.targetRef as ModdleElement | undefined)?.id;

    if (sourceId !== undefined) {
      outgoingByNode.set(sourceId, [...(outgoingByNode.get(sourceId) ?? []), flow]);
    }

    if (targetId !== undefined) {
      incomingByNode.set(targetId, [...(incomingByNode.get(targetId) ?? []), flow]);
    }
  }

  // 流程 id（key）：空 = 阻断；不符合标识符习惯 = 提示（Activiti 允许任意 id，后端同样只提示）
  if (typeof process.id !== 'string' || process.id.length === 0) {
    issues.push(error(processId, '流程 id（key）不能为空：启动实例与部署都按它定位流程。'));
  } else if (!/^[A-Za-z_][A-Za-z0-9_-]*$/.test(process.id)) {
    issues.push(
      warning(
        process.id,
        `流程 id '${process.id}' 不符合标识符习惯（建议字母/下划线开头，只含字母数字下划线与短横）：流程定义 id 形如 {key}:{version}:{id}。`,
      ),
    );
  }

  // 规则 4：id 非空且唯一（顺序流也参与，因为 id 是全局唯一的）
  const seen = new Map<string, number>();

  for (const element of process.flowElements ?? []) {
    const id = element.id;

    if (id === undefined || id.length === 0) {
      issues.push(error('', `${label(element)} 缺少 id。`));
      continue;
    }

    seen.set(id, (seen.get(id) ?? 0) + 1);
  }

  for (const [id, count] of seen) {
    if (count > 1) {
      issues.push(error(id, `id '${id}' 重复出现 ${count} 次，后端会拒绝部署。`));
    }
  }

  // 规则 6：恰好 1 个开始事件 + 至少 1 个结束事件
  const starts = flowElements.filter((element) => element.$type === 'bpmn:StartEvent');

  if (starts.length === 0) {
    issues.push(error(processId, '流程缺少开始事件。'));
  } else if (starts.length > 1) {
    issues.push(error(processId, `流程有 ${starts.length} 个开始事件；本子集只支持 1 个。`));
  }

  if (!flowElements.some((element) => element.$type === 'bpmn:EndEvent')) {
    issues.push(error(processId, '流程缺少结束事件。'));
  }

  for (const node of flowElements) {
    const type = node.$type;
    const id = node.id ?? '';

    if (!NODE_TYPES.has(type)) {
      issues.push(error(id, `${label(node)} 不在子集元素清单内，后端解析会直接报错。`));
      continue;
    }

    const outgoing = outgoingByNode.get(id) ?? [];
    const incoming = incomingByNode.get(id) ?? [];

    // 规则 1：除结束事件外必须有出流
    if (type !== 'bpmn:EndEvent' && outgoing.length === 0) {
      issues.push(error(id, `${label(node)} 没有出流。`));
    }

    // 规则 3：除开始事件与边界事件外必须有人流（边界事件由挂靠任务上的定时器触发）
    if (type !== 'bpmn:StartEvent' && type !== 'bpmn:BoundaryEvent' && incoming.length === 0) {
      issues.push(error(id, `${label(node)} 没有入流（孤立节点）。`));
    }
  }

  issues.push(...validateGateways(flowElements, outgoingByNode, incomingByNode));
  issues.push(...validateBoundaryEvents(flowElements));
  issues.push(...validateEndEvents(flowElements, outgoingByNode));
  issues.push(...validateTasks(flowElements));
  issues.push(...validateSequenceFlows(sequenceFlows));
  issues.push(...validateUnsupportedCapabilities(flowElements, process));

  return issues;
}

function validateGateways(
  flowElements: ModdleElement[],
  outgoingByNode: Map<string, ModdleElement[]>,
  incomingByNode: Map<string, ModdleElement[]>,
): DraftIssue[] {
  const issues: DraftIssue[] = [];

  for (const node of flowElements) {
    const id = node.id ?? '';
    const outgoing = outgoingByNode.get(id) ?? [];
    const incoming = incomingByNode.get(id) ?? [];
    // default 是引用属性：导入后是 SequenceFlow 对象（取 id），防御历史数据里的字符串形式
    const defaultFlow =
      typeof node.default === 'string'
        ? node.default
        : typeof (node.default as { id?: unknown } | undefined)?.id === 'string'
          ? String((node.default as { id: string }).id)
          : undefined;

    // 默认流必须指向本节点的出流；默认流上的条件不会生效（与后端 default-flow 规则一致）
    if (defaultFlow !== undefined) {
      const target = outgoing.find((flow) => flow.id === defaultFlow);

      if (target === undefined) {
        issues.push(
          error(id, `默认流 '${defaultFlow}' 不是该节点的出流（或 id 拼写错误）：运行期永远不会选中它。`),
        );
      } else if (hasCondition(target)) {
        issues.push(
          warning(id, `默认流 '${defaultFlow}' 上写了条件：Activiti 语义下默认流不参与条件判定，该条件不会生效。`),
        );
      }
    }

    if (node.$type === 'bpmn:ExclusiveGateway') {
      // 规则 2：无默认流时不能所有出流都带条件（否则可能出现一条都选不中）
      const allConditional =
        outgoing.length > 0 && outgoing.every((flow) => hasCondition(flow));

      if (defaultFlow === undefined && allConditional) {
        issues.push(
          error(id, '单一网关没有默认流，且所有出流都带条件：条件全不成立时流程会卡住。请指定默认流或留一条无条件出流。'),
        );
      }
    }

    if (node.$type === 'bpmn:InclusiveGateway') {
      // 包含网关：fork 取「所有条件为真的出流」，条件全假时同样会卡住
      const allConditional =
        outgoing.length > 0 && outgoing.every((flow) => hasCondition(flow));

      if (defaultFlow === undefined && allConditional) {
        issues.push(
          error(id, '包含网关没有默认流，且所有出流都带条件：条件全不成立时流程会卡住。请指定默认流或留一条无条件出流。'),
        );
      }
    }

    if (node.$type === 'bpmn:ParallelGateway') {
      // 规则 5：并行网关必须有入流与出流，且不得有默认流
      if (outgoing.length === 0) {
        issues.push(error(id, '并行网关没有出流。'));
      }

      if (incoming.length === 0) {
        issues.push(error(id, '并行网关没有入流。'));
      }

      if (defaultFlow !== undefined) {
        issues.push(error(id, '并行网关不支持默认流。'));
      }
    }

    if (node.$type === 'bpmn:EventBasedGateway') {
      // 事件网关的每条出流只能指向「中间捕获事件」（定时/消息/信号），本子集未实现 ——
      // 与后端校验保持一致：显式报错，而不是部署后才在运行期炸。
      issues.push(
        error(id, '事件网关的后续必须是中间捕获事件（定时/消息/信号），本子集未实现，部署会被拒绝；请改用单一/包含网关。'),
      );
    }
  }

  return issues;
}

/**
 * 边界定时事件的配置：本子集只支持「挂在用户任务上的中断型一次性定时器」。
 * 与后端 `boundary-event-configuration` 规则逐条对应。
 */
function validateBoundaryEvents(flowElements: ModdleElement[]): DraftIssue[] {
  const issues: DraftIssue[] = [];
  const boundaries = flowElements.filter((element) => element.$type === 'bpmn:BoundaryEvent');

  for (const boundary of boundaries) {
    const id = boundary.id ?? '';
    const attachedTo = (boundary.attachedToRef as ModdleElement | undefined)?.id;

    if (attachedTo === undefined) {
      issues.push(error(id, '边界事件必须由「边界定时器」工具创建并挂到用户任务上（attachedToRef 缺失）。'));
      continue;
    }

    const host = flowElements.find((element) => element.id === attachedTo);

    if (host === undefined) {
      issues.push(error(id, `边界事件的 attachedToRef='${attachedTo}' 解析不到任何活动。`));
      continue;
    }

    if (host.$type !== 'bpmn:UserTask') {
      issues.push(
        error(id, `边界事件只支持挂在用户任务上（当前挂在 ${label(host)} '${attachedTo}'）：服务/脚本任务同步执行，定时器没有意义。`),
      );
    } else if (host.loopCharacteristics !== undefined) {
      issues.push(error(id, '会签/串签（多实例）节点暂不支持边界定时器：实例级超时会与会签计数脱节，请把超时放到会签外层。'));
    }

    if (boundary.cancelActivity === false) {
      issues.push(error(id, '不支持非中断型（cancelActivity=false）边界事件：本子集只实现「到点取消任务并转入边界分支」。'));
    }

    const definitions = (boundary.eventDefinitions ?? []) as ModdleElement[];

    if (definitions.length === 0) {
      issues.push(error(id, '边界事件没有 timerEventDefinition，部署会被拒绝。'));
      continue;
    }

    if (definitions.length > 1) {
      issues.push(error(id, '一个边界事件只能有一个事件定义（多个定时器请拆成多个边界事件）。'));
    }

    const timer = definitions.find((definition) => definition.$type === 'bpmn:TimerEventDefinition');

    if (timer === undefined) {
      issues.push(error(id, '边界事件只支持 timerEventDefinition（定时器），其它事件定义不在子集内。'));
      continue;
    }

    const duration = timerText(timer.timeDuration);
    const date = timerText(timer.timeDate);
    const cycle = timerText(timer.timeCycle);
    const configured = [duration, date, cycle].filter((value) => value !== undefined);

    if (configured.length === 0) {
      issues.push(error(id, '定时器必须指定 timeDuration 或 timeDate 之一。'));
    }

    if (configured.length > 1) {
      issues.push(error(id, '定时器只能三选一：timeDuration / timeDate / timeCycle。'));
    }

    if (cycle !== undefined) {
      issues.push(error(id, '不支持 timeCycle（重复/周期定时）：请改用 timeDuration 或 timeDate。'));
    }

    if (duration !== undefined && !looksLikeExpression(duration) && !ISO_DURATION.test(duration)) {
      issues.push(error(id, `timeDuration '${duration}' 不是合法 ISO-8601 时长（示例：PT30M、PT2H、P1D）。`));
    }

    if (
      date !== undefined
      && !looksLikeExpression(date)
      && (!ISO_DATE_TIME.test(date) || Number.isNaN(Date.parse(date)))
    ) {
      issues.push(error(id, `timeDate '${date}' 不是合法 ISO-8601 时间点（示例：2026-10-20T09:00:00Z）。`));
    }
  }

  return issues;
}

/** 终止结束事件不得有出流（与后端 `terminate-end-event` 一致）。 */
function validateEndEvents(
  flowElements: ModdleElement[],
  outgoingByNode: Map<string, ModdleElement[]>,
): DraftIssue[] {
  const issues: DraftIssue[] = [];

  for (const endEvent of flowElements.filter((element) => element.$type === 'bpmn:EndEvent')) {
    const terminates = ((endEvent.eventDefinitions ?? []) as ModdleElement[]).some(
      (definition) => definition.$type === 'bpmn:TerminateEventDefinition',
    );

    if (terminates && (outgoingByNode.get(endEvent.id ?? '') ?? []).length > 0) {
      issues.push(error(endEvent.id ?? '', '终止结束事件不能有出流：它会立即终止整个流程实例，后续节点永远不会执行。'));
    }
  }

  return issues;
}

function timerText(value: unknown): string | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  const text = typeof value === 'string' ? value : String((value as ModdleElement).body ?? '');

  return text.trim().length === 0 ? undefined : text.trim();
}

function validateTasks(flowElements: ModdleElement[]): DraftIssue[] {
  const issues: DraftIssue[] = [];

  for (const node of flowElements) {
    const id = node.id ?? '';

    if (node.$type === 'bpmn:UserTask') {
      // ⚠️ 无人办理的任务<b>不再报错</b>：Activiti 允许（任务照常创建，只是没人认领），
      // 后端校验也已同步放宽（见 ProcessValidationRules.RequiredAttributesRule 的 remarks）。
      // 这里只提示 —— 它将无法按办理人/候选人查到，只能按流程实例查。
      const hasAssignee = isSet(node.assignee);
      const hasCandidates = isSet(node.candidateUsers) || isSet(node.candidateGroups);

      if (!hasAssignee && !hasCandidates) {
        issues.push(warning(id, '该用户任务没有办理人或候选人：任务会被创建但没人认领，只能按流程实例查询。'));
      }

      // 面板补充校验：优先级必须是整数
      if (isSet(node.priority) && !/^-?\d+$/.test(String(node.priority))) {
        issues.push(warning(id, `优先级 '${String(node.priority)}' 不是整数。`));
      }

      // 表单 enum 选项 id 必须是数字：后端按整数比较（result == 1），"1" 之外的 id 永远不匹配
      const formProperties = (node.extensionElements?.values ?? []).filter(
        (value) => value.$type === 'activiti:FormProperty' && String(value.type ?? '') === 'enum',
      );

      for (const formProperty of formProperties) {
        const values = (formProperty.value as ModdleElement[] | undefined) ?? [];

        for (const option of values) {
          if (!/^\d+$/.test(String(option.id ?? ''))) {
            issues.push(
              warning(
                id,
                `表单属性 '${String(formProperty.id)}' 的枚举选项 id '${String(option.id ?? '')}' 不是数字：审批条件按整数比较（如 \${result == 1}），该选项永远不会命中。`,
              ),
            );
          }
        }
      }

      // 多实例（会签 / 串签）：镜像后端 multi-instance 规则
      const mi = node.loopCharacteristics as ModdleElement | undefined;

      if (mi !== undefined && mi !== null) {
        const cardinality = expressionBody(mi.loopCardinality);
        const collection = isSet(mi.collection) ? String(mi.collection).trim() : undefined;

        if (cardinality === undefined && collection === undefined) {
          issues.push(error(id, '多实例必须指定实例数来源：loopCardinality 或 collection 之一，后端会拒绝部署。'));
        }

        if (cardinality !== undefined && collection !== undefined) {
          issues.push(warning(id, '多实例同时设置了 loopCardinality 与 collection：建议只保留一个（二选一）。'));
        }

        if (collection !== undefined && !isSet(mi.elementVariable)) {
          issues.push(warning(id, '使用集合（collection）时未指定元素变量（elementVariable）：实例之间无法区分当前元素。'));
        }

        if (collection !== undefined && !looksLikeExpression(collection)) {
          issues.push(warning(id, `集合 '${collection}' 未写成 \${...} 形式，后端不会把它当作表达式求值。`));
        }

        const completionCondition = expressionBody(mi.completionCondition);

        if (completionCondition !== undefined && !looksLikeExpression(completionCondition)) {
          issues.push(
            warning(id, `完成条件 '${completionCondition}' 未写成 \${...} 形式，后端不会把它当作表达式求值。`),
          );
        }

        // 后端 multi-instance 规则：skipExpression 与多实例语义冲突，部署期拒绝
        if (isSet(node.skipExpression)) {
          issues.push(error(id, '多实例用户任务不支持 skipExpression（与实例计数语义冲突），后端会拒绝部署。'));
        }
      }
    }

    if (node.$type === 'bpmn:ScriptTask') {
      if (!isSet(node.script)) {
        issues.push(error(id, '脚本任务必须有 <script> 正文，后端会拒绝部署。'));
      }

      const format = String(node.scriptFormat ?? '').trim();

      if (!/^(csharp|c#)$/i.test(format)) {
        issues.push(
          error(id, `脚本任务只支持 scriptFormat="csharp"（实际：'${format || '(未指定)'}'），后端会拒绝部署。`),
        );
      }
    }

    if (node.$type === 'bpmn:ServiceTask') {
      const implementations = IMPLEMENTATION_KEYS.filter((key) => isSet(node[key]));

      // 规则 8（服务任务）：实现方式至少一个
      if (implementations.length === 0) {
        issues.push(error(id, '服务任务必须指定 class / delegateExpression / expression 之一。'));
      } else if (implementations.length > 1) {
        // 面板补充校验：后端按 class > delegateExpression > expression 静默取值
        issues.push(
          warning(id, `服务任务同时设置了 ${implementations.join(' / ')}；后端只按 class > delegateExpression > expression 取第一个，建议只保留一个。`),
        );
      }
    }
  }

  return issues;
}

function validateSequenceFlows(sequenceFlows: ModdleElement[]): DraftIssue[] {
  const issues: DraftIssue[] = [];

  for (const flow of sequenceFlows) {
    const id = flow.id ?? '';

    // 面板补充校验：条件表达式必须是 ${...} / #{...}，否则后端按字面量处理
    const body = flow.conditionExpression?.body;

    if (body !== undefined && String(body).length > 0 && !/^[$#]\{.*\}$/.test(String(body).trim())) {
      issues.push(warning(id, `条件 '${String(body)}' 未写成 \${...} 形式，后端不会把它当作表达式求值。`));
    }
  }

  return issues;
}

/** 认识但**本子集不支持**的元素（与后端 BpmnXmlConverter.UnsupportedElements 一一对应）。 */
export const UNSUPPORTED_ELEMENTS: Record<string, string> = {
  'bpmn:SubProcess': '子流程',
  'bpmn:AdHocSubProcess': '临时子流程',
  'bpmn:Transaction': '事务子流程',
  'bpmn:CallActivity': '调用活动',
  'bpmn:ReceiveTask': '接收任务',
  'bpmn:SendTask': '发送任务',
  'bpmn:BusinessRuleTask': '业务规则任务',
  'bpmn:IntermediateCatchEvent': '中间捕获事件',
  'bpmn:IntermediateThrowEvent': '中间抛出事件',
  'bpmn:ComplexGateway': '复杂网关',
};

/** 规则 9 + 事件定义 + 不支持的元素 + 未实现的任务监听器事件（后端一律显式拒绝）。 */
function validateUnsupportedCapabilities(
  flowElements: ModdleElement[],
  process: ModdleElement,
): DraftIssue[] {
  const issues: DraftIssue[] = [];
  const candidates = [...flowElements, process];

  for (const element of candidates) {
    const id = element.id ?? '';

    // 子流程 / 调用活动这类"有语义但不做"的元素：后端 unsupported-element 规则会拒绝，
    // 这里同步提示 —— 否则建模者只在部署时才知道，且拿到的是「XML 坏了」形态的解析错误。
    const capability = UNSUPPORTED_ELEMENTS[element.$type ?? ''];

    if (capability) {
      issues.push(
        error(
          id,
          `${label(element)} 是${capability}（${(element.$type ?? '').replace('bpmn:', '')}）：` +
            '不在本子集支持范围内，部署会被拒绝。',
        ),
      );
    }

    const extensions = element.extensionElements?.values ?? [];

    for (const extension of extensions) {
      // 监听器（执行 / 任务 / 事件）与表单属性已由后端支持：
      //   监听器由引擎执行（ListenerInvoker / EngineEventDispatcher），
      //   表单属性是审批应用层的表单契约（WorkflowFormMapper）—— 均不再告警。
      // 仍要拦截的只有字段注入与事件定义（子集外能力）。
      if (extension.$type === 'activiti:Field') {
        issues.push(error(id, `${label(element)} 带有字段注入（activiti:field），本子集未实现，部署会被拒绝。`));
      }

      // delete 事件尚未实现（任务删除的各条路径都没有通知点），后端 listener-configuration 会拒绝
      if (extension.$type === 'activiti:TaskListener' && String(extension.event ?? '').toLowerCase() === 'delete') {
        issues.push(
          error(id, `${label(element)} 配置了 taskListener event="delete"：该事件尚未实现，部署会被拒绝；请改用 complete 事件或在宿主侧处理。`),
        );
      }
    }

    // 已支持的事件定义：结束事件的 terminate、边界事件的 timer；其余仍是子集外
    for (const definition of (element.eventDefinitions ?? []) as ModdleElement[]) {
      const supported =
        (element.$type === 'bpmn:EndEvent' && definition.$type === 'bpmn:TerminateEventDefinition')
        || (element.$type === 'bpmn:BoundaryEvent' && definition.$type === 'bpmn:TimerEventDefinition');

      if (!supported) {
        issues.push(
          error(
            id,
            `${label(element)} 带有不支持的事件定义 ${definition.$type.replace('bpmn:', '')} ` +
              '（只支持结束事件的 terminate 与边界事件的 timer）。',
          ),
        );
      }
    }
  }

  return issues;
}

// ─────────────────────────── 小工具 ───────────────────────────

function hasCondition(flow: ModdleElement): boolean {
  const body = flow.conditionExpression?.body;

  return body !== undefined && String(body).trim().length > 0;
}

/** 读取 Expression 型子元素的正文（loopCardinality / completionCondition）。 */
function expressionBody(value: unknown): string | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value === 'string') {
    return value.trim().length === 0 ? undefined : value;
  }

  const body = (value as ModdleElement).body;

  return body === undefined || body === null || String(body).trim().length === 0 ? undefined : String(body);
}

/** 是否为 ${...} / #{...} 形式（后端按字面量处理的判定口径）。 */
function looksLikeExpression(value: string): boolean {
  return /^[$#]\{.*\}$/.test(value.trim());
}

function isSet(value: unknown): boolean {
  return value !== undefined && value !== null && String(value).trim().length > 0;
}

function label(element: ModdleElement): string {
  const name = TYPE_LABELS[element.$type] ?? element.$type;

  return element.id ? `${name} '${element.id}'` : name;
}

function error(elementId: string, message: string): DraftIssue {
  return { severity: 'error', elementId, message };
}

function warning(elementId: string, message: string): DraftIssue {
  return { severity: 'warning', elementId, message };
}

/** 同一 business object 在注册表里可能出现多次（图形 + 连线），按引用去重。 */
function uniqueBusinessObjects(registry: ElementRegistryService): ModdleElement[] {
  const seen = new Set<ModdleElement>();
  const result: ModdleElement[] = [];

  for (const element of registry.getAll()) {
    const bo = element.businessObject;

    if (bo === undefined || seen.has(bo)) {
      continue;
    }

    seen.add(bo);
    result.push(bo);
  }

  return result;
}
