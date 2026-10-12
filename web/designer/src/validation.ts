/**
 * 即时校验（开发计划 §6.2 第 3 项）。
 *
 * ⚠️ 这里**镜像后端** `ProcessValidationRules.cs` 的 9 条规则（外加两条子集边界的补充检查），
 * 目的是让用户在部署**之前**就看到会被拒绝的原因。它是**便利设施，不是权威**：
 * 真正的裁决永远在后端 `ProcessValidator`，因此本文件的规则一旦与后端不一致，
 * 应以「后端报错」为准并修这里（测试见 tests/NetBpm.UnitTests/ProcessValidatorTests.cs）。
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
  'bpmn:UserTask': '用户任务',
  'bpmn:ServiceTask': '服务任务',
  'bpmn:ManualTask': '手工任务',
  'bpmn:ScriptTask': '脚本任务',
  'bpmn:ExclusiveGateway': '排他网关',
  'bpmn:ParallelGateway': '并行网关',
};

/** 提示语里可出现的全部元素名（含顺序流与流程本身）。 */
const TYPE_LABELS: Record<string, string> = {
  ...NODE_LABELS,
  'bpmn:SequenceFlow': '顺序流',
  'bpmn:Process': '流程',
};

const NODE_TYPES = new Set(Object.keys(NODE_LABELS));

const IMPLEMENTATION_KEYS = ['class', 'delegateExpression', 'expression'] as const;

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

    const outgoing = node.outgoing ?? [];
    const incoming = node.incoming ?? [];

    // 规则 1：除结束事件外必须有出流
    if (type !== 'bpmn:EndEvent' && outgoing.length === 0) {
      issues.push(error(id, `${label(node)} 没有出流。`));
    }

    // 规则 3：除开始事件外必须有人流
    if (type !== 'bpmn:StartEvent' && incoming.length === 0) {
      issues.push(error(id, `${label(node)} 没有入流（孤立节点）。`));
    }
  }

  issues.push(...validateGateways(flowElements));
  issues.push(...validateTasks(flowElements));
  issues.push(...validateSequenceFlows(sequenceFlows));
  issues.push(...validateUnsupportedCapabilities(flowElements, process));

  return issues;
}

function validateGateways(flowElements: ModdleElement[]): DraftIssue[] {
  const issues: DraftIssue[] = [];

  for (const node of flowElements) {
    const id = node.id ?? '';
    const outgoing = node.outgoing ?? [];
    const defaultFlow = typeof node.default === 'string' ? node.default : undefined;

    if (node.$type === 'bpmn:ExclusiveGateway') {
      // 规则 2：无默认流时不能所有出流都带条件（否则可能出现一条都选不中）
      const allConditional =
        outgoing.length > 0 && outgoing.every((flow) => hasCondition(flow));

      if (defaultFlow === undefined && allConditional) {
        issues.push(
          error(id, '排他网关没有默认流，且所有出流都带条件：条件全不成立时流程会卡住。请指定默认流或留一条无条件出流。'),
        );
      }
    }

    if (node.$type === 'bpmn:ParallelGateway') {
      // 规则 5：并行网关必须有入流与出流，且不得有默认流
      if (outgoing.length === 0) {
        issues.push(error(id, '并行网关没有出流。'));
      }

      if ((node.incoming ?? []).length === 0) {
        issues.push(error(id, '并行网关没有入流。'));
      }

      if (defaultFlow !== undefined) {
        issues.push(error(id, '并行网关不支持默认流。'));
      }
    }
  }

  return issues;
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
    }

    if (node.$type === 'bpmn:ScriptTask' && !isSet(node.script)) {
      issues.push(warning(id, '脚本任务没有脚本正文，执行时不会做任何事。'));
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

/** 规则 9 + 事件定义（子集外元素，后端目前会静默忽略）。 */
function validateUnsupportedCapabilities(
  flowElements: ModdleElement[],
  process: ModdleElement,
): DraftIssue[] {
  const issues: DraftIssue[] = [];
  const candidates = [...flowElements, process];

  for (const element of candidates) {
    const id = element.id ?? '';
    const extensions = element.extensionElements?.values ?? [];

    for (const extension of extensions) {
      if (extension.$type === 'activiti:TaskListener') {
        issues.push(error(id, `${label(element)} 带有任务监听器（activiti:taskListener），本子集未实现，部署会被拒绝。`));
      }

      if (extension.$type === 'activiti:ExecutionListener') {
        issues.push(error(id, `${label(element)} 带有执行监听器（activiti:executionListener），本子集未实现，部署会被拒绝。`));
      }

      if (extension.$type === 'activiti:Field') {
        issues.push(error(id, `${label(element)} 带有字段注入（activiti:field），本子集未实现，部署会被拒绝。`));
      }
    }

    if ((element.eventDefinitions ?? []).length > 0) {
      issues.push(
        error(id, `${label(element)} 带有事件定义（定时/消息/信号等），本子集不支持；请改用空开始/结束事件。`),
      );
    }
  }

  return issues;
}

// ─────────────────────────── 小工具 ───────────────────────────

function hasCondition(flow: ModdleElement): boolean {
  const body = flow.conditionExpression?.body;

  return body !== undefined && String(body).trim().length > 0;
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
