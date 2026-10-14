/**
 * 设计器单元测试（开发计划 P8）。
 *
 * 运行方式见 package.json 的 `npm test`：先用 vite 自带的 esbuild 打包（顺带把浏览器专用的
 * properties-panel 换成替身），再交给 Node 内置的 `node --test` —— **不新增任何依赖**。
 *
 * 覆盖两类逻辑，它们都是"编译通过 ≠ 行为正确"的部分：
 *  1. 属性面板的属性映射：面板能写哪些属性、写到哪个 XML 名、空值是否真的清除了属性；
 *  2. 即时校验：本地镜像的后端规则是否真的会拦下会被后端拒绝的图。
 */
import assert from 'node:assert/strict';
import { test } from 'node:test';

import { BpmnModdle } from 'bpmn-moddle';

import activitiModdle from '../src/activiti-moddle.json';
import { implementationFields } from '../src/designer/listEditors';
import NetBpmPropertiesProvider from '../src/designer/propertiesProvider';
import { parseProcessMeta, upsertDiagram, type DiagramRecord } from '../src/storage';
import { hasBlockingError, summarize, validateDiagram } from '../src/validation';
import type { StubEntry, StubGroup } from './propertiesPanelStub';

// ─────────────────────────── 测试脚手架 ───────────────────────────

interface Harness {
  provider: NetBpmPropertiesProvider;
  registered: { priority: number; provider: unknown }[];
  updates: { element: unknown; properties: Record<string, unknown> }[];
}

function createHarness(): Harness {
  const registered: { priority: number; provider: unknown }[] = [];
  const updates: { element: unknown; properties: Record<string, unknown> }[] = [];

  const provider = new NetBpmPropertiesProvider(
    {
      registerProvider: (priority: number, instance: unknown) => registered.push({ priority, provider: instance }),
    } as never,
    {
      updateProperties: (element: unknown, properties: Record<string, unknown>) =>
        updates.push({ element, properties }),
    } as never,
    { create: (type: string, properties?: Record<string, unknown>) => ({ $type: type, ...properties }) } as never,
    (text: string) => text,
  );

  return { provider, registered, updates };
}

function userTask(businessObject: Record<string, unknown> = {}): never {
  return {
    id: 'approve',
    type: 'bpmn:UserTask',
    businessObject: { $type: 'bpmn:UserTask', id: 'approve', name: '审批', ...businessObject },
  } as never;
}

function groupsOf(harness: Harness, element: unknown): StubGroup[] {
  return harness.provider.getGroups(element as never)([]) as StubGroup[];
}

function groupById(groups: StubGroup[], id: string): StubGroup {
  const group = groups.find((candidate) => candidate.id === id);

  assert.ok(group, `没有找到属性组 ${id}（实际：${groups.map((g) => g.id).join(', ')}）`);

  return group;
}

function entryById(group: StubGroup, id: string): StubEntry {
  const entry = group.entries.find((candidate) => candidate.id === id);

  assert.ok(entry, `组 ${group.id} 里没有条目 ${id}（实际：${group.entries.map((e) => e.id).join(', ')}）`);

  return entry;
}

/** 构造一个只含 businessObject 的注册表替身。 */
function registryOf(elements: Record<string, unknown>[]): never {
  return {
    getAll: () => elements.map((businessObject) => ({ id: businessObject.id, type: businessObject.$type, businessObject })),
  } as never;
}

// ─────────────────────────── 属性面板：注册与分组 ───────────────────────────

test('构造时把自身注册为属性提供者', () => {
  const harness = createHarness();

  assert.equal(harness.registered.length, 1);
  assert.equal(harness.registered[0].provider, harness.provider);
});

test('用户任务：只渲染白名单属性（未连线时没有默认流下拉）', () => {
  const harness = createHarness();
  const groups = groupsOf(harness, userTask());

  assert.deepEqual(
    groupById(groups, 'netbpm-user-task').entries.map((entry) => entry.id),
    ['assignee', 'owner', 'candidateUsers', 'candidateGroups', 'formKey', 'dueDate', 'priority', 'category', 'skipExpression'],
  );
});

