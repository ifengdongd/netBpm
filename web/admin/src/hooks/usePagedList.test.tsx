import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';

import { usePagedList, type PageState } from './usePagedList';
import type { PagedResponse } from '../api/types';

function createWrapper() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

const page1: PagedResponse<number> = { items: [1], total: 10, firstResult: 0, maxResults: 20 };
const page2: PagedResponse<number> = { items: [2, 22], total: 10, firstResult: 20, maxResults: 20 };

describe('usePagedList', () => {
  it('初始加载后填充 items / total / pagination', async () => {
    const fetcher = vi.fn(() => Promise.resolve(page1));
    const { result } = renderHook(() => usePagedList<number>(['test'], fetcher, () => {}), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(fetcher).toHaveBeenCalledWith({ current: 1, pageSize: 20 });
    expect(result.current.items).toEqual([1]);
    expect(result.current.total).toBe(10);
    expect(result.current.pagination.current).toBe(1);
    expect(result.current.pagination.total).toBe(10);
  });

  it('翻页触发新请求并更新数据', async () => {
    const fetcher = vi.fn((page: PageState) =>
      Promise.resolve(page.current === 1 ? page1 : page2),
    );
    const { result } = renderHook(() => usePagedList<number>(['test'], fetcher, () => {}), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.items).toEqual([1]));

    act(() => result.current.setPage({ current: 2, pageSize: 20 }));

    await waitFor(() => expect(result.current.items).toEqual([2, 22]));
    expect(fetcher).toHaveBeenLastCalledWith({ current: 2, pageSize: 20 });
    expect(result.current.pagination.current).toBe(2);
  });

  it('加载失败时只回调一次 onError', async () => {
    const onError = vi.fn();
    const fetcher = vi.fn(() => Promise.reject(new Error('boom')));

    renderHook(() => usePagedList<number>(['test'], fetcher, onError), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
    expect(onError).toHaveBeenCalledWith(expect.objectContaining({ message: 'boom' }));

    // 稍等确认不会重复提示（retry 已关，失败即终态）
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(onError).toHaveBeenCalledTimes(1);
  });

  it('快速翻页时旧页的迟到响应不会污染当前页数据', async () => {
    // 第 1 页的 promise 由测试手动控制返回时机
    let resolvePage1: (value: PagedResponse<number>) => void = () => {};
    const fetcher = vi.fn((page: PageState) =>
      page.current === 1
        ? new Promise<PagedResponse<number>>((resolve) => {
            resolvePage1 = resolve;
          })
        : Promise.resolve(page2),
    );

    const { result } = renderHook(() => usePagedList<number>(['test'], fetcher, () => {}), {
      wrapper: createWrapper(),
    });

    // 第 1 页挂起时直接翻到第 2 页
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1));
    act(() => result.current.setPage({ current: 2, pageSize: 20 }));

    await waitFor(() => expect(result.current.items).toEqual([2, 22]));

    // 第 1 页此刻才迟到返回：当前页码已是 2，数据不应被覆盖
    await act(async () => {
      resolvePage1(page1);
    });
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(result.current.page.current).toBe(2);
    expect(result.current.items).toEqual([2, 22]);
  });

  it('reload 按当前页码重新拉取', async () => {
    let payload: PagedResponse<number> = page1;
    const fetcher = vi.fn(() => Promise.resolve(payload));
    const { result } = renderHook(() => usePagedList<number>(['test'], fetcher, () => {}), {
      wrapper: createWrapper(),
    });

    await waitFor(() => expect(result.current.items).toEqual([1]));

    payload = page2;
    act(() => result.current.reload());

    await waitFor(() => expect(result.current.items).toEqual([2, 22]));
    expect(fetcher).toHaveBeenCalledTimes(2);
  });
});
