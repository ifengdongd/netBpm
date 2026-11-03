import { describe, expect, it } from 'vitest';

import type { ExternalTaskView } from '../api/externalTasks';
import {
  canRetry,
  canUnlock,
  externalTaskStatusColor,
  externalTaskStatusLabel,
  externalTaskStatusOptions,
  isLockExpired,
  isTruncated,
  summarizeStatuses,
} from './externalTasks';

/** 构造一条外部任务；只覆盖断言用到的字段，其余给安全默认值。 */
function task(overrides: Partial<ExternalTaskView> = {}): ExternalTaskView {
  return {
    id: '9001',
    processInstanceId: '9000',
    executionId: '9002',
    processDefinitionId: 'demo:1:1',
    processDefinitionKey: 'demo',
    activityId: 'svc_invoice',
    activityName: '开票',
    topic: 'invoice',
    tenantId: 'default',
    businessKey: null,
    payload: { amount: 1000 },
    priority: 0,
    retries: 3,
    status: 'PENDING',
    workerId: null,
    lockExpiresAt: null,
    nextDueAt: null,
    errorMessage: null,
    errorDetails: null,
    createdAt: '2026-11-03T02:00:00Z',
    completedAt: null,
    ...overrides,
  };
}

describe('外部任务状态展示', () => {
  it('四种状态都有中文文案与配色', () => {
    expect(externalTaskStatusLabel('PENDING')).toBe('待领取');
    expect(externalTaskStatusLabel('DEAD_LETTER')).toBe('死信');
    expect(externalTaskStatusColor('LOCKED')).toBe('geekblue');
    expect(externalTaskStatusColor('DEAD_LETTER')).toBe('error');
  });

  it('未知状态原样显示，不吞掉信息', () => {
    expect(externalTaskStatusLabel('SOMETHING_NEW')).toBe('SOMETHING_NEW');
    expect(externalTaskStatusColor('SOMETHING_NEW')).toBe('default');
  });

  it('筛选下拉覆盖四种状态', () => {
    expect(externalTaskStatusOptions().map((option) => option.value)).toEqual([
      'PENDING',
      'LOCKED',
      'COMPLETED',
      'DEAD_LETTER',
    ]);
  });
});

describe('外部任务动作可用性', () => {
  it('只有死信能重投（与后端 409 external_task_not_dead_letter 同口径）', () => {
    expect(canRetry('DEAD_LETTER')).toBe(true);

    for (const status of ['PENDING', 'LOCKED', 'COMPLETED']) {
      expect(canRetry(status)).toBe(false);
    }
  });

  it('解锁要求已锁定且能拿到持锁者', () => {
    expect(canUnlock(task({ status: 'LOCKED', workerId: 'worker-01' }))).toBe(true);
    // 锁着但不知道持锁者：后端要求 workerId，前端不该给按钮
    expect(canUnlock(task({ status: 'LOCKED', workerId: null }))).toBe(false);
    expect(canUnlock(task({ status: 'LOCKED', workerId: '   ' }))).toBe(false);
    // 非锁定态没有锁可释放
    expect(canUnlock(task({ status: 'PENDING', workerId: 'worker-01' }))).toBe(false);
    expect(canUnlock(task({ status: 'DEAD_LETTER', workerId: 'worker-01' }))).toBe(false);
  });

  it('锁过期只影响提示，不影响解锁判定', () => {
    const now = Date.parse('2026-11-03T02:10:00Z');

    expect(isLockExpired(task({ lockExpiresAt: '2026-11-03T02:09:00Z' }), now)).toBe(true);
    expect(isLockExpired(task({ lockExpiresAt: '2026-11-03T02:11:00Z' }), now)).toBe(false);
    // 没锁过 / 时间不可解析：都当作没过期，避免误报红字
    expect(isLockExpired(task({ lockExpiresAt: null }), now)).toBe(false);
    expect(isLockExpired(task({ lockExpiresAt: 'not-a-date' }), now)).toBe(false);

    // 过期了仍然允许解锁：锁可能还没被别人重领
    expect(canUnlock(task({ status: 'LOCKED', workerId: 'worker-01', lockExpiresAt: '2026-11-03T02:09:00Z' }))).toBe(
      true,
    );
  });
});

describe('外部任务列表统计', () => {
  it('按状态计数，未识别状态不计入', () => {
    const summary = summarizeStatuses([
      task({ id: '1', status: 'PENDING' }),
      task({ id: '2', status: 'PENDING' }),
      task({ id: '3', status: 'LOCKED' }),
      task({ id: '4', status: 'DEAD_LETTER' }),
      task({ id: '5', status: 'WHATEVER' }),
    ]);

    expect(summary).toEqual({ PENDING: 2, LOCKED: 1, COMPLETED: 0, DEAD_LETTER: 1 });
  });

  it('达到上限时判定为被截断（提醒收窄筛选）', () => {
    const tasks = [task({ id: '1' }), task({ id: '2' })];

    expect(isTruncated(tasks, 200)).toBe(false);
    expect(isTruncated(tasks, 2)).toBe(true);
  });
});