test('顺序流：只渲染条件表达式（不放行子集不支持的属性）', () => {
  const harness = createHarness();

  const groups = groupsOf(harness, {
    id: 'f1',
    type: 'bpmn:SequenceFlow',
    businessObject: { $type: 'bpmn:SequenceFlow', id: 'f1' },
  });

  assert.deepEqual(groups.map((group) => group.id), [
    'netbpm-common',
    'netbpm-sequence-flow',
    'netbpm-sequence-flow-exec-listeners',
  ]);
  assert.deepEqual(groupById(groups, 'netbpm-sequence-flow').entries.map((entry) => entry.id), ['conditionExpression']);
});

test('子集外元素不渲染任何属性组', () => {
  const harness = createHarness();

  assert.deepEqual(
    groupsOf(harness, { id: 'sub', type: 'bpmn:SubProcess', businessObject: { $type: 'bpmn:SubProcess', id: 'sub' } }),
    [],
  );
});

// ─────────────────────────── 属性面板：读写行为 ───────────────────────────

test('文本属性：写入时去掉首尾空白，空值清除属性', () => {
  const harness = createHarness();
  const element = userTask({ assignee: 'kermit' });
  const assignee = entryById(groupById(groupsOf(harness, element), 'netbpm-user-task'), 'assignee');

  assert.equal(assignee.getValue?.(element), 'kermit');

  assignee.setValue?.('  gonzo  ');
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: 'gonzo' });

  // 清空 ⇒ undefined（不是空字符串）：否则 XML 会写出 activiti:assignee=""
  assignee.setValue?.('   ');
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: undefined });

  assignee.setValue?.(undefined);
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: undefined });
});

test('服务任务：类名/表达式/代理的表达式三个字段平铺（对齐 Activiti 原版）', () => {
  const harness = createHarness();
  const element = {
    id: 'notify',
    type: 'bpmn:ServiceTask',
    businessObject: { $type: 'bpmn:ServiceTask', id: 'notify', class: 'NetBpm.Demo.Notify' },
  };

  const entries = groupById(groupsOf(harness, element), 'netbpm-service-task').entries.map((entry) => entry.id);

  assert.deepEqual(
    entries,
    ['impl-class', 'impl-expression', 'impl-delegateExpression', 'resultVariableName', 'note-notify-默认流'],
  );

  // 类名条目读写 class 属性
  const classEntry = entryById(groupById(groupsOf(harness, element), 'netbpm-service-task'), 'impl-class');
  assert.equal(classEntry.getValue?.(element), 'NetBpm.Demo.Notify');
});

test('默认流下拉：选项来自形状级出流连线；写入必须是引用对象（回归）', () => {
  // 回归：default 是 BPMN 描述符里的引用属性（isReference），曾按字符串 id 写入，
  // saveXML 会输出 default="undefined" —— 保存后默认流直接失效。
  const harness = createHarness();
  const toConnection = (id: string, name?: string): never =>
    ({
      id,
      type: 'bpmn:SequenceFlow',
      businessObject: { $type: 'bpmn:SequenceFlow', id, ...(name ? { name } : {}) },
    }) as never;
  const flowBo = { $type: 'bpmn:SequenceFlow', id: 'f2', name: '同意' };
  const element: never = {
    id: 'approve',
    type: 'bpmn:UserTask',
    businessObject: { $type: 'bpmn:UserTask', id: 'approve', name: '审批' },
    outgoing: [{ id: 'f2', type: 'bpmn:SequenceFlow', businessObject: flowBo }, toConnection('f3')],
  } as never;

  const group = groupById(groupsOf(harness, element), 'netbpm-user-task');
  const defaultFlow = entryById(group, 'defaultFlow');

  assert.deepEqual(defaultFlow.getOptions?.(element).map((option) => option.value), ['', 'f2', 'f3']);

  // 读取：导入后是引用对象，取其 id；历史字符串形式也要能读
  (element as unknown as { businessObject: Record<string, unknown> }).businessObject.default = flowBo;
  assert.equal(defaultFlow.getValue?.(element), 'f2');

  (element as unknown as { businessObject: Record<string, unknown> }).businessObject.default = 'f3';
  assert.equal(defaultFlow.getValue?.(element), 'f3');

  // 写入：必须是出流的 businessObject 对象（moddle 引用序列化只认对象）
  defaultFlow.setValue?.('f2');
  assert.deepEqual(harness.updates.at(-1)?.properties, { default: flowBo });

  defaultFlow.setValue?.('');
  assert.deepEqual(harness.updates.at(-1)?.properties, { default: undefined });
});

