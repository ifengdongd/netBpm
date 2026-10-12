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

import NetBpmPropertiesProvider from '../src/designer/propertiesProvider';
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

  assert.deepEqual(groups.map((group) => group.id), ['netbpm-common', 'netbpm-sequence-flow']);
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

  assignee.setValue?.(element, '  gonzo  ');
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: 'gonzo' });

  // 清空 ⇒ undefined（不是空字符串）：否则 XML 会写出 activiti:assignee=""
  assignee.setValue?.(element, '   ');
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: undefined });

  assignee.setValue?.(element, undefined);
  assert.deepEqual(harness.updates.at(-1)?.properties, { assignee: undefined });
});

test('服务任务：三项实现方式互斥，切换时清空其余两项', () => {
  const harness = createHarness();
  const element = {
    id: 'notify',
    type: 'bpmn:ServiceTask',
    businessObject: { $type: 'bpmn:ServiceTask', id: 'notify', class: 'NetBpm.Demo.Notify' },
  };
  const group = groupById(groupsOf(harness, element), 'netbpm-service-task');
  const select = entryById(group, 'implementationType');

  // 当前实现方式 = class（与后端 ImplementationType 的优先级一致）
  assert.equal(select.getValue?.(element), 'class');

  // 切换到 delegateExpression：class 被清除，新项尚未填写 ⇒ 三项都不写
  select.setValue?.(element, 'delegateExpression');
  assert.deepEqual(harness.updates.at(-1)?.properties, {
    class: undefined,
    delegateExpression: undefined,
    expression: undefined,
  });

  // 切换回 class：保留原值
  select.setValue?.(element, 'class');
  assert.deepEqual(harness.updates.at(-1)?.properties, { class: 'NetBpm.Demo.Notify', delegateExpression: undefined, expression: undefined });
});

test('服务任务：未选择实现方式时给出提示条目', () => {
  const harness = createHarness();
  const element = { id: 'notify', type: 'bpmn:ServiceTask', businessObject: { $type: 'bpmn:ServiceTask', id: 'notify' } };

  const entries = groupById(groupsOf(harness, element), 'netbpm-service-task').entries.map((entry) => entry.id);

  assert.ok(entries.includes('note-notify-实现方式取值'), `实际条目：${entries.join(', ')}`);
});

test('默认流下拉：选项来自出流，取值是出流 id', () => {
  const harness = createHarness();
  const element = userTask({ outgoing: [{ id: 'f2', name: '同意' }, { id: 'f3' }] });
  const group = groupById(groupsOf(harness, element), 'netbpm-user-task');
  const defaultFlow = entryById(group, 'defaultFlow');

  assert.deepEqual(defaultFlow.options?.map((option) => option.value), ['', 'f2', 'f3']);

  defaultFlow.setValue?.(element, 'f2');
  assert.deepEqual(harness.updates.at(-1)?.properties, { default: 'f2' });

  defaultFlow.setValue?.(element, '');
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

  condition.setValue?.(element, '${amount > 100}');
  assert.deepEqual(harness.updates.at(-1)?.properties, {
    conditionExpression: { $type: 'bpmn:FormalExpression', body: '${amount > 100}' },
  });

  condition.setValue?.(element, '');
  assert.deepEqual(harness.updates.at(-1)?.properties, { conditionExpression: undefined });
});

test('并行网关：不提供默认流，只给出说明', () => {
  const harness = createHarness();
  const element = { id: 'fork', type: 'bpmn:ParallelGateway', businessObject: { $type: 'bpmn:ParallelGateway', id: 'fork' } };
  const group = groupById(groupsOf(harness, element), 'netbpm-parallel-gateway');

  assert.ok(group.entries[0].id.startsWith('note-'));
});

test('不支持的能力：出现监听器/事件定义时额外渲染提示组', () => {
  const harness = createHarness();
  const element = userTask({
    extensionElements: { values: [{ $type: 'activiti:TaskListener', event: 'create' }] },
    eventDefinitions: [{ $type: 'bpmn:TimerEventDefinition' }],
  });

  const group = groupById(groupsOf(harness, element), 'netbpm-unsupported');

  assert.equal(group.entries.length, 2);
  assert.ok(group.entries.every((entry) => entry.id.startsWith('note-')));
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
          { $type: 'bpmn:StartEvent', id: 'start', outgoing: [{ id: 'f1' }] },
          { $type: 'bpmn:UserTask', id: 'approve', incoming: [{ id: 'f1' }], outgoing: [] },
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
          { $type: 'bpmn:UserTask', id: 'approve', candidateGroups: 'managers', outgoing: [{ id: 'f1' }] },
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
          { $type: 'bpmn:ParallelGateway', id: 'fork', default: 'f1', incoming: [{ id: 'f0' }], outgoing: [{ id: 'f1' }] },
          {
            $type: 'bpmn:ExclusiveGateway',
            id: 'gw',
            incoming: [{ id: 'f1' }],
            outgoing: [
              { id: 'f2', conditionExpression: { body: '${a}' } },
              { id: 'f3', conditionExpression: { body: '${b}' } },
            ],
          },
        ],
      },
    ]),
  );

  assert.ok(issues.some((issue) => issue.elementId === 'fork' && issue.message.includes('不支持默认流')));
  assert.ok(issues.some((issue) => issue.elementId === 'gw' && issue.message.includes('默认流')));
});

test('校验：不支持的能力与事件定义都会拦下部署', () => {
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
            extensionElements: { values: [{ $type: 'activiti:Field', name: 'url' }] },
          },
          { $type: 'bpmn:StartEvent', id: 'start', eventDefinitions: [{ $type: 'bpmn:TimerEventDefinition' }] },
        ],
      },
    ]),
  );

  assert.ok(issues.some((issue) => issue.message.includes('字段注入')));
  assert.ok(issues.some((issue) => issue.message.includes('事件定义')));
  assert.ok(hasBlockingError(issues));
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
