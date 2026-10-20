import { describe, expect, it } from 'vitest';

import { auditActionColor, formatDuration, formatTime, formatValue } from './utils';

describe('formatTime', () => {
  it('空值显示占位符', () => {
    expect(formatTime(null)).toBe('—');
    expect(formatTime(undefined)).toBe('—');
    expect(formatTime('')).toBe('—');
  });

  it('非法时间原样返回', () => {
    expect(formatTime('not-a-date')).toBe('not-a-date');
  });

  it('UTC ISO 转本地时区可读格式（不含原始 T/Z 字符）', () => {
    const result = formatTime('2026-01-02T03:04:05Z');

    // 本地时区不同则日/时不同，这里只断言「被转换过」而不是原样输出
    expect(result).not.toBe('2026-01-02T03:04:05Z');
    expect(result).not.toBe('—');
    // zh-CN 格式形如 2026/1/2 11:04:05
    expect(result).toMatch(/2026\/1\/(1|2)/);
  });
});

describe('auditActionColor', () => {
  it('删除与清理类用红色', () => {
    expect(auditActionColor('deployment.delete')).toBe('red');
    expect(auditActionColor('history.archive')).toBe('red');
    expect(auditActionColor('audit.purge')).toBe('red');
    expect(auditActionColor('instance.delete')).toBe('red');
    expect(auditActionColor('instance.batch_delete')).toBe('red');
  });

  it('变量类用紫色', () => {
    expect(auditActionColor('variable.write')).toBe('purple');
    expect(auditActionColor('task_variable.write')).toBe('purple');
  });

  it('部署与定义类用 geekblue', () => {
    expect(auditActionColor('deployment.create')).toBe('geekblue');
    expect(auditActionColor('definition.suspend')).toBe('geekblue');
  });

  it('其余实例类用橙色、未知动作用蓝色', () => {
    expect(auditActionColor('instance.jump')).toBe('orange');
    expect(auditActionColor('model.history_prune')).toBe('blue');
    expect(auditActionColor('task.complete')).toBe('blue');
  });
});

describe('formatValue', () => {
  it('null / undefined 各有可读形态', () => {
    expect(formatValue(null)).toBe('null');
    expect(formatValue(undefined)).toBe('—');
  });

  it('对象 JSON 序列化、标量直接转字符串', () => {
    expect(formatValue({ a: 1 })).toBe('{"a":1}');
    expect(formatValue(42)).toBe('42');
    expect(formatValue(true)).toBe('true');
    expect(formatValue('text')).toBe('text');
  });
});

describe('formatDuration', () => {
  it('空值显示占位符', () => {
    expect(formatDuration(undefined)).toBe('—');
    expect(formatDuration(null)).toBe('—');
  });

  it('毫秒 / 秒 / 分 / 时分级展示', () => {
    expect(formatDuration(500)).toBe('500 ms');
    expect(formatDuration(1500)).toBe('1 秒');
    expect(formatDuration(90_000)).toBe('1 分 30 秒');
    expect(formatDuration(3_600_000 + 5 * 60_000)).toBe('1 时 5 分');
  });
});