test('条件表达式：写入 moddle 子元素，留空则删除', () => {
  const harness = createHarness();
  const element = {
    id: 'f1',
    type: 'bpmn:SequenceFlow',
    businessObject: { $type: 'bpmn:SequenceFlow', id: 'f1', conditionExpression: { body: '${approved}' } },
  };
  const condition = entryById(groupById(groupsOf(harness, element), 'netbpm-sequence-flow'), 'conditionExpression');

  assert.equal(condition.getValue?.(element), '${approved}');

  condition.setValue?.('${amount > 100}');
  assert.deepEqual(harness.updates.at(-1)?.properties, {
    conditionExpression: { $type: 'bpmn:FormalExpression', body: '${amount > 100}' },
  });

  condition.setValue?.('');
  assert.deepEqual(harness.updates.at(-1)?.properties, { conditionExpression: undefined });
});

test('包含网关：提供默认流下拉；事件网关只给说明', () => {
  const harness = createHarness();
  const inclusive = {
    id: 'inclGw',
    type: 'bpmn:InclusiveGateway',
    businessObject: { $type: 'bpmn:InclusiveGateway', id: 'inclGw' },
    outgoing: [
      { id: 'flowA', type: 'bpmn:SequenceFlow', businessObject: { $type: 'bpmn:SequenceFlow', id: 'flowA' } },
    ],
  };

  const inclusiveGroup = groupById(groupsOf(harness, inclusive), 'netbpm-inclusive-gateway');
  assert.ok(inclusiveGroup.entries.some((entry) => entry.id === 'defaultFlow'));

  const eventGroup = groupById(
    groupsOf(harness, {
      id: 'evtGw',
      type: 'bpmn:EventBasedGateway',
      businessObject: { $type: 'bpmn:EventBasedGateway', id: 'evtGw' },
    }),
    'netbpm-event-gateway',
  );

  assert.ok(eventGroup.entries.every((entry) => entry.id.startsWith('note-')));
});

test('并行网关：不提供默认流，只给出说明', () => {
  const harness = createHarness();
  const element = { id: 'fork', type: 'bpmn:ParallelGateway', businessObject: { $type: 'bpmn:ParallelGateway', id: 'fork' } };
  const group = groupById(groupsOf(harness, element), 'netbpm-parallel-gateway');

  assert.ok(group.entries[0].id.startsWith('note-'));
});

test('不支持的能力：只有事件定义/字段注入才渲染提示组（监听器已支持）', () => {
  const harness = createHarness();

  // 任务监听器已开放编辑（listEditors.ts），不再提示"不支持"
  const withListener = userTask({ extensionElements: { values: [{ $type: 'activiti:TaskListener', event: 'create' }] } });
  assert.ok(!groupsOf(harness, withListener).some((group) => group.id === 'netbpm-unsupported'));

  // 事件定义仍然是子集外能力
  const withEventDefinition = userTask({ eventDefinitions: [{ $type: 'bpmn:TimerEventDefinition' }] });
  const group = groupById(groupsOf(harness, withEventDefinition), 'netbpm-unsupported');

  assert.equal(group.entries.length, 1);
  assert.ok(group.entries.every((entry) => entry.id.startsWith('note-')));
});

