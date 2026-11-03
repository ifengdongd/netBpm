import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App as AntApp } from 'antd';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

vi.mock('../api/externalTasks', () => ({
  EXTERNAL_TASK_MAX_RESULTS: 200,
  externalTasksApi: {
    list: vi.fn(),
    retry: vi.fn(),
    unlock: vi.fn(),
  },
}));

import { externalTasksApi } from '../api/externalTasks';
import type { ExternalTaskView } from '../api/externalTasks';

import ExternalTasksPage from './ExternalTasksPage';

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

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    (
      <QueryClientProvider client={client}>
        <AntApp>
          <MemoryRouter initialEntries={['/external-tasks']}>
            <ExternalTasksPage />
          </MemoryRouter>
        </AntApp>
      </QueryClientProvider>
    ) as ReactNode,
  );
}

describe('外部任务页', () => {
  // 没有开 vitest globals，RTL 的自动 cleanup 不会注册：不手动清会串到下一个用例的 DOM
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  beforeAll(() => {
    // antd 的响应式栅格 / Table 会读 matchMedia，jsdom 没有实现
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => false,
      }),
    });
  });

  it('按状态给出统计，并且只给死信「重投」、只给锁定任务「解锁」', async () => {
    vi.mocked(externalTasksApi.list).mockResolvedValue([
      task({ id: '1', status: 'PENDING' }),
      task({ id: '2', status: 'LOCKED', workerId: 'worker-01' }),
      task({ id: '3', status: 'DEAD_LETTER', retries: 0, errorMessage: '下游超时' }),
    ]);
    vi.mocked(externalTasksApi.retry).mockResolvedValue(undefined);
    vi.mocked(externalTasksApi.unlock).mockResolvedValue(undefined);

    renderPage();

    // 三条任务的 topic 都在（说明表格渲染了数据）
    await waitFor(() => expect(screen.getAllByText('invoice').length).toBe(3));

    expect(screen.getByText(/待领取 1 条 \/ 已锁定 1 条 \/ 死信 1 条/)).toBeTruthy();
    expect(screen.getByText('下游超时')).toBeTruthy();

    // 动作按钮数量就是状态机的体现：死信 1 个重投，锁定 1 个解锁
    expect(screen.getAllByRole('button', { name: /重投/ })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: /解锁/ })).toHaveLength(1);
  });

  it('重投走 retry 端点', async () => {
    vi.mocked(externalTasksApi.list).mockResolvedValue([task({ id: '77', status: 'DEAD_LETTER', retries: 0 })]);
    vi.mocked(externalTasksApi.retry).mockResolvedValue(undefined);

    renderPage();

    const retryButton = await screen.findByRole('button', { name: /重投/ });
    fireEvent.click(retryButton);

    // Popconfirm 里的确认按钮（与表格里的触发按钮文案区分开，避免歧义）
    const confirm = await screen.findByRole('button', { name: '确认重投' });
    fireEvent.click(confirm);

    await waitFor(() => expect(externalTasksApi.retry).toHaveBeenCalledWith('77'));
  });
});