// ─────────────────────────── 监听器 / 表单属性编辑组 ───────────────────────────

test('流程：提供执行监听器与事件监听器编辑组', () => {
  const harness = createHarness();
  const groups = groupsOf(harness, {
    id: 'process_1',
    type: 'bpmn:Process',
    businessObject: { $type: 'bpmn:Process', id: 'process_1' },
  });

  assert.deepEqual(
    groups.map((group) => group.id),
    ['netbpm-process', 'netbpm-process-exec-listeners', 'netbpm-event-listeners'],
  );
});

test('用户任务：提供执行/任务监听器与表单属性编辑组', () => {
  const harness = createHarness();
  const groupIds = groupsOf(harness, userTask()).map((group) => group.id);

  assert.deepEqual(groupIds, [
    'netbpm-common',
    'netbpm-user-task',
    'netbpm-user-task-exec-listeners',
    'netbpm-user-task-task-listeners',
    'netbpm-form-properties',
  ]);
});

test('顺序流：执行监听器组的事件只有 take', () => {
  const harness = createHarness();
  const groups = groupsOf(harness, {
    id: 'f1',
    type: 'bpmn:SequenceFlow',
    businessObject: { $type: 'bpmn:SequenceFlow', id: 'f1' },
  });

  assert.deepEqual(
    groups.map((group) => group.id),
    ['netbpm-common', 'netbpm-sequence-flow', 'netbpm-sequence-flow-exec-listeners'],
  );
});

// ─────────────────────────── 即时校验（镜像后端规则）───────────────────────────

test('校验：缺少结束事件与节点无出流都会被拦下', () => {
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [{ $type: 'bpmn:StartEvent', id: 'start', outgoing: [] }],
      },
    ]),
  );

  assert.ok(issues.some((issue) => issue.message.includes('缺少结束事件')));
  assert.ok(issues.some((issue) => issue.message.includes('开始事件') && issue.message.includes('没有出流')));
  assert.ok(hasBlockingError(issues));
});

test('校验：无人办理的用户任务只告警（Activiti 允许，后端不再拒绝部署）', () => {
  const withoutAssignee = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:StartEvent', id: 'start' },
          { $type: 'bpmn:UserTask', id: 'approve' },
          { $type: 'bpmn:EndEvent', id: 'end' },
          { $type: 'bpmn:SequenceFlow', id: 'f1', sourceRef: { id: 'start' }, targetRef: { id: 'approve' } },
        ],
      },
    ]),
  );

  const issued = withoutAssignee.filter((issue) => issue.message.includes('办理人'));

  assert.equal(issued.length, 1);
  assert.equal(issued[0].severity, 'warning');

  const withCandidate = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:UserTask', id: 'approve', candidateGroups: 'managers' },
        ],
      },
    ]),
  );

  assert.ok(!withCandidate.some((issue) => issue.message.includes('办理人')));
});

test('校验：服务任务实现方式缺失报错、重复只告警', () => {
  const missing = validateDiagram(
    registryOf([{ $type: 'bpmn:Process', id: 'p1', flowElements: [{ $type: 'bpmn:ServiceTask', id: 'svc' }] }]),
  );

  assert.ok(missing.some((issue) => issue.severity === 'error' && issue.message.includes('class / delegateExpression / expression')));

  const duplicated = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [{ $type: 'bpmn:ServiceTask', id: 'svc', class: 'A', expression: '${x}' }],
      },
    ]),
  );

  const duplicateIssue = duplicated.find((issue) => issue.message.includes('同时设置了'));

  assert.equal(duplicateIssue?.severity, 'warning');
});

test('校验：并行网关不得有默认流，排他网关不得"全条件且无默认流"', () => {
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:ParallelGateway', id: 'fork', default: 'f1' },
          {
            $type: 'bpmn:ExclusiveGateway',
            id: 'gw',
          },
          { $type: 'bpmn:SequenceFlow', id: 'f0', sourceRef: { id: 'start' }, targetRef: { id: 'fork' } },
          { $type: 'bpmn:SequenceFlow', id: 'f1', sourceRef: { id: 'fork' }, targetRef: { id: 'gw' } },
          { $type: 'bpmn:SequenceFlow', id: 'f2', sourceRef: { id: 'gw' }, targetRef: { id: 'a' }, conditionExpression: { body: '${a}' } },
          { $type: 'bpmn:SequenceFlow', id: 'f3', sourceRef: { id: 'gw' }, targetRef: { id: 'b' }, conditionExpression: { body: '${b}' } },
        ],
      },
    ]),
  );

  assert.ok(issues.some((issue) => issue.elementId === 'fork' && issue.message.includes('不支持默认流')));
  assert.ok(issues.some((issue) => issue.elementId === 'gw' && issue.message.includes('默认流')));
});

test('校验：监听器/表单属性已支持不再告警，事件定义与字段注入仍拦下部署', () => {
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          {
            $type: 'bpmn:ServiceTask',
            id: 'svc',
            class: 'A',
            extensionElements: {
              values: [
                { $type: 'activiti:ExecutionListener', event: 'start', class: 'A' },
                { $type: 'activiti:FormProperty', id: 'approvalResult' },
                { $type: 'activiti:Field', name: 'url' },
              ],
            },
          },
          { $type: 'bpmn:StartEvent', id: 'start', eventDefinitions: [{ $type: 'bpmn:TimerEventDefinition' }] },
        ],
      },
    ]),
  );

  // 监听器/表单属性已由后端支持，不再告警
  assert.ok(!issues.some((issue) => issue.message.includes('执行监听器')));
  assert.ok(!issues.some((issue) => issue.message.includes('表单属性')));

  // 字段注入 / 事件定义 → 错误，阻断
  assert.ok(issues.some((issue) => issue.severity === 'error' && issue.message.includes('字段注入')));
  assert.ok(issues.some((issue) => issue.severity === 'error' && issue.message.includes('事件定义')));
  assert.ok(hasBlockingError(issues));
});

test('校验：包含网关缺默认流且全条件时拦下；事件网关一并拦下', () => {
  const inclusiveIssues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:StartEvent', id: 'start' },
          { $type: 'bpmn:InclusiveGateway', id: 'inclGw' },
          { $type: 'bpmn:EndEvent', id: 'end' },
          { $type: 'bpmn:SequenceFlow', id: 'f1', sourceRef: { id: 'start' }, targetRef: { id: 'inclGw' } },
          { $type: 'bpmn:SequenceFlow', id: 'f2', sourceRef: { id: 'inclGw' }, targetRef: { id: 'end' }, conditionExpression: { body: '${a}' } },
        ],
      },
    ]),
  );

  assert.ok(inclusiveIssues.some((issue) => issue.elementId === 'inclGw' && issue.message.includes('包含网关')));
  assert.ok(hasBlockingError(inclusiveIssues));

  const eventIssues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:StartEvent', id: 'start' },
          { $type: 'bpmn:EventBasedGateway', id: 'evtGw' },
          { $type: 'bpmn:EndEvent', id: 'end' },
          { $type: 'bpmn:SequenceFlow', id: 'f1', sourceRef: { id: 'start' }, targetRef: { id: 'evtGw' } },
          { $type: 'bpmn:SequenceFlow', id: 'f2', sourceRef: { id: 'evtGw' }, targetRef: { id: 'end' } },
        ],
      },
    ]),
  );

  assert.ok(eventIssues.some((issue) => issue.elementId === 'evtGw' && issue.message.includes('中间捕获事件')));
  assert.ok(hasBlockingError(eventIssues));
});

test('校验：排他网关默认流以引用对象形式读取（回归）', () => {
  // 回归：导入后 default 是 SequenceFlow 引用对象而非字符串，
  // 曾因 typeof === 'string' 判断读不到，把「有默认流」误报成「没有默认流」。
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:StartEvent', id: 'start' },
          { $type: 'bpmn:ExclusiveGateway', id: 'gw', default: { $type: 'bpmn:SequenceFlow', id: 'f2' } },
          { $type: 'bpmn:EndEvent', id: 'end' },
          { $type: 'bpmn:SequenceFlow', id: 'f1', sourceRef: { id: 'gw' }, targetRef: { id: 'end' }, conditionExpression: { body: '${a}' } },
          { $type: 'bpmn:SequenceFlow', id: 'f2', sourceRef: { id: 'gw' }, targetRef: { id: 'end' }, conditionExpression: { body: '${b}' } },
        ],
      },
    ]),
  );

  assert.ok(!issues.some((issue) => issue.elementId === 'gw' && issue.message.includes('默认流')));
});

test('校验：条件未写成 ${...} 只告警，不阻断部署', () => {
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [{ $type: 'bpmn:SequenceFlow', id: 'f1', conditionExpression: { body: 'approved' } }],
      },
    ]),
  );

  const conditionIssues = issues.filter((issue) => issue.message.includes('条件'));

  assert.equal(conditionIssues.length, 1);
  assert.equal(conditionIssues[0].severity, 'warning');

  // 这条最简流程另外还会因为"缺开始/结束事件"报 2 个错误，但它们都是 error 而不是这条警告
  assert.equal(issues.filter((issue) => issue.severity === 'error').length, 2);
  assert.ok(hasBlockingError(issues));

  assert.equal(summarize([]), '校验通过');
  assert.equal(summarize([{ severity: 'warning', elementId: 'x', message: 'm' }]), '校验：0 个错误、1 个警告');
});

test('校验：重复 id 会被拦下', () => {
  const issues = validateDiagram(
    registryOf([
      {
        $type: 'bpmn:Process',
        id: 'p1',
        flowElements: [
          { $type: 'bpmn:StartEvent', id: 'dup', outgoing: [{ id: 'f1' }] },
          { $type: 'bpmn:EndEvent', id: 'dup', incoming: [{ id: 'f1' }] },
        ],
      },
    ]),
  );

  assert.ok(issues.some((issue) => issue.message.includes("'dup' 重复")));
});

// ─────────────────────────── 流程记录存储（storage.ts）───────────────────────────

test('存储：从 XML 解析 process 的 id 与 name，name 缺省退回 id', () => {
  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL">
  <bpmn:process id="leave_process" name="请假流程" isExecutable="true" />
</bpmn:definitions>`;

  assert.deepEqual(parseProcessMeta(xml), { processId: 'leave_process', name: '请假流程' });

  const withoutName = '<bpmn:process id=\'solo\' isExecutable="true" />';

  assert.deepEqual(parseProcessMeta(withoutName), { processId: 'solo', name: 'solo' });

  // 无 process 节点：调用方据此判定"不是有效的 BPMN 流程图"
  assert.equal(parseProcessMeta('<html><body></body></html>').processId, '');
});

test('存储：按 processId 覆盖更新，新记录排在最前，且不改变原数组', () => {
  const existing: DiagramRecord[] = [
    { modelId: 'm-a', processId: 'a', name: 'A', xml: '<a/>', updatedAt: '2026-01-01T00:00:00.000Z', version: 1 },
    { modelId: 'm-b', processId: 'b', name: 'B', xml: '<b/>', updatedAt: '2026-01-02T00:00:00.000Z', version: 1 },
  ];
  const snapshot = JSON.stringify(existing);

  const updated = upsertDiagram(existing, {
    modelId: 'm-a',
    processId: 'a',
    name: 'A2',
    xml: '<a2/>',
    updatedAt: '2026-01-03T00:00:00.000Z',
    version: 2,
  });

  // 覆盖后 a 只出现一次，且排到了最前
  assert.deepEqual(
    updated.map((record) => record.processId),
    ['a', 'b'],
  );
  assert.equal(updated[0].name, 'A2');

  const inserted = upsertDiagram(existing, {
    modelId: 'm-c',
    processId: 'c',
    name: 'C',
    xml: '<c/>',
    updatedAt: '2026-01-03T00:00:00.000Z',
    version: 1,
  });

  assert.deepEqual(
    inserted.map((record) => record.processId),
    ['c', 'a', 'b'],
  );

  // 原数组未被改动
  assert.equal(JSON.stringify(existing), snapshot);
});

// ─────────────────────────── moddle 往返（保存/重开回归）───────────────────────────

test('XML 往返：表单属性与监听器保存后重新载入仍出现在 extensionElements.values（回归）', async () => {
  // 回归：activiti-moddle.json 曾在 ExtensionElements 上声明具名属性（formProperty 等），
  // 导致导入时元素落到具名属性而非 values，属性面板（readItems 只读 values）
  // 保存后重新打开显示为空。修复后导入必须统一落入 values。
  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:activiti="http://activiti.org/bpmn"
                  id="Definitions_1" targetNamespace="http://netbpm/bpmn">
  <bpmn:process id="p1" isExecutable="true">
    <bpmn:startEvent id="start" />
    <bpmn:userTask id="approve" name="审批">
      <bpmn:extensionElements>
        <activiti:executionListener event="start" class="A" />
        <activiti:taskListener event="create" class="B" />
        <activiti:eventListener events="TASK_COMPLETED" delegateExpression="WorkFlowTaskCompletedListener" />
        <activiti:formProperty id="approvalResult" name="审批结果" type="enum" required="true">
          <activiti:value id="approved" name="同意" />
          <activiti:value id="rejected" name="驳回" />
        </activiti:formProperty>
        <activiti:formProperty id="remark" name="备注" type="string" expression="\${remark}" variable="remark" />
      </bpmn:extensionElements>
    </bpmn:userTask>
    <bpmn:endEvent id="end" />
  </bpmn:process>
</bpmn:definitions>`;

  const moddle = new BpmnModdle({ activiti: activitiModdle as never });

  // 导入：所有 activiti:* 子元素都必须落入 values（属性面板 readItems 的读取位置）
  const { rootElement } = await moddle.fromXML(xml);
  const process = rootElement.rootElements.find((element) => element.$type === 'bpmn:Process')!;
  const userTask = process.flowElements.find((element) => element.$type === 'bpmn:UserTask')!;
  const values = userTask.extensionElements?.values ?? [];

  assert.deepEqual(
    values.map((value) => value.$type),
    ['activiti:ExecutionListener', 'activiti:TaskListener', 'activiti:EventListener', 'activiti:FormProperty', 'activiti:FormProperty'],
  );

  const enumProperty = values[3];

  assert.equal(enumProperty.id, 'approvalResult');
  assert.equal(enumProperty.type, 'enum');
  assert.equal(enumProperty.required, true);
  assert.deepEqual(
    (enumProperty.value ?? []).map((option) => [option.id, option.name]),
    [['approved', '同意'], ['rejected', '驳回']],
  );

  // 导出：序列化不得丢失 activiti:* 扩展
  const { xml: exported } = await moddle.toXML(rootElement, { format: true });

  assert.ok(exported.includes('activiti:formProperty'));
  assert.ok(exported.includes('activiti:executionListener'));
  assert.ok(exported.includes('activiti:taskListener'));
  assert.ok(exported.includes('activiti:value'));

  // 再导入（保存 → 重新打开）：数据仍可读
  const { rootElement: reparsed } = await moddle.fromXML(exported);
  const reparsedProcess = reparsed.rootElements.find((element) => element.$type === 'bpmn:Process')!;
  const reparsedTask = reparsedProcess.flowElements.find((element) => element.$type === 'bpmn:UserTask')!;

  assert.equal((reparsedTask.extensionElements?.values ?? []).length, 5);
});

test('XML 往返：排他网关默认流以引用对象读写（回归）', async () => {
  // 回归：default 是 isReference 属性 —— 导入后是 SequenceFlow 对象；
  // 面板曾按字符串写入导致 saveXML 输出 default="undefined"，保存后默认流失效。
  const moddle = new BpmnModdle({});

  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  id="Definitions_1" targetNamespace="http://netbpm/bpmn">
  <bpmn:process id="p1" isExecutable="true">
    <bpmn:startEvent id="start" />
    <bpmn:exclusiveGateway id="gw" default="f2" />
    <bpmn:userTask id="t1" />
    <bpmn:userTask id="t2" />
    <bpmn:endEvent id="end" />
    <bpmn:sequenceFlow id="f1" sourceRef="gw" targetRef="t1" />
    <bpmn:sequenceFlow id="f2" sourceRef="gw" targetRef="t2" />
  </bpmn:process>
</bpmn:definitions>`;

  const { rootElement } = await moddle.fromXML(xml);
  const process = rootElement.rootElements.find((element) => element.$type === 'bpmn:Process')!;
  const gateway = process.flowElements.find((element) => element.id === 'gw')!;

  // 导入后引用被解析成 SequenceFlow 对象
  assert.equal(gateway.default.$type, 'bpmn:SequenceFlow');
  assert.equal(gateway.default.id, 'f2');

  // 面板的正确写法（写入引用对象）→ 序列化保留 default="f1"
  const flow1 = process.flowElements.find((element) => element.id === 'f1')!;
  gateway.default = flow1;

  const { xml: exported } = await moddle.toXML(rootElement, { format: true });
  assert.ok(exported.includes('default="f1"'));
});

test('监听器实现方式：切换类型时取值跟随（回归）', () => {
  // 回归：setType 原实现是「新键保留它自己原有的值」——从「类名」切到「委托表达式」
  // 时新键原本为空 ⇒ 三个属性全部清空 ⇒ 重渲染回落 class，表现为「切换没有发生变化」。
  const updates: Array<Record<string, unknown>> = [];
  const context = {
    modeling: {
      updateModdleProperties: (_e: unknown, _i: unknown, properties: Record<string, unknown>) => {
        updates.push(properties);
      },
    },
  } as never;
  const element = { id: "p1", type: "bpmn:Process", businessObject: { $type: "bpmn:Process", id: "p1" } } as never;
  const item = { $type: 'activiti:ExecutionListener', class: 'A' } as never;
  const t = (text: string) => text;

  const [typeSelect, valueInput] = implementationFields(element, context, t, item, (fn) => fn);

  // 初始：class 有值 ⇒ 显示「类名」，取值框带出 A
  assert.equal((typeSelect.props as { value: string }).value, 'class');
  assert.equal((valueInput.props as { value: string }).value, 'A');

  // 切到委托表达式 ⇒ 取值跟随迁移，三项互斥
  (typeSelect.props as { onChange: (event: { target: { value: string } }) => void }).onChange(
    { target: { value: 'delegateExpression' } });
  assert.deepEqual(updates.at(-1), { class: undefined, delegateExpression: 'A', expression: undefined });

  // 数据更新后再次渲染：显示「委托表达式」且取值不丢
  const item2 = { $type: 'activiti:ExecutionListener', delegateExpression: 'A' } as never;
  const [select2, input2] = implementationFields(element, context, t, item2, (fn) => fn);
  assert.equal((select2.props as { value: string }).value, 'delegateExpression');
  assert.equal((input2.props as { value: string }).value, 'A');
});
